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
/// The token is stored only as an unsalted SHA-256 digest, so its entropy is the whole defence
/// against an offline search of a leaked dump; 60 bits was within reach of a well-funded attacker.
/// The token forms the first label of <c>{token}.share.{domain}</c>, so it must stay under 63
/// characters.
/// </remarks>
public sealed class ShareTokenGenerator : IShareTokenGenerator
{
    private const string Alphabet = "0123456789abcdefghjkmnpqrstvwxyz";
    private const int TokenLength = 16;

    public string Generate() =>
        new(RandomNumberGenerator.GetItems<char>(Alphabet, TokenLength));
}
