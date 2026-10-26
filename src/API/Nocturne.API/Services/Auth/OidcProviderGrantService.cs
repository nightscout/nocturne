using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Services.Auth;

/// <inheritdoc cref="IOidcProviderGrantService"/>
public class OidcProviderGrantService(
    INocturneDbContextFactory contexts,
    IOidcProviderService providerService,
    IHttpClientFactory httpClientFactory,
    IDataProtectionProvider dataProtectionProvider,
    TimeProvider timeProvider,
    ILogger<OidcProviderGrantService> logger) : IOidcProviderGrantService
{
    private readonly IDataProtector _protector =
        dataProtectionProvider.CreateProtector("Nocturne.Oidc.ProviderRefreshToken.v1");

    /// <inheritdoc />
    public async Task KeepAsync(
        OidcProvider provider, string oidcSubjectId, string? refreshToken, CancellationToken ct = default)
    {
        if (provider.ProviderType != OidcProviderType.Apple || string.IsNullOrEmpty(refreshToken))
            return;

        await using var db = await contexts.CreateAsync(ct);
        var identity = await db.SubjectOidcIdentities.FirstOrDefaultAsync(
            i => i.OidcSubjectId == oidcSubjectId && i.Issuer == provider.IssuerUrl, ct);
        if (identity is null)
            return;

        identity.ProviderRefreshTokenEncrypted = _protector.Protect(Encoding.UTF8.GetBytes(refreshToken));
        await db.SaveChangesAsync(ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<HeldProviderGrant>> CaptureAsync(
        Guid subjectId, Guid? identityId = null, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateAsync(ct);
        var held = await db.SubjectOidcIdentities
            .AsNoTracking()
            .Where(i => i.SubjectId == subjectId && i.ProviderRefreshTokenEncrypted != null)
            .Where(i => identityId == null || i.Id == identityId)
            .Select(i => new { i.ProviderId, Token = i.ProviderRefreshTokenEncrypted! })
            .ToListAsync(ct);

        var grants = new List<HeldProviderGrant>(held.Count);
        foreach (var row in held)
        {
            try
            {
                grants.Add(new HeldProviderGrant(row.ProviderId, Encoding.UTF8.GetString(_protector.Unprotect(row.Token))));
            }
            catch (CryptographicException ex)
            {
                logger.LogWarning(ex, "A kept refresh token for provider {ProviderId} could not be decrypted", row.ProviderId);
            }
        }

        return grants;
    }

    /// <inheritdoc />
    public async Task RevokeAsync(IReadOnlyCollection<HeldProviderGrant> grants, CancellationToken ct = default)
    {
        foreach (var grant in grants)
        {
            try
            {
                await RevokeOneAsync(grant, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
            {
                logger.LogWarning(ex, "Could not revoke the grant provider {ProviderId} holds", grant.ProviderId);
            }
        }
    }

    private async Task RevokeOneAsync(HeldProviderGrant grant, CancellationToken ct)
    {
        var provider = await providerService.GetProviderByIdAsync(grant.ProviderId);
        if (provider is not { ProviderType: OidcProviderType.Apple })
        {
            logger.LogWarning("Provider {ProviderId} of a kept grant is gone or no longer Apple", grant.ProviderId);
            return;
        }

        var discovery = await providerService.GetDiscoveryDocumentAsync(provider.Id);
        if (string.IsNullOrEmpty(discovery?.RevocationEndpoint))
            throw new InvalidOperationException($"Provider {provider.Name} publishes no revocation endpoint");

        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = provider.ClientId,
            ["client_secret"] = AppleClientSecret.Create(provider, timeProvider.GetUtcNow()),
            ["token"] = grant.RefreshToken,
            ["token_type_hint"] = "refresh_token",
        });

        using var response = await httpClientFactory.CreateClient("OidcProvider")
            .PostAsync(discovery.RevocationEndpoint, content, ct);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Provider {provider.Name} refused the revocation: {(int)response.StatusCode}");

        logger.LogInformation("Revoked the grant provider {Provider} held", provider.Name);
    }
}
