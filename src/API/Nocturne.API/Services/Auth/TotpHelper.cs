using System.Security.Cryptography;
using System.Text;

namespace Nocturne.API.Services.Auth;

/// <summary>
/// RFC 6238 TOTP computation helper. HMAC-SHA1, 6-digit codes, 30-second time step.
/// HMAC-SHA1 is fixed by the ecosystem, not a choice: authenticator apps default to it
/// (the provisioning URI omits the algorithm parameter, i.e. SHA-1), and many ignore a
/// SHA-256 request outright. HMAC's security does not depend on SHA-1 collision
/// resistance, so this is intentionally NOT migrated alongside the instance key.
/// </summary>
public static class TotpHelper
{
    private const int SecretLength = 20;
    private const int Digits = 6;
    private const long TimeStep = 30;
    private static readonly int[] Pow10 = [1, 10, 100, 1_000, 10_000, 100_000, 1_000_000];
    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    /// <summary>
    /// Generates a 20-byte cryptographically random secret suitable for TOTP.
    /// </summary>
    public static byte[] GenerateSecret()
    {
        return RandomNumberGenerator.GetBytes(SecretLength);
    }

    /// <summary>
    /// Computes a 6-digit TOTP code for the given secret and Unix timestamp (seconds),
    /// implementing RFC 6238 with HMAC-SHA1 and a 30-second time step.
    /// </summary>
    /// <param name="secret">The raw shared secret bytes.</param>
    /// <param name="unixTimeSeconds">Current time as Unix seconds since epoch.</param>
    /// <returns>A zero-padded 6-digit OTP string.</returns>
    public static string ComputeTotp(byte[] secret, long unixTimeSeconds)
    {
        var counter = unixTimeSeconds / TimeStep;
        var counterBytes = new byte[8];
        for (var i = 7; i >= 0; i--)
        {
            counterBytes[i] = (byte)(counter & 0xFF);
            counter >>= 8;
        }

        var hash = HMACSHA1.HashData(secret, counterBytes);

        var offset = hash[^1] & 0x0F;
        var binaryCode =
            ((hash[offset] & 0x7F) << 24)
            | ((hash[offset + 1] & 0xFF) << 16)
            | ((hash[offset + 2] & 0xFF) << 8)
            | (hash[offset + 3] & 0xFF);

        var otp = binaryCode % Pow10[Digits];
        return otp.ToString().PadLeft(Digits, '0');
    }

    /// <summary>
    /// Verifies a TOTP code against the current time, accepting codes from the previous, current,
    /// and next time step (a ±1 step / 90-second window). Uses constant-time comparison to prevent
    /// timing side-channel attacks.
    /// </summary>
    /// <param name="secret">The raw shared secret bytes.</param>
    /// <param name="code">The 6-digit code string provided by the user.</param>
    /// <returns><see langword="true"/> if the code matches any adjacent time step.</returns>
    public static bool Verify(byte[] secret, string code)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var codeBytes = Encoding.UTF8.GetBytes(code);

        for (var i = -1; i <= 1; i++)
        {
            var candidate = ComputeTotp(secret, now + i * TimeStep);
            var candidateBytes = Encoding.UTF8.GetBytes(candidate);

            if (CryptographicOperations.FixedTimeEquals(codeBytes, candidateBytes))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Encodes binary data as RFC 4648 Base32 without padding characters.
    /// </summary>
    /// <param name="data">The raw bytes to encode.</param>
    /// <returns>An uppercase Base32 string without trailing <c>=</c> padding.</returns>
    public static string ToBase32(byte[] data)
    {
        var sb = new StringBuilder((data.Length * 8 + 4) / 5);
        var buffer = 0;
        var bitsLeft = 0;

        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bitsLeft += 8;

            while (bitsLeft >= 5)
            {
                bitsLeft -= 5;
                sb.Append(Base32Alphabet[(buffer >> bitsLeft) & 0x1F]);
            }
        }

        if (bitsLeft > 0)
        {
            sb.Append(Base32Alphabet[(buffer << (5 - bitsLeft)) & 0x1F]);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Builds an <c>otpauth://totp/</c> provisioning URI suitable for QR code generation
    /// and import into standard authenticator applications.
    /// </summary>
    /// <param name="username">The account username or email shown in the authenticator app.</param>
    /// <param name="secret">The raw shared secret bytes to encode.</param>
    /// <returns>A fully formed <c>otpauth://totp/Nocturne:{username}?…</c> URI string.</returns>
    public static string BuildProvisioningUri(string username, byte[] secret)
    {
        var base32Secret = ToBase32(secret);
        var encodedUser = Uri.EscapeDataString(username);
        return $"otpauth://totp/Nocturne:{encodedUser}?secret={base32Secret}&issuer=Nocturne&digits=6&period=30";
    }
}
