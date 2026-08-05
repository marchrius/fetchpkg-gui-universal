namespace FetchPkg.Core;

internal sealed record DownloadSegment(
    PackagePiece Piece,
    long RelativeOffset,
    long Length)
{
    public long SourceStart => RelativeOffset;
    public long SourceEnd => checked(RelativeOffset + Length - 1);
    public long DestinationOffset => checked(Piece.Offset + RelativeOffset);
    public string Key => $"{Piece.Index}:{RelativeOffset}:{Length}";
    public bool IsWholePiece => RelativeOffset == 0 && Length == Piece.Length;
}
