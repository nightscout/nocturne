using System.Security.Cryptography;
using System.Text;

namespace Nocturne.Core.Models.Authorization;

/// <summary>
/// PKCE (Proof Key for Code Exchange) validator per RFC 7636.
/// S256 method only - plain is intentionally unsupported.
/// </summary>
public static class PkceValidator
{
    private const int CodeVerifierByteLength = 32;

    /// <summary>
    /// Validates a code_verifier against a stored code_challenge using the S256 method.
    /// Computes SHA-256 of the verifier, base64url-encodes it, and compares.
    /// </summary>
    public static bool ValidateCodeChallenge(string codeVerifier, string storedCodeChallenge)
    {
        if (string.IsNullOrEmpty(codeVerifier) || string.IsNullOrEmpty(storedCodeChallenge))
            return false;

        var computed = ComputeCodeChallenge(codeVerifier);
        return string.Equals(computed, storedCodeChallenge, StringComparison.Ordinal);
    }

    /// <summary>
    /// Computes the S256 code_challenge for a given code_verifier.
    /// SHA-256 hash, then base64url-encoded (RFC 7636 Appendix B).
    /// </summary>
    public static string ComputeCodeChallenge(string codeVerifier)
    {
        var bytes = Encoding.ASCII.GetBytes(codeVerifier);
        var hash = SHA256.HashData(bytes);
        return Base64UrlEncode(hash);
    }

    /// <summary>
    /// Generates a cryptographically random code_verifier (43 characters, URL-safe per RFC 7636).
    /// Uses <see cref="RandomNumberGenerator"/> for secure random bytes.
    /// </summary>
    public static string GenerateCodeVerifier()
    {
        var bytes = RandomNumberGenerator.GetBytes(CodeVerifierByteLength);
        return Base64UrlEncode(bytes);
    }

    /// <summary>
    /// Base64url encoding per RFC 7636 / RFC 4648 Section 5:
    /// standard Base64 with + replaced by -, / replaced by _, and trailing = removed.
    /// </summary>
    private static string Base64UrlEncode(byte[] data)
    {
        return Convert.ToBase64String(data)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }
}
