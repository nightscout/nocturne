namespace Nocturne.Core.Contracts.Health;

/// <summary>
/// The stored state behind <see cref="IGoogleHealthGrantHolders"/>: one row per tenant whose state has
/// been recorded, naming the account it holds a token for, or none once it holds no token.
/// </summary>
public interface IGoogleHealthGrantHolderRepository
{
    /// <summary>
    /// Records <paramref name="accountKey"/>, or no account when it is null, as the state of
    /// <paramref name="tenantId"/>, adding the tenant's row when it has none.
    /// </summary>
    /// <exception cref="InvalidOperationException">The state could not be recorded.</exception>
    Task RecordAsync(Guid tenantId, string? accountKey, CancellationToken ct);

    /// <summary>
    /// Adds a row recording <paramref name="accountKey"/> for <paramref name="tenantId"/>. Returns false
    /// when the tenant already has one or is gone.
    /// </summary>
    Task<bool> TryAddAsync(Guid tenantId, string accountKey, CancellationToken ct);

    /// <summary>Whether a tenant other than <paramref name="tenantId"/> holds <paramref name="accountKey"/>.</summary>
    Task<bool> IsHeldByAnotherTenantAsync(Guid tenantId, string accountKey, CancellationToken ct);

    /// <summary>The tenants whose state has been recorded.</summary>
    Task<IReadOnlySet<Guid>> GetRecordedTenantsAsync(CancellationToken ct);
}
