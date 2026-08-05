using System.Security.Cryptography;

namespace FetchPkg.Core;

public sealed record PackageManifest(
    Uri Source,
    long OriginalFileSize,
    IReadOnlyList<PackagePiece> Pieces);

public sealed record PackagePiece(
    int Index,
    Uri Url,
    long Offset,
    long Length,
    string? HashHex,
    HashAlgorithmName? HashAlgorithm,
    bool SupportsRanges);

public sealed record DownloadOptions
{
    public int MaxConnections { get; init; } = 8;
    public long SegmentSizeBytes { get; init; } = 16L * 1024 * 1024;
    public int RetryCount { get; init; } = 3;
    public bool UseSparseFile { get; init; } = true;
    public bool Resume { get; init; } = true;
    public bool VerifyPieceHashes { get; init; } = true;
    public bool IgnoreSslErrors { get; init; }
    public string? ProxyUrl { get; init; }
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromMinutes(10);
}

public enum DownloadStage
{
    Preparing,
    Downloading,
    Verifying,
    Completed
}

public sealed record DownloadProgress(
    DownloadStage Stage,
    long BytesCompleted,
    long TotalBytes,
    double BytesPerSecond,
    int ActiveSegments,
    string? CurrentItem)
{
    public double Percent => TotalBytes <= 0 ? 0 : Math.Clamp(100.0 * BytesCompleted / TotalBytes, 0, 100);

    public TimeSpan? EstimatedRemaining =>
        BytesPerSecond <= 0 || TotalBytes <= BytesCompleted
            ? null
            : TimeSpan.FromSeconds((TotalBytes - BytesCompleted) / BytesPerSecond);
}

public sealed record DownloadResult(
    string OutputPath,
    long LogicalSize,
    int PieceCount,
    TimeSpan Elapsed,
    bool SparseFileEnabled,
    bool Resumed);
