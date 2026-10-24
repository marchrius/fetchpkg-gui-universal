using FetchPkg.Core;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

// No test-framework packages or external servers: exercise the real downloader
// with deterministic HTTP responses and real filesystem writes on each OS.
var root = Path.Combine(Path.GetTempPath(), "fetchpkg-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    var payload = new byte[2 * 1024 * 1024 + 123];
    new Random(42).NextBytes(payload);
    var options = new DownloadOptions { MaxConnections = 3, SegmentSizeBytes = 1024 * 1024, RetryCount = 1 };
    var source = new Uri("https://test.invalid/direct.pkg");

    // Ranged, out-of-order writes produce a byte-for-byte complete package.
    using (var handler = new FixtureHandler(payload))
    using (var http = new HttpClient(handler))
    {
        var path = Path.Combine(root, "direct.pkg");
        var result = await new PackageDownloader(http).DownloadAsync(source, path, options);
        Check(File.ReadAllBytes(path).SequenceEqual(payload), "parallel range download");
        Check(result.LogicalSize == payload.Length && !result.Resumed, "direct result metadata");
        Check(handler.Ranges.Count == 3, "three segments requested");
        Check(!File.Exists(path + ".fetchpkg.state.json"), "successful download removes resume state");
        if (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())
            Check(result.SparseFileEnabled, "Unix sparse strategy selected");
    }

    // Cancel at the second range, after the first has been durably recorded.
    using (var handler = new FixtureHandler(payload))
    using (var http = new HttpClient(handler))
    using (var cancel = new CancellationTokenSource())
    {
        handler.BeforeRange = from => { if (from >= 1024 * 1024) cancel.Cancel(); };
        var path = Path.Combine(root, "resume.pkg");
        var downloader = new PackageDownloader(http);
        try
        {
            await downloader.DownloadAsync(source, path, options with { MaxConnections = 1 }, cancellationToken: cancel.Token);
            throw new Exception("Expected cancellation.");
        }
        catch (OperationCanceledException) { }
        using (var state = JsonDocument.Parse(File.ReadAllText(path + ".fetchpkg.state.json")))
            Check(state.RootElement.GetProperty("CompletedSegments").GetArrayLength() == 1, "cancel preserves completed segment");
        handler.BeforeRange = null;
        handler.Ranges.Clear();
        var result = await downloader.DownloadAsync(source, path, options with { MaxConnections = 1 });
        Check(result.Resumed, "resume recognized");
        Check(handler.Ranges.All(from => from >= 1024 * 1024), "resume skips completed range");
        Check(File.ReadAllBytes(path).SequenceEqual(payload), "resumed bytes match");
    }

    // Two pieces use SHA-1/SHA-256 and leave a zero-filled hole between them.
    // Corrupt one transfer; verification must retry only that piece.
    using (var handler = new FixtureHandler(payload) { CorruptOnce = true })
    using (var http = new HttpClient(handler))
    {
        var path = Path.Combine(root, "manifest.pkg");
        var result = await new PackageDownloader(http).DownloadAsync(new Uri("https://test.invalid/manifest.json"), path, options);
        var output = File.ReadAllBytes(path);
        Check(result.PieceCount == 2 && output.Length == payload.Length + 4096, "manifest logical size");
        Check(output.Take(1024).SequenceEqual(payload.Take(1024)), "SHA-1 piece repaired");
        Check(output.Skip(1024).Take(4096).All(b => b == 0), "unwritten sparse gap reads as zero");
        Check(output.Skip(5120).SequenceEqual(payload.Skip(1024)), "SHA-256 piece merged at offset");
        Check(handler.FirstPieceRequests == 2, "hash mismatch retries failed piece once");
    }

    // Reject a server that returns bytes for the wrong range.
    using (var handler = new FixtureHandler(payload) { InvalidRange = true })
    using (var http = new HttpClient(handler))
    {
        try
        {
            await new PackageDownloader(http).DownloadAsync(source, Path.Combine(root, "bad.pkg"), options);
            throw new Exception("Expected invalid Content-Range failure.");
        }
        catch (IOException ex) when (ex.InnerException is InvalidDataException) { }
    }

    // Pausing prevents transfers, and cancellation releases the pause wait.
    using (var handler = new FixtureHandler(payload))
    using (var http = new HttpClient(handler))
    using (var cancel = new CancellationTokenSource())
    {
        var pause = new PauseTokenSource();
        pause.Pause();
        var prepared = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var progress = new InlineProgress(p => { if (p.Stage == DownloadStage.Downloading) prepared.TrySetResult(); });
        var task = new PackageDownloader(http).DownloadAsync(source, Path.Combine(root, "paused.pkg"), options, progress, pause, cancel.Token);
        await prepared.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Check(handler.Ranges.IsEmpty, "pause blocks HTTP transfers");
        cancel.Cancel();
        try { await task; throw new Exception("Expected paused cancellation."); }
        catch (OperationCanceledException) { }
    }

    var sc = new Uri("https://test.invalid/app/info/revision/f_hash/TITLE_sc.pkg?token=123");
    Check(ManifestUrl.Normalize(sc).AbsoluteUri == "https://test.invalid/app/info/revision/f_hash/TITLE.json?token=123", "SC conversion preserves revision, hash and query");
    var piece = new Uri("https://test.invalid/app/pkg/revision/f_hash/TITLE_0.pkg");
    Check(ManifestUrl.Normalize(piece) == piece, "PS5 piece manifest never guessed");
    Console.WriteLine("All FetchPkg smoke tests passed.");
}
finally { Directory.Delete(root, recursive: true); }

static void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAILED: " + name);
    Console.WriteLine("PASS: " + name);
}

sealed class InlineProgress(Action<DownloadProgress> report) : IProgress<DownloadProgress>
{
    public void Report(DownloadProgress value) => report(value);
}

sealed class FixtureHandler(byte[] payload) : HttpMessageHandler
{
    public ConcurrentBag<long> Ranges { get; } = new();
    public Action<long>? BeforeRange { get; set; }
    public bool CorruptOnce { get; set; }
    public bool InvalidRange { get; set; }
    public int FirstPieceRequests;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = request.RequestUri!.AbsolutePath;
        if (path.EndsWith(".json"))
        {
            var json = JsonSerializer.Serialize(new
            {
                originalFileSize = payload.Length + 4096,
                pieces = new[]
                {
                    new { url = "https://test.invalid/first.pkg", fileSize = 1024, fileOffset = 0,
                        hashValue = Convert.ToHexString(SHA1.HashData(payload.AsSpan(0, 1024))) },
                    new { url = "https://test.invalid/second.pkg", fileSize = payload.Length - 1024, fileOffset = 5120,
                        hashValue = Convert.ToHexString(SHA256.HashData(payload.AsSpan(1024))) }
                }
            });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        }
        var bytes = path switch
        {
            "/first.pkg" => payload[..1024],
            "/second.pkg" => payload[1024..],
            _ => payload
        };
        if (request.Method == HttpMethod.Head)
        {
            var head = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
            head.Content.Headers.ContentLength = bytes.Length;
            head.Headers.AcceptRanges.Add("bytes");
            return Task.FromResult(head);
        }
        var range = request.Headers.Range?.Ranges.Single();
        var from = range?.From ?? 0;
        var to = range?.To ?? bytes.Length - 1;
        BeforeRange?.Invoke(from);
        cancellationToken.ThrowIfCancellationRequested();
        Ranges.Add(from);
        var content = bytes[(int)from..((int)to + 1)];
        if (path == "/first.pkg" && Interlocked.Increment(ref FirstPieceRequests) == 1 && CorruptOnce)
            content[0] ^= 0xFF;
        var response = new HttpResponseMessage(range is null ? HttpStatusCode.OK : HttpStatusCode.PartialContent)
        {
            Content = new ByteArrayContent(content)
        };
        if (range is not null)
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(InvalidRange ? from + 1 : from, InvalidRange ? to + 1 : to, bytes.Length + (InvalidRange ? 1 : 0));
        return Task.FromResult(response);
    }
}
