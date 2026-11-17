using Nocturne.Core.Models.Authorization;

namespace Nocturne.Core.Contracts.Auth;

/// <summary>
/// Stores the OIDC and OAuth2 identity providers an instance signs in through. Secrets cross as
/// ciphertext only: the service holding the key encrypts before a write and decrypts after a read.
/// </summary>
/// <remarks>
/// <c>oidc_providers</c> is not tenant-scoped and carries no policy, so every method runs as the
/// scope's reader, or on the caller's unit; the startup sync resolves its repository in a scope of
/// its own, which reads as no one.
/// </remarks>
public interface IOidcProviderRepository
{
    /// <summary>Every provider, or with <paramref name="enabledOnly"/> the enabled ones, by display order then name.</summary>
    Task<IReadOnlyList<OidcProviderRecord>> ListAsync(bool enabledOnly, CancellationToken ct);

    /// <summary>The provider with <paramref name="providerId"/>; null when there is none.</summary>
    Task<OidcProviderRecord?> GetAsync(Guid providerId, CancellationToken ct);

    /// <summary>The provider at <paramref name="issuerUrl"/>, with or without a trailing slash on either side; null when there is none.</summary>
    Task<OidcProviderRecord?> FindByIssuerAsync(string issuerUrl, CancellationToken ct);

    /// <summary>Stores a provider and returns its id.</summary>
    Task<Guid> CreateAsync(NewOidcProvider provider, CancellationToken ct);

    /// <summary>Applies <paramref name="edit"/>; null when there is none.</summary>
    Task<OidcProviderRecord?> UpdateAsync(Guid providerId, OidcProviderEdit edit, CancellationToken ct);

    /// <summary>Deletes the provider; false when there is none.</summary>
    Task<bool> DeleteAsync(Guid providerId, CancellationToken ct);

    /// <summary>Enables or disables the provider; false when there is none.</summary>
    Task<bool> SetEnabledAsync(Guid providerId, bool enabled, CancellationToken ct);

    /// <summary>Keeps the discovery document fetched at <paramref name="cachedAt"/>; false when there is no such provider.</summary>
    Task<bool> CacheDiscoveryDocumentAsync(Guid providerId, string documentJson, DateTime cachedAt, CancellationToken ct);

    /// <summary>
    /// Makes the stored providers match <paramref name="configured"/>, each of which names its id:
    /// an existing row takes the configured values, a missing one is added, and an enabled row not
    /// among them is disabled, in one save. Stored secrets, claim mappings and discovery documents
    /// are left as they are. An existing row whose values are unchanged is recorded as updated at
    /// <paramref name="now"/>.
    /// </summary>
    Task SyncConfiguredAsync(IReadOnlyList<NewOidcProvider> configured, DateTime now, CancellationToken ct);

    /// <summary>When any provider was last updated; null when there is none.</summary>
    Task<DateTime?> LastModifiedAsync(CancellationToken ct);
}
