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
    /// <param name="input">The string to hash</param>
    /// <returns>Lowercase hex string of the SHA256 hash</returns>
    public static string Sha256Hex(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>
    ///     Computes SHA1 hash and returns as lowercase hex string.
    ///     Used only for Nightscout API-secret hashing, which the Nightscout
    ///     <c>api-secret</c> wire protocol mandates as SHA-1. Internal hashing we
    ///     control uses SHA-256 (see <see cref="Sha256Hex"/>) — do not use SHA-1 for new code.
    /// </summary>
    /// <param name="input">The string to hash</param>
    /// <returns>Lowercase hex string of the SHA1 hash</returns>
    public static string Sha1Hex(string input)
    {
        using var sha1 = SHA1.Create();
        var hashBytes = sha1.ComputeHash(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }
}