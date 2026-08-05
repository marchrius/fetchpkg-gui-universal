using Microsoft.Win32.SafeHandles;
using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace FetchPkg.Core;

public sealed class PackageDownloader
{
    private readonly HttpClient _http;

    public PackageDownloader(HttpClient http)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
    }

    public async Task<DownloadResult> DownloadAsync(
        Uri source,
        string outputPath,
        DownloadOptions options,
        IProgress<DownloadProgress>? progress = null,
        PauseTokenSource? pause = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentNullException.ThrowIfNull(options);

        ValidateOptions(options);
        pause ??= new PauseTokenSource();

        var stopwatch = Stopwatch.StartNew();
        progress?.Report(new DownloadProgress(DownloadStage.Preparing, 0, 0, 0, 0, "Loading manifest"));

        var manifest = await new ManifestClient(_http).LoadAsync(source, cancellationToken).ConfigureAwait(false);
        var segments = BuildSegments(manifest.Pieces, options.SegmentSizeBytes);
        var transferSize = manifest.Pieces.Sum(piece => piece.Length);

        var fullOutputPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullOutputPath)!);

        var resumeCandidate = options.Resume &&
                              File.Exists(fullOutputPath) &&
                              new FileInfo(fullOutputPath).Length == manifest.OriginalFileSize;

        var state = await DownloadStateStore.OpenAsync(
            fullOutputPath,
            manifest,
            resumeCandidate,
            cancellationToken).ConfigureAwait(false);
        var usableExistingFile = resumeCandidate && state.WasResumed;

        using var handle = File.OpenHandle(
            fullOutputPath,
            usableExistingFile ? FileMode.Open : FileMode.Create,
            FileAccess.ReadWrite,
            FileShare.Read,
            FileOptions.Asynchronous | FileOptions.RandomAccess);

        var sparseEnabled = (File.GetAttributes(fullOutputPath) & FileAttributes.SparseFile) != 0;
        if (options.UseSparseFile && !sparseEnabled)
        {
            sparseEnabled = SparseFile.TryEnable(handle, out _);
        }

        RandomAccess.SetLength(handle, manifest.OriginalFileSize);
        if (!usableExistingFile)
        {
            await state.MarkOutputInitializedAsync(cancellationToken).ConfigureAwait(false);
        }

        long completedBytes = usableExistingFile ? state.CountCompletedBytes(segments) : 0;
        var reporter = new ProgressReporter(progress, transferSize, stopwatch, completedBytes);
        reporter.Report(DownloadStage.Downloading, "Starting", force: true);

        var pending = segments.Where(segment => !state.IsCompleted(segment)).ToArray();
        await DownloadSegmentsAsync(
            pending,
            handle,
            state,
            options,
            pause,
            reporter,
            cancellationToken).ConfigureAwait(false);

        if (options.VerifyPieceHashes)
        {
            reporter.Report(DownloadStage.Verifying, "Verifying piece hashes", force: true);
            foreach (var piece in manifest.Pieces)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await pause.WaitWhilePausedAsync(cancellationToken).ConfigureAwait(false);

                if (string.IsNullOrWhiteSpace(piece.HashHex) || piece.HashAlgorithm is null)
                {
                    continue;
                }

                if (await VerifyPieceAsync(handle, piece, cancellationToken).ConfigureAwait(false))
                {
                    continue;
                }

                await state.ClearPieceAsync(piece.Index, cancellationToken).ConfigureAwait(false);
                reporter.AdjustCompleted(-piece.Length);
                reporter.Report(DownloadStage.Downloading, $"Retrying piece {piece.Index + 1}", force: true);

                var retrySegments = segments.Where(segment => segment.Piece.Index == piece.Index).ToArray();
                await DownloadSegmentsAsync(
                    retrySegments,
                    handle,
                    state,
                    options,
                    pause,
                    reporter,
                    cancellationToken).ConfigureAwait(false);

                reporter.Report(DownloadStage.Verifying, $"Rechecking piece {piece.Index + 1}", force: true);
                if (!await VerifyPieceAsync(handle, piece, cancellationToken).ConfigureAwait(false))
                {
                    throw new InvalidDataException(
                        $"Hash verification failed for piece {piece.Index + 1} after a complete retry.");
                }
            }
        }

        RandomAccess.FlushToDisk(handle);
        await state.CompleteAsync().ConfigureAwait(false);
        reporter.SetCompleted(transferSize);
        reporter.Report(DownloadStage.Completed, Path.GetFileName(fullOutputPath), force: true);
        stopwatch.Stop();

        return new DownloadResult(
            fullOutputPath,
            manifest.OriginalFileSize,
            manifest.Pieces.Count,
            stopwatch.Elapsed,
            sparseEnabled,
            state.WasResumed);
    }

    private async Task DownloadSegmentsAsync(
        IReadOnlyCollection<DownloadSegment> segments,
        SafeFileHandle handle,
        DownloadStateStore state,
        DownloadOptions options,
        PauseTokenSource pause,
        ProgressReporter reporter,
        CancellationToken cancellationToken)
    {
        if (segments.Count == 0)
        {
            return;
        }

        await Parallel.ForEachAsync(
            segments,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = options.MaxConnections,
                CancellationToken = cancellationToken
            },
            async (segment, token) =>
            {
                await pause.WaitWhilePausedAsync(token).ConfigureAwait(false);
                reporter.SegmentStarted(Path.GetFileName(segment.Piece.Url.AbsolutePath));
                try
                {
                    await DownloadWithRetryAsync(segment, handle, options, pause, reporter, token)
                        .ConfigureAwait(false);
                    await state.MarkCompletedAsync(segment, token).ConfigureAwait(false);
                }
                finally
                {
                    reporter.SegmentFinished();
                }
            }).ConfigureAwait(false);
    }

    private async Task DownloadWithRetryAsync(
        DownloadSegment segment,
        SafeFileHandle handle,
        DownloadOptions options,
        PauseTokenSource pause,
        ProgressReporter reporter,
        CancellationToken cancellationToken)
    {
        Exception? lastError = null;
        for (var attempt = 1; attempt <= options.RetryCount; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await pause.WaitWhilePausedAsync(cancellationToken).ConfigureAwait(false);

            long attemptBytes = 0;
            try
            {
                await DownloadSegmentOnceAsync(
                    segment,
                    handle,
                    pause,
                    bytes =>
                    {
                        attemptBytes += bytes;
                        reporter.AddBytes(bytes, Path.GetFileName(segment.Piece.Url.AbsolutePath));
                    },
                    cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException)
            {
                lastError = ex;
                reporter.AdjustCompleted(-attemptBytes);
                if (attempt == options.RetryCount)
                {
                    break;
                }

                var delay = TimeSpan.FromSeconds(Math.Min(8, Math.Pow(2, attempt - 1)));
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }

        throw new IOException(
            $"Failed to download piece {segment.Piece.Index + 1}, range " +
            $"{segment.SourceStart}-{segment.SourceEnd} after {options.RetryCount} attempts.",
            lastError);
    }

    private async Task DownloadSegmentOnceAsync(
        DownloadSegment segment,
        SafeFileHandle handle,
        PauseTokenSource pause,
        Action<int> onBytes,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, segment.Piece.Url);
        request.Headers.AcceptEncoding.Clear();
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("identity"));
        var expectPartialContent = segment.Piece.SupportsRanges && !segment.IsWholePiece;

        if (segment.Piece.SupportsRanges)
        {
            request.Headers.Range = new RangeHeaderValue(segment.SourceStart, segment.SourceEnd);
        }
        else if (!segment.IsWholePiece)
        {
            throw new InvalidDataException("A non-range server cannot download a split segment.");
        }

        using var response = await _http.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        await HttpResponseErrors.EnsureSuccessAsync(response, request, cancellationToken)
            .ConfigureAwait(false);

        if (expectPartialContent)
        {
            var contentRange = response.Content.Headers.ContentRange;
            var validRange = contentRange is not null &&
                             contentRange.From == segment.SourceStart &&
                             contentRange.To == segment.SourceEnd &&
                             (contentRange.Length is null || contentRange.Length == segment.Piece.Length);
            if (response.StatusCode != HttpStatusCode.PartialContent || !validRange)
            {
                throw new InvalidDataException(
                    $"The server returned an invalid HTTP Content-Range for {segment.Piece.Url.Host}.");
            }
        }

        if (response.Content.Headers.ContentLength is long contentLength && contentLength != segment.Length)
        {
            if (!(segment.IsWholePiece && !segment.Piece.SupportsRanges && contentLength == segment.Piece.Length))
            {
                throw new InvalidDataException(
                    $"Unexpected response length {contentLength}; expected {segment.Length}.");
            }
        }

        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
        try
        {
            long written = 0;
            while (written < segment.Length)
            {
                await pause.WaitWhilePausedAsync(cancellationToken).ConfigureAwait(false);
                var requested = (int)Math.Min(buffer.Length, segment.Length - written);
                var read = await input.ReadAsync(buffer.AsMemory(0, requested), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                await RandomAccess.WriteAsync(
                    handle,
                    buffer.AsMemory(0, read),
                    segment.DestinationOffset + written,
                    cancellationToken).ConfigureAwait(false);
                written += read;
                onBytes(read);
            }

            if (written != segment.Length)
            {
                throw new EndOfStreamException(
                    $"Segment ended after {written} bytes; expected {segment.Length}.");
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task<bool> VerifyPieceAsync(
        SafeFileHandle handle,
        PackagePiece piece,
        CancellationToken cancellationToken)
    {
        if (piece.HashAlgorithm is null || string.IsNullOrWhiteSpace(piece.HashHex))
        {
            return true;
        }

        using var hash = IncrementalHash.CreateHash(piece.HashAlgorithm.Value);
        var buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
        try
        {
            long readTotal = 0;
            while (readTotal < piece.Length)
            {
                var requested = (int)Math.Min(buffer.Length, piece.Length - readTotal);
                var read = await RandomAccess.ReadAsync(
                    handle,
                    buffer.AsMemory(0, requested),
                    piece.Offset + readTotal,
                    cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    return false;
                }

                hash.AppendData(buffer, 0, read);
                readTotal += read;
            }

            var actual = Convert.ToHexString(hash.GetHashAndReset());
            return actual.Equals(piece.HashHex, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static IReadOnlyList<DownloadSegment> BuildSegments(
        IReadOnlyList<PackagePiece> pieces,
        long segmentSize)
    {
        var segments = new List<DownloadSegment>();
        foreach (var piece in pieces)
        {
            if (!piece.SupportsRanges || piece.Length <= segmentSize)
            {
                segments.Add(new DownloadSegment(piece, 0, piece.Length));
                continue;
            }

            for (long offset = 0; offset < piece.Length; offset += segmentSize)
            {
                segments.Add(new DownloadSegment(piece, offset, Math.Min(segmentSize, piece.Length - offset)));
            }
        }

        return segments;
    }

    private static void ValidateOptions(DownloadOptions options)
    {
        if (options.MaxConnections is < 1 or > 64)
        {
            throw new ArgumentOutOfRangeException(nameof(options.MaxConnections), "Connections must be between 1 and 64.");
        }

        if (options.SegmentSizeBytes < 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(options.SegmentSizeBytes), "Segment size must be at least 1 MiB.");
        }

        if (options.RetryCount is < 1 or > 10)
        {
            throw new ArgumentOutOfRangeException(nameof(options.RetryCount), "Retry count must be between 1 and 10.");
        }
    }

    private sealed class ProgressReporter
    {
        private readonly IProgress<DownloadProgress>? _progress;
        private readonly long _total;
        private readonly Stopwatch _stopwatch;
        private long _completed;
        private int _activeSegments;
        private long _lastReportTimestamp;

        public ProgressReporter(
            IProgress<DownloadProgress>? progress,
            long total,
            Stopwatch stopwatch,
            long completed)
        {
            _progress = progress;
            _total = total;
            _stopwatch = stopwatch;
            _completed = completed;
        }

        public void AddBytes(int bytes, string? currentItem)
        {
            Interlocked.Add(ref _completed, bytes);
            Report(DownloadStage.Downloading, currentItem, force: false);
        }

        public void AdjustCompleted(long bytes)
        {
            Interlocked.Add(ref _completed, bytes);
        }

        public void SetCompleted(long value)
        {
            Interlocked.Exchange(ref _completed, value);
        }

        public void SegmentStarted(string? currentItem)
        {
            Interlocked.Increment(ref _activeSegments);
            Report(DownloadStage.Downloading, currentItem, force: true);
        }

        public void SegmentFinished()
        {
            Interlocked.Decrement(ref _activeSegments);
        }

        public void Report(DownloadStage stage, string? currentItem, bool force)
        {
            if (_progress is null)
            {
                return;
            }

            var now = Stopwatch.GetTimestamp();
            var previous = Interlocked.Read(ref _lastReportTimestamp);
            if (!force && previous != 0 && Stopwatch.GetElapsedTime(previous, now) < TimeSpan.FromMilliseconds(250))
            {
                return;
            }

            Interlocked.Exchange(ref _lastReportTimestamp, now);
            var completed = Math.Max(0, Interlocked.Read(ref _completed));
            var seconds = Math.Max(0.001, _stopwatch.Elapsed.TotalSeconds);
            _progress.Report(new DownloadProgress(
                stage,
                completed,
                _total,
                completed / seconds,
                Math.Max(0, Volatile.Read(ref _activeSegments)),
                currentItem));
        }
    }
}
