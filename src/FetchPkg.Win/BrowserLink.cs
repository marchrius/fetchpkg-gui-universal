namespace FetchPkg.Win;

internal sealed record BrowserLink(string Text, string Url);

internal sealed class PackageLinksSelectedEventArgs : EventArgs
{
    public PackageLinksSelectedEventArgs(IReadOnlyList<Uri> links)
    {
        Links = links;
    }

    public IReadOnlyList<Uri> Links { get; }
}
