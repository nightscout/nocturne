using System.Security.Cryptography;
using System.Text;

namespace Nocturne.Infrastructure.Data.Security;

/// <summary>
/// SHA-256 hex digests for bearer credentials that are only ever resolved by exact match.
/// Storing the digest instead of the credential means a database read or a restored backup
/// yields nothing that can be replayed as the credential.
/// </summary>
public static class CredentialHash
{
    /// <summary>Length in characters of the values returned by this class.</summary>
    public const int HexLength = 64;

    /// <summary>Lowercase hex SHA-256 of the UTF-8 bytes of <paramref name="value"/>.</summary>
    public static string Sha256Hex(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    /// <summary>
    /// Digest of a public share token. Hostnames are case-insensitive and generated tokens are
    /// lowercase, so the token is lower-cased before hashing.
    /// </summary>
    public static string ShareToken(string token) => Sha256Hex(token.ToLowerInvariant());
}
