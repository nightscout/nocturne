using System.Security.Cryptography;

namespace Nocturne.API.Services.Auth;

/// <summary>
/// Generates unguessable tokens for tenant public share links.
/// </summary>
public interface IShareTokenGenerator
{
    /// <summary>Generates a new random share token.</summary>
    string Generate();
}

/// <summary>
/// Generates share tokens as 16 lowercase Crockford-base32 characters (80 bits of entropy)
/// from a cryptographically secure RNG. The alphabet excludes i, l, o, and u to avoid
/// visual ambiguity. Uniqueness against existing tokens is enforced at the call site.
/// </summary>
/// <remarks>
/// 16 rather than 12 because the token's entropy is now the whole security argument. It is stored
/// only as an unsalted SHA-256 digest, which is the right choice for an exact-match lookup — but it
/// means a leaked database gives an attacker an offline search target, and 60 bits is within reach
/// of a well-funded one. The token is only ever copied and pasted, never typed, so the extra four
/// characters cost nothing. Well inside the 63-character DNS label limit for
/// <c>{token}.share.{domain}</c>.
/// </remarks>
public sealed class ShareTokenGenerator : IShareTokenGenerator
{
    private const string Alphabet = "0123456789abcdefghjkmnpqrstvwxyz";
    private const int TokenLength = 16;

    public string Generate() =>
        new(RandomNumberGenerator.GetItems<char>(Alphabet, TokenLength));
}
