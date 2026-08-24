namespace Nocturne.API.Authorization;

/// <summary>
/// The shape of a presented credential, read before anything validates it.
/// </summary>
/// <remarks>
/// The auth chain routes on this: a handler that recognises JWTs claims the credential and every
/// later handler is skipped, so what counts as a JWT here decides which handler owns it.
/// </remarks>
public static class TokenFormat
{
    /// <summary>
    /// Whether <paramref name="token"/> is in the JWT compact serialization — three dot-separated
    /// segments. Nocturne's opaque credentials carry no dot at all: <c>noc_</c> direct grants are
    /// Base64-URL, and legacy Nightscout access tokens are <c>name-hash</c>.
    /// </summary>
    /// <param name="token">The presented credential, or null when none was presented.</param>
    public static bool IsJwt(string? token) =>
        !string.IsNullOrEmpty(token) && token.Count(c => c == '.') == 2;
}
