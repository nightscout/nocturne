using Nocturne.Core.Models.Authorization;

namespace Nocturne.Core.Contracts.Auth;

/// <summary>
/// Stores the OAuth clients of a tenant: one per Dynamic Client Registration, plus the tenant's
/// docs client.
/// </summary>
/// <remarks>
/// Every method runs as the scope's reader on its tenant, except the two that take a
/// <c>tenantId</c>, which pin it: they serve the anonymous docs paths and the hosted cleanup sweep,
/// whose contexts carry no tenant, and under FORCE ROW LEVEL SECURITY an unpinned read there
/// matches no row. A lookup returns null for a row the reader cannot see.
/// </remarks>
public interface IOAuthClientRepository
{
    /// <summary>The client whose <c>client_id</c> is <paramref name="clientId"/>.</summary>
    Task<OAuthClientRecord?> FindByClientIdAsync(string clientId, CancellationToken ct);

    /// <summary>The client with row id <paramref name="id"/>.</summary>
    Task<OAuthClientRecord?> GetAsync(Guid id, CancellationToken ct);

    /// <summary>
    /// Stores <paramref name="client"/> and returns it. Over a unit of work the tenant is stamped
    /// only when the owner saves, so the returned <see cref="OAuthClientRecord.TenantId"/> is empty.
    /// </summary>
    Task<OAuthClientRecord> CreateAsync(NewOAuthClient client, CancellationToken ct);

    /// <summary>
    /// On <paramref name="tenantId"/>, finds the client whose software id is
    /// <paramref name="client"/>'s, creating it from <paramref name="client"/> with
    /// <paramref name="redirectUri"/> as its one redirect URI when there is none, and otherwise adds
    /// <paramref name="redirectUri"/> to it unless already there or the client already holds
    /// <paramref name="maxRedirectUris"/>.
    /// </summary>
    /// <remarks>
    /// Serialised per tenant by a lock held for the transaction, since no unique index keeps two
    /// first callers from each inserting a row.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="client"/> has no software id.</exception>
    Task<OAuthClientRedirectUriRegistration> FindOrCreateWithRedirectUriAsync(
        Guid tenantId, NewOAuthClient client, string redirectUri, int maxRedirectUris, CancellationToken ct);

    /// <summary>
    /// Deletes, on <paramref name="tenantId"/>, every client created before
    /// <paramref name="createdBefore"/> that no grant, authorization code or device code names,
    /// except one whose software id is <paramref name="keptSoftwareId"/>, and returns how many. A
    /// revoked grant still names its client.
    /// </summary>
    Task<int> DeleteUnreferencedAsync(
        Guid tenantId, DateTime createdBefore, string keptSoftwareId, CancellationToken ct);
}
