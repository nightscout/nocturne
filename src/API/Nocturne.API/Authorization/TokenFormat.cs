namespace Nocturne.API.Authorization;

/// <summary>
/// The shape of a presented credential, read before anything validates it.
/// </summary>
/// <remarks>
/// The auth chain routes on this: a handler that recognises a credential claims it and every later
/// handler is skipped, so what counts as a given shape here decides which handler owns it.
/// </remarks>
public static class TokenFormat
{
    /// <summary>
    /// Random bytes behind a Nocturne-minted access token, whose plaintext is those bytes
    /// hex-encoded. Read by the generator too, so the mint and the shape cannot drift apart.
    /// </summary>
    internal const int AccessTokenBytes = 32;

    /// <summary>
    /// Whether <paramref name="token"/> is in the JWT compact serialization — three dot-separated
    /// segments. Nocturne's opaque credentials carry no dot at all: <c>noc_</c> direct grants are
    /// Base64-URL, and legacy Nightscout access tokens are <c>name-hash</c>.
    /// </summary>
    /// <param name="token">The presented credential, or null when none was presented.</param>
    public static bool IsJwt(string? token) =>
        !string.IsNullOrEmpty(token) && token.Count(c => c == '.') == 2;

    /// <summary>
    /// Whether <paramref name="token"/> could be an access token, and so worth a lookup.
    /// </summary>
    /// <remarks>
    /// Two shapes reach the same credential. Nocturne mints a bare hex string of
    /// <see cref="AccessTokenBytes"/> bytes and stores its SHA-256; a subject migrated from classic
    /// Nightscout keeps that instance's <c>{name-abbrev}-{digest}</c> token, matched either by the
    /// same SHA-256 or by <see cref="Services.Auth.LegacyNightscoutToken"/>'s digest-prefix rule.
    /// Nothing here validates the token — it only decides whether a database lookup is worth doing,
    /// so it is deliberately wider than either lookup.
    /// <para>
    /// A JWT is not hex and carries no dash-delimited suffix of its own, and a <c>noc_</c> direct
    /// grant is neither hex nor dash-delimited before its secret, so both fall through to the
    /// handlers that own them.
    /// </para>
    /// </remarks>
    /// <param name="token">The presented credential, or null when none was presented.</param>
    public static bool IsAccessToken(string? token) =>
        !string.IsNullOrEmpty(token) && (IsMintedAccessToken(token) || IsNightscoutAccessToken(token));

    /// <summary>
    /// A token as <see cref="Services.Auth.SubjectService"/> mints it: hex, and nothing else.
    /// </summary>
    private static bool IsMintedAccessToken(string token)
    {
        if (token.Length != AccessTokenBytes * 2)
        {
            return false;
        }

        foreach (var c in token)
        {
            if (!Uri.IsHexDigit(c))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// A classic Nightscout token: <c>{name-abbrev}-{digest}</c>, with the digest after the last
    /// dash. The digest's own length and alphabet are left loose because a migrated subject carries
    /// whatever its source instance issued, and the lookup that follows is what actually decides.
    /// </summary>
    private static bool IsNightscoutAccessToken(string token)
    {
        var dashIndex = token.LastIndexOf('-');
        if (dashIndex <= 0 || dashIndex >= token.Length - 1)
        {
            return false;
        }

        var digest = token[(dashIndex + 1)..];
        return digest.Length >= 8 && digest.All(char.IsLetterOrDigit);
    }
}
