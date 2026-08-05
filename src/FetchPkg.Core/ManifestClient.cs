using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace FetchPkg.Core;

public sealed class ManifestClient
{
    private readonly HttpClient _http;

    public ManifestClient(HttpClient http)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
    }

    public async Task<PackageManifest> LoadAsync(Uri source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (ManifestUrl.TryGetManifestUri(source, out var manifestUri))
        {
            return await LoadJsonManifestAsync(manifestUri, cancellationToken).ConfigureAwait(false);
        }

        if (!source.AbsolutePath.EndsWith(".pkg", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Unsupported source URL: {source.AbsoluteUri}. " +
                "Use a Sony JSON manifest, a PS5 _sc.pkg link, or a direct standalone PKG.");
        }

        if (ManifestUrl.IsPs5ApplicationPackage(source) &&
            !source.AbsolutePath.EndsWith("-DP.pkg", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "This is a PS5 application PKG piece under /app/pkg/. Its JSON manifest " +
                "cannot be derived by changing the file name because the manifest uses a " +
                "different /app/info/ revision and hash. Select the .json or _sc.pkg link " +
                "for this update in PROSPEROPatches.");
        }

        if (ManifestUrl.IsNumberedPackagePiece(source))
        {
            throw new InvalidDataException(
                "This URL points to one numbered PKG piece, not the complete package. " +
                "Use the corresponding JSON manifest so every piece can be downloaded and merged.");
        }

        return await CreateDirectPackageManifestAsync(source, cancellationToken).ConfigureAwait(false);
    }

    private async Task<PackageManifest> LoadJsonManifestAsync(Uri manifestUri, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, manifestUri);
        using var response = await _http.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        await HttpResponseErrors.EnsureSuccessAsync(response, request, cancellationToken)
            .ConfigureAwait(false);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;

        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("The package manifest root is not a JSON object.");
        }

        var piecesElement = GetRequiredProperty(root, "pieces");
        if (piecesElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("The package manifest does not contain a valid 'pieces' array.");
        }

        var declaredSize = TryGetInt64(root, "originalFileSize", "fileSize", "size") ?? 0;
        var drafts = new List<PieceDraft>();

        var index = 0;
        foreach (var element in piecesElement.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException($"Manifest piece {index} is not an object.");
            }

            var urlText = TryGetString(element, "url", "downloadUrl", "href")
                ?? throw new InvalidDataException($"Manifest piece {index} has no URL.");

            if (!Uri.TryCreate(manifestUri, urlText, out var pieceUri))
            {
                throw new InvalidDataException($"Manifest piece {index} contains an invalid URL.");
            }

            var hashHex = TryGetString(element, "hashValue", "hash", "digest");
            var length = TryGetInt64(element, "fileSize", "size", "length", "pieceSize", "downloadSize");
            var offset = TryGetInt64(element, "fileOffset", "offset", "startOffset", "start");
            drafts.Add(new PieceDraft(index++, pieceUri, offset, length, hashHex));
        }

        if (drafts.Count == 0)
        {
            throw new InvalidDataException("The package manifest contains no pieces.");
        }

        var pieces = new List<PackagePiece>(drafts.Count);
        long runningOffset = 0;
        foreach (var draft in drafts)
        {
            var probe = await ProbeAsync(draft.Url, cancellationToken).ConfigureAwait(false);
            var length = draft.Length ?? probe.Length;
            if (length <= 0)
            {
                throw new InvalidDataException($"Unable to determine the length of piece {draft.Index}.");
            }

            var offset = draft.Offset ?? runningOffset;
            if (offset < 0)
            {
                throw new InvalidDataException($"Piece {draft.Index} has a negative offset.");
            }

            var algorithm = GetHashAlgorithm(draft.HashHex);
            pieces.Add(new PackagePiece(
                draft.Index,
                draft.Url,
                offset,
                length,
                NormalizeHash(draft.HashHex),
                algorithm,
                probe.SupportsRanges));

            runningOffset = Math.Max(runningOffset, checked(offset + length));
        }

        var totalSize = declaredSize > 0 ? declaredSize : runningOffset;
        if (runningOffset > totalSize)
        {
            throw new InvalidDataException(
                $"Manifest pieces extend to {runningOffset} bytes, beyond originalFileSize {totalSize}.");
        }

        return new PackageManifest(manifestUri, totalSize, pieces);
    }

    private async Task<PackageManifest> CreateDirectPackageManifestAsync(
        Uri packageUri,
        CancellationToken cancellationToken)
    {
        var probe = await ProbeAsync(packageUri, cancellationToken).ConfigureAwait(false);
        if (probe.Length <= 0)
        {
            throw new InvalidDataException("Unable to determine the direct package size.");
        }

        var piece = new PackagePiece(0, packageUri, 0, probe.Length, null, null, probe.SupportsRanges);
        return new PackageManifest(packageUri, probe.Length, new[] { piece });
    }

    private async Task<ResourceProbe> ProbeAsync(Uri uri, CancellationToken cancellationToken)
    {
        try
        {
            using var head = new HttpRequestMessage(HttpMethod.Head, uri);
            SetIdentityEncoding(head);
            using var response = await _http.SendAsync(
                head,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                var length = response.Content.Headers.ContentLength ?? -1;
                var ranges = response.Headers.AcceptRanges.Any(value => value.Equals("bytes", StringComparison.OrdinalIgnoreCase));
                if (length > 0 && ranges)
                {
                    return new ResourceProbe(length, true);
                }

                if (length > 0)
                {
                    var rangeProbe = await ProbeRangeAsync(uri, cancellationToken).ConfigureAwait(false);
                    return new ResourceProbe(length, rangeProbe.SupportsRanges);
                }
            }
        }
        catch (HttpRequestException)
        {
            // Some Sony endpoints reject HEAD; a one-byte range request is more reliable.
        }

        return await ProbeRangeAsync(uri, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ResourceProbe> ProbeRangeAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        SetIdentityEncoding(request);
        request.Headers.Range = new RangeHeaderValue(0, 0);
        using var response = await _http.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        await HttpResponseErrors.EnsureSuccessAsync(response, request, cancellationToken)
            .ConfigureAwait(false);

        var contentRange = response.Content.Headers.ContentRange;
        if (response.StatusCode == HttpStatusCode.PartialContent && contentRange?.Length is > 0)
        {
            return new ResourceProbe(contentRange.Length.Value, true);
        }

        var length = response.Content.Headers.ContentLength ?? -1;
        return new ResourceProbe(length, false);
    }

    private static void SetIdentityEncoding(HttpRequestMessage request)
    {
        request.Headers.AcceptEncoding.Clear();
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("identity"));
    }

    private static JsonElement GetRequiredProperty(JsonElement element, string name)
    {
        if (TryGetProperty(element, name, out var value))
        {
            return value;
        }

        throw new InvalidDataException($"Manifest property '{name}' is missing.");
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string? TryGetString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (TryGetProperty(element, name, out var value) && value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }
        }

        return null;
    }

    private static long? TryGetInt64(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!TryGetProperty(element, name, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
            {
                return number;
            }

            if (value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), out number))
            {
                return number;
            }
        }

        return null;
    }

    private static HashAlgorithmName? GetHashAlgorithm(string? hashHex)
    {
        var normalized = NormalizeHash(hashHex);
        return normalized?.Length switch
        {
            40 => HashAlgorithmName.SHA1,
            64 => HashAlgorithmName.SHA256,
            _ => null
        };
    }

    private static string? NormalizeHash(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim();
        if (normalized.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[2..];
        }

        return normalized.ToUpperInvariant();
    }

    private sealed record PieceDraft(int Index, Uri Url, long? Offset, long? Length, string? HashHex);
    private sealed record ResourceProbe(long Length, bool SupportsRanges);
}
