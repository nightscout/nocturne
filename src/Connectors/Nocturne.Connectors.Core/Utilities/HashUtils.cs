using System.Security.Cryptography;
using System.Text;

namespace Nocturne.Connectors.Core.Utilities;

/// <summary>
///     Shared hashing utilities for connectors.
///     Used for generating unique IDs, hashing API secrets, and account identification.
/// </summary>
public static class HashUtils
{
    /// <summary>
    ///     Computes SHA256 hash and returns as lowercase hex string.
    ///     Used for secure hashing of sensitive identifiers.
    /// </summary>
    /// <returns>Lowercase hex string of the SHA256 hash</returns>
    public static string Sha256Hex(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>
    ///     Computes SHA1 hash and returns as lowercase hex string.
    ///     Used for Nightscout API-secret hashing, which the Nightscout <c>api-secret</c> wire
    ///     protocol mandates as SHA-1, and for the hashed keys of <see cref="ConnectorLegacyIds.Hashed"/>,
    ///     which records are already stored under. Other hashing uses SHA-256 (see <see cref="Sha256Hex"/>).
    /// </summary>
    /// <returns>Lowercase hex string of the SHA1 hash</returns>
    public static string Sha1Hex(string input)
    {
        using var sha1 = SHA1.Create();
        var hashBytes = sha1.ComputeHash(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    public const string HmacSha256Prefix = "hmac-sha256:";

    /// <summary>
    ///     Returns whether <paramref name="value"/> is already wrapped with the HMAC-SHA256 prefix.
    /// </summary>
    public static bool IsHmacSha256(string? value) =>
        value is not null && value.StartsWith(HmacSha256Prefix, StringComparison.Ordinal);

    /// <summary>
    ///     Computes HMAC-SHA256 over an existing lowercase hex SHA-256 digest, keyed with
    ///     <paramref name="key"/>, prefixed with <see cref="HmacSha256Prefix"/>.
    /// </summary>
    public static string HmacSha256OverDigest(string key, string sha256Hex)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        var bytes = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(key),
            Encoding.UTF8.GetBytes(sha256Hex));
        return HmacSha256Prefix + Convert.ToHexStringLower(bytes);
    }

    /// <summary>
    ///     Computes HMAC-SHA256 over the SHA-256 hex digest of <paramref name="input"/>,
    ///     keyed with <paramref name="key"/>.
    /// </summary>
    public static string HmacSha256OverSha256Hex(string key, string input) =>
        HmacSha256OverDigest(key, Sha256Hex(input));
}