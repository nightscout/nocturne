using System.Security.Cryptography;
using System.Text;

namespace Nocturne.API.Services.Auth;

/// <summary>
/// Reproduces legacy Nightscout's access-token matching so migrated tokens keep working 1:1.
/// </summary>
/// <remarks>
/// Legacy Nightscout (<c>cgm-remote-monitor</c>, <c>lib/authorization/storage.js</c>) derives a
/// subject's <c>digest = sha1(sha1(api_secret) + mongo _id)</c> (40 hex chars) and its human-facing
/// access token as <c>{name-abbrev}-{first 16 chars of digest}</c>. Its <c>findSubject</c> then
/// matches an incoming token by taking the substring after the <em>last</em> dash and checking that
/// it is a prefix (length ≥ 16) of the stored digest. Because only that suffix is compared, the
/// name-abbrev is cosmetic and any prefix of the digest between 16 and 40 chars authenticates.
/// <para>
/// This helper implements that suffix-prefix rule (Nightscout's dominant path). It deliberately does
/// not implement the secondary <c>sha1(accessToken)</c> path — no known client emits it and matching
/// it would require persisting a second bearer-equivalent hash at rest.
/// </para>
/// </remarks>
public static class LegacyNightscoutToken
{
    /// <summary>Minimum length Nightscout requires for the digest-prefix suffix.</summary>
    private const int MinPrefixLength = 16;

    /// <summary>Length of the full SHA-1 digest in hex characters.</summary>
    private const int DigestLength = 40;

    /// <summary>
    /// Extracts the normalized digest-prefix (the part after the last dash) from an incoming legacy
    /// token, or null if the token cannot match any digest. The prefix must be 16–40 hex characters;
    /// requiring hex also makes the value safe to use as a SQL <c>LIKE</c> pattern and mirrors the
    /// fact that a non-hex suffix can never be a prefix of a hex digest.
    /// </summary>
    public static string? ExtractDigestPrefix(string? token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return null;
        }

        var dashIndex = token.LastIndexOf('-');
        var suffix = dashIndex >= 0 ? token[(dashIndex + 1)..] : token;

        if (suffix.Length < MinPrefixLength || suffix.Length > DigestLength)
        {
            return null;
        }

        foreach (var c in suffix)
        {
            if (!Uri.IsHexDigit(c))
            {
                return null;
            }
        }

        return suffix.ToLowerInvariant();
    }

    /// <summary>
    /// Returns true if <paramref name="token"/> resolves to a subject with the given
    /// <paramref name="legacyTokenDigest"/> under Nightscout's suffix-prefix matching rule.
    /// </summary>
    public static bool Matches(string? legacyTokenDigest, string? token)
    {
        if (string.IsNullOrEmpty(legacyTokenDigest))
        {
            return false;
        }

        var prefix = ExtractDigestPrefix(token);
        return prefix != null && legacyTokenDigest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Reconstructs a subject's legacy Nightscout digest (<c>sha1(hashedSecret + mongoId)</c>) at
    /// migration time. <paramref name="hashedSecret"/> is the source instance's
    /// <c>sha1(api_secret)</c> (what Nightscout stores and what the migration sends as the
    /// <c>api-secret</c> header). Returns null when any input is missing, or when the reconstructed
    /// digest does not match the token Nightscout actually issued (<c>{abbrev}-{first 16 of
    /// digest}</c>) — the self-check guards against a rotated or mismatched source secret producing
    /// a digest that would never authenticate the client's real token.
    /// </summary>
    /// <param name="hashedSecret">The source instance's <c>sha1(api_secret)</c> (40 hex chars).</param>
    /// <param name="mongoId">The subject's Mongo <c>_id</c> string.</param>
    /// <param name="issuedAccessToken">The plaintext access token the source instance issued.</param>
    public static string? DeriveDigest(string? hashedSecret, string? mongoId, string? issuedAccessToken)
    {
        if (string.IsNullOrEmpty(hashedSecret) || string.IsNullOrEmpty(mongoId))
        {
            return null;
        }

        var digest = Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(hashedSecret + mongoId)));

        var prefix = ExtractDigestPrefix(issuedAccessToken);
        if (prefix == null || !digest.StartsWith(prefix, StringComparison.Ordinal))
        {
            return null;
        }

        return digest;
    }
}
