using System.Security.Cryptography;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Nocturne.Core.Models.Authorization;

namespace Nocturne.API.Services.Auth;

/// <summary>
/// Signs the client secret Sign in with Apple takes in place of a fixed one: an ES256 JWT issued
/// by the team, about the Services ID, for Apple, signed with the team's <c>.p8</c> key.
/// </summary>
/// <remarks>
/// Apple accepts a secret for up to six months. One is signed per call instead and lives for
/// minutes, so no signed secret is stored or cached, and a rotated key takes effect at once.
/// </remarks>
internal static class AppleClientSecret
{
    /// <summary>The audience Apple requires of a client secret.</summary>
    internal const string Audience = "https://appleid.apple.com";

    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    /// <summary>Signs a client secret for <paramref name="provider"/>, valid from <paramref name="now"/>.</summary>
    /// <exception cref="InvalidOperationException">
    /// The provider has no team or key ID, or its client secret is not a PEM EC private key.
    /// </exception>
    internal static string Create(OidcProvider provider, DateTimeOffset now)
    {
        var settings = provider.Apple;
        if (settings is null || string.IsNullOrWhiteSpace(settings.TeamId) || string.IsNullOrWhiteSpace(settings.KeyId))
            throw new InvalidOperationException($"Apple provider {provider.Name} has no team ID or key ID");

        if (string.IsNullOrWhiteSpace(provider.ClientSecret))
            throw new InvalidOperationException($"Apple provider {provider.Name} has no private key");

        using var key = ECDsa.Create();
        try
        {
            key.ImportFromPem(NormalizePem(provider.ClientSecret));
        }
        catch (ArgumentException ex)
        {
            throw new InvalidOperationException($"Apple provider {provider.Name} private key is not a PEM EC key", ex);
        }
        catch (CryptographicException ex)
        {
            throw new InvalidOperationException($"Apple provider {provider.Name} private key could not be read", ex);
        }

        // The cached signature provider would outlive the key disposed above.
        var signingKey = new ECDsaSecurityKey(key)
        {
            KeyId = settings.KeyId.Trim(),
            CryptoProviderFactory = new CryptoProviderFactory { CacheSignatureProviders = false },
        };

        return new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false }.CreateToken(
            new SecurityTokenDescriptor
            {
                Issuer = settings.TeamId.Trim(),
                Audience = Audience,
                Claims = new Dictionary<string, object> { [JwtRegisteredClaimNames.Sub] = provider.ClientId },
                IssuedAt = now.UtcDateTime,
                Expires = now.Add(Lifetime).UtcDateTime,
                SigningCredentials = new SigningCredentials(signingKey, SecurityAlgorithms.EcdsaSha256),
            });
    }

    /// <summary>
    /// The <c>.p8</c> key as PEM. An environment variable holds one line, so a key arrives with its
    /// line breaks escaped as <c>\n</c>, or as the bare base64 body without the PEM lines.
    /// </summary>
    internal static string NormalizePem(string key)
    {
        var pem = key.Trim()
            .Replace("\\r", "", StringComparison.Ordinal)
            .Replace("\\n", "\n", StringComparison.Ordinal);
        return pem.Contains("-----BEGIN", StringComparison.Ordinal)
            ? pem
            : $"-----BEGIN PRIVATE KEY-----\n{pem}\n-----END PRIVATE KEY-----";
    }
}
