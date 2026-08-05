using System.Net;

namespace FetchPkg.Core;

internal static class HttpResponseErrors
{
    public static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        string? body = null;
        try
        {
            body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (body.Length > 512)
            {
                body = body[..512] + "...";
            }

            body = body.Replace('\r', ' ').Replace('\n', ' ').Trim();
        }
        catch
        {
            // Preserve the original HTTP failure even if the error body cannot be read.
        }

        var status = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}".TrimEnd();
        var message = $"{status} for {request.Method} {request.RequestUri}";
        if (!string.IsNullOrWhiteSpace(body))
        {
            message += $". Response: {body}";
        }

        throw new HttpRequestException(message, null, response.StatusCode);
    }
}
