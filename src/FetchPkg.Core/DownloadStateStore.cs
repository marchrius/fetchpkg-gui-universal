using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FetchPkg.Core;

internal sealed class DownloadStateStore
{
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DownloadStateDocument _document;

    private DownloadStateStore(string path, DownloadStateDocument document)
    {
        _path = path;
        _document = document;
    }

    public bool WasResumed { get; private init; }

    public static async Task<DownloadStateStore> OpenAsync(
        string outputPath,
        PackageManifest manifest,
        bool resume,
        CancellationToken cancellationToken)
    {
        var statePath = outputPath + ".fetchpkg.state.json";
        var fingerprint = ComputeManifestFingerprint(manifest);
        if (resume && File.Exists(statePath))
        {
            try
            {
                await using var stream = File.OpenRead(statePath);
                var existing = await JsonSerializer.DeserializeAsync<DownloadStateDocument>(
                    stream,
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                if (existing is not null &&
                    existing.Version == DownloadStateDocument.CurrentVersion &&
                    string.Equals(existing.ManifestUrl, manifest.Source.AbsoluteUri, StringComparison.Ordinal) &&
                    existing.TotalSize == manifest.OriginalFileSize &&
                    existing.OutputInitialized &&
                    existing.CompletedSegments is not null &&
                    string.Equals(existing.ManifestFingerprint, fingerprint, StringComparison.Ordinal))
                {
                    return new DownloadStateStore(statePath, existing) { WasResumed = true };
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                // Invalid or stale state is replaced below.
            }
        }

        var document = new DownloadStateDocument
        {
            Version = DownloadStateDocument.CurrentVersion,
            ManifestUrl = manifest.Source.AbsoluteUri,
            ManifestFingerprint = fingerprint,
            TotalSize = manifest.OriginalFileSize,
            OutputInitialized = false,
            CompletedSegments = new HashSet<string>(StringComparer.Ordinal)
        };
        var store = new DownloadStateStore(statePath, document);
        await store.SaveAsync(cancellationToken).ConfigureAwait(false);
        return store;
    }

    public bool IsCompleted(DownloadSegment segment)
    {
        lock (_document.CompletedSegments)
        {
            return _document.CompletedSegments.Contains(segment.Key);
        }
    }

    public long CountCompletedBytes(IEnumerable<DownloadSegment> segments)
    {
        lock (_document.CompletedSegments)
        {
            return segments.Where(s => _document.CompletedSegments.Contains(s.Key)).Sum(s => s.Length);
        }
    }

    public async Task MarkOutputInitializedAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _document.OutputInitialized = true;
            await SaveCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task MarkCompletedAsync(DownloadSegment segment, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _document.CompletedSegments.Add(segment.Key);
            await SaveCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ClearPieceAsync(int pieceIndex, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _document.CompletedSegments.RemoveWhere(key => key.StartsWith(pieceIndex + ":", StringComparison.Ordinal));
            await SaveCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task CompleteAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (File.Exists(_path))
            {
                File.Delete(_path);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string ComputeManifestFingerprint(PackageManifest manifest)
    {
        var text = new StringBuilder();
        text.Append(manifest.Source.AbsoluteUri).Append('\n')
            .Append(manifest.OriginalFileSize).Append('\n');

        foreach (var piece in manifest.Pieces.OrderBy(piece => piece.Index))
        {
            text.Append(piece.Index).Append('|')
                .Append(piece.Url.AbsoluteUri).Append('|')
                .Append(piece.Offset).Append('|')
                .Append(piece.Length).Append('|')
                .Append(piece.HashHex ?? string.Empty).Append('\n');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SaveCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task SaveCoreAsync(CancellationToken cancellationToken)
    {
        var tempPath = _path + ".tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        await using (var stream = new FileStream(
                         tempPath,
                         FileMode.Create,
                         FileAccess.Write,
                         FileShare.None,
                         16 * 1024,
                         FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await JsonSerializer.SerializeAsync(stream, _document, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        File.Move(tempPath, _path, overwrite: true);
    }

    private sealed class DownloadStateDocument
    {
        public const int CurrentVersion = 2;

        public int Version { get; set; }
        public string ManifestUrl { get; set; } = string.Empty;
        public string ManifestFingerprint { get; set; } = string.Empty;
        public long TotalSize { get; set; }
        public bool OutputInitialized { get; set; }
        public HashSet<string> CompletedSegments { get; set; } = new(StringComparer.Ordinal);
    }
}
