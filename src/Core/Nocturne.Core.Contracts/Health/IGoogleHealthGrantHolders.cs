namespace Nocturne.Core.Contracts.Health;

/// <summary>
/// Which tenants hold a Google Health refresh token, and for which Google account.
/// </summary>
/// <remarks>
/// Every tenant signs in through the instance's one OAuth client, and revoking any one token
/// revokes the account's whole grant to that client's project, every tenant's token with it. One
/// tenant may therefore revoke only a grant no other tenant holds. The record spans tenants,
/// so it lives outside row-level security and holds only a tenant id and the account's hashed
/// Google subject.
/// </remarks>
public interface IGoogleHealthGrantHolders
{
    /// <summary>Records that <paramref name="tenantId"/> holds a token for <paramref name="accountKey"/>.</summary>
    Task HoldAsync(Guid tenantId, string accountKey, CancellationToken ct);

    /// <summary>Records that <paramref name="tenantId"/> no longer holds a token.</summary>
    Task ReleaseAsync(Guid tenantId, CancellationToken ct);

    /// <summary>
    /// Whether a tenant other than <paramref name="tenantId"/> holds a token for
    /// <paramref name="accountKey"/>; true as well when that cannot be determined.
    /// </summary>
    Task<bool> MayBeHeldByAnotherTenantAsync(Guid tenantId, string accountKey, CancellationToken ct);
}
