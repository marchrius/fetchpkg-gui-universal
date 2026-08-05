namespace FetchPkg.Core;

public static class ManifestUrl
{
    private const string Ps5AppInfoMarker = "/app/info/";
    private const string Ps5AppPackageMarker = "/app/pkg/";

    public static Uri Normalize(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return Normalize(new Uri(value.Trim(), UriKind.Absolute));
    }

    /// <summary>
    /// Converts only URLs whose manifest location can be derived without guessing.
    /// PS5 application PKGs under /app/pkg/ use a different revision and hash path
    /// than the JSON under /app/info/, so those URLs are deliberately left intact.
    /// </summary>
    public static Uri Normalize(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        return TryGetManifestUri(uri, out var manifestUri) ? manifestUri : uri;
    }

    public static bool TryGetManifestUri(Uri source, out Uri manifestUri)
    {
        ArgumentNullException.ThrowIfNull(source);

        var path = source.AbsolutePath;
        if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            manifestUri = source;
            return true;
        }

        // PS5 _sc.pkg and its JSON manifest are siblings in the same /app/info/
        // directory and share the same revision/hash. This conversion is exact.
        if (path.EndsWith("_sc.pkg", StringComparison.OrdinalIgnoreCase))
        {
            manifestUri = ReplacePathSuffix(source, "_sc.pkg", ".json");
            return true;
        }

        // The original fetchpkg also accepts PS4 numbered pieces and DP links.
        // On the PS4 ppkgo layout the JSON really is a sibling of the PKG.
        if (UsesLegacySiblingManifestLayout(source))
        {
            if (path.EndsWith("-DP.pkg", StringComparison.OrdinalIgnoreCase))
            {
                manifestUri = ReplacePathSuffix(source, "-DP.pkg", ".json");
                return true;
            }

            var chunkSuffixLength = GetNumericChunkSuffixLength(path);
            if (chunkSuffixLength > 0)
            {
                manifestUri = ReplacePathTail(source, chunkSuffixLength, ".json");
                return true;
            }

            if (path.EndsWith(".pkg", StringComparison.OrdinalIgnoreCase))
            {
                manifestUri = ReplacePathSuffix(source, ".pkg", ".json");
                return true;
            }
        }

        manifestUri = null!;
        return false;
    }

    public static bool IsPs5ApplicationPackage(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        return uri.AbsolutePath.Contains(Ps5AppPackageMarker, StringComparison.OrdinalIgnoreCase) &&
               uri.AbsolutePath.EndsWith(".pkg", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsPs5ApplicationInfo(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        return uri.AbsolutePath.Contains(Ps5AppInfoMarker, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsNumberedPackagePiece(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        return GetNumericChunkSuffixLength(uri.AbsolutePath) > 0;
    }

    public static string SuggestFileName(Uri sourceUri)
    {
        ArgumentNullException.ThrowIfNull(sourceUri);
        var normalized = Normalize(sourceUri);
        var name = Path.GetFileName(Uri.UnescapeDataString(normalized.AbsolutePath));
        if (string.IsNullOrWhiteSpace(name))
        {
            return "package.pkg";
        }

        if (name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^5] + ".pkg";
        }
        else if (name.EndsWith("_sc.pkg", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^7] + ".pkg";
        }
        else if (!name.EndsWith(".pkg", StringComparison.OrdinalIgnoreCase))
        {
            name += ".pkg";
        }

        foreach (var c in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(c, '_');
        }

        return name;
    }

    private static bool UsesLegacySiblingManifestLayout(Uri uri)
    {
        var host = uri.IdnHost;
        var path = uri.AbsolutePath;
        return host.Equals("gs2.ww.prod.dl.playstation.net", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("/ppkgo/", StringComparison.OrdinalIgnoreCase);
    }

    private static Uri ReplacePathSuffix(Uri source, string suffix, string replacement)
    {
        return ReplacePathTail(source, suffix.Length, replacement);
    }

    private static Uri ReplacePathTail(Uri source, int tailLength, string replacement)
    {
        var builder = new UriBuilder(source)
        {
            Path = source.AbsolutePath[..^tailLength] + replacement
        };
        return builder.Uri;
    }

    private static int GetNumericChunkSuffixLength(string path)
    {
        if (!path.EndsWith(".pkg", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        var extensionStart = path.Length - 4;
        var cursor = extensionStart - 1;
        while (cursor >= 0 && char.IsAsciiDigit(path[cursor]))
        {
            cursor--;
        }

        if (cursor < 0 || path[cursor] != '_' || cursor == extensionStart - 1)
        {
            return 0;
        }

        return path.Length - cursor;
    }
}
