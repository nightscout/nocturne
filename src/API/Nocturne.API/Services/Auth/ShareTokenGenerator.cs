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
/// Generates share tokens as 12 lowercase Crockford-base32 characters (60 bits of entropy)
/// from a cryptographically secure RNG. The alphabet excludes i, l, o, and u to avoid
/// visual ambiguity. Uniqueness against existing tokens is enforced at the call site.
/// </summary>
public sealed class ShareTokenGenerator : IShareTokenGenerator
{
    private const string Alphabet = "0123456789abcdefghjkmnpqrstvwxyz";
    private const int TokenLength = 12;

    public string Generate() =>
        new(RandomNumberGenerator.GetItems<char>(Alphabet, TokenLength));
}
