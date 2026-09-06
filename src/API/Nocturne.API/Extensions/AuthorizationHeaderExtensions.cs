namespace Nocturne.API.Extensions;

/// <summary>
/// Reads a scheme-prefixed credential out of a request's <c>Authorization</c> header.
/// </summary>
public static class AuthorizationHeaderExtensions
{
    /// <summary>
    /// The credential carried under <paramref name="scheme"/>, or <see langword="null"/> when the
    /// header is absent or names a different scheme.
    /// </summary>
    /// <remarks>
    /// The scheme is matched case-insensitively, as RFC 9110 §11.1 defines it, and the credential is
    /// trimmed. A present-but-empty credential (<c>Authorization: Bearer </c>) comes back as an empty
    /// string rather than null, so that a caller with a second source — a query-string token — decides
    /// for itself whether to fall through to it. Only the first header value is read; every credential
    /// path in the auth chain reads a repeated <c>Authorization</c> header the same way.
    /// </remarks>
    /// <param name="request">The request to read.</param>
    /// <param name="scheme">The scheme word, without its trailing space.</param>
    public static string? GetAuthorizationCredential(
        this HttpRequest request, string scheme = "Bearer")
    {
        var header = request.Headers.Authorization.FirstOrDefault();
        var prefix = scheme + ' ';

        return !string.IsNullOrEmpty(header)
            && header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? header[prefix.Length..].Trim()
            : null;
    }
}
