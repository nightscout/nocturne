using System.IO.Compression;
using System.Text;
using Nocturne.Connectors.Glooko.Configurations;

namespace Nocturne.Connectors.Glooko.Utilities;

/// <summary>
///     Shared HTTP utilities for Glooko API requests.
/// </summary>
internal static class GlookoHttpHelper
{
    /// <summary>
    ///     Applies the standard Glooko browser-like headers to a request.
    /// </summary>
    public static void ApplyStandardHeaders(
        HttpRequestMessage request,
        string webOrigin,
        string? sessionCookie = null)
    {
        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
        request.Headers.TryAddWithoutValidation("Accept-Encoding", "gzip, deflate, br");
        request.Headers.TryAddWithoutValidation("User-Agent", GlookoConstants.UserAgent);
        request.Headers.TryAddWithoutValidation("Referer", $"{webOrigin}/");
        request.Headers.TryAddWithoutValidation("Origin", webOrigin);
        request.Headers.TryAddWithoutValidation("Connection", "keep-alive");
        request.Headers.TryAddWithoutValidation("Accept-Language", "en-GB,en;q=0.9");

        if (!string.IsNullOrEmpty(sessionCookie))
        {
            request.Headers.TryAddWithoutValidation("Cookie", sessionCookie);
            request.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", "empty");
            request.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "cors");
            request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "same-site");
        }
    }

    /// <summary>
    ///     Reads an HTTP response body, automatically decompressing gzip if needed.
    ///     Glooko sometimes returns gzip-compressed bodies even when the HTTP layer
    ///     hasn't decompressed them (e.g. manual Accept-Encoding handling).
    /// </summary>
    public static async Task<string> ReadResponseAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken = default)
    {
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);

        // Check for gzip magic number (0x1F 0x8B)
        if (bytes.Length >= 2 && bytes[0] == 0x1F && bytes[1] == 0x8B)
        {
            using var compressed = new MemoryStream(bytes);
            using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
            using var decompressed = new MemoryStream();
            await gzip.CopyToAsync(decompressed, cancellationToken);
            return Encoding.UTF8.GetString(decompressed.ToArray());
        }

        return Encoding.UTF8.GetString(bytes);
    }
}
