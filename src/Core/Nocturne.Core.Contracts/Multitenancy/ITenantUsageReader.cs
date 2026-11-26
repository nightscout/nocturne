using Nocturne.Core.Models.Identity;

namespace Nocturne.Core.Contracts.Multitenancy;

/// <summary>
/// Reads the usage metadata of a page of tenants, across tenants, for the operator of the instance.
/// </summary>
/// <remarks>
/// Demo tenants are left out: their data is synthetic and says nothing about use.
/// </remarks>
public interface ITenantUsageReader
{
    /// <summary>
    /// The tenants ordered by id, from <paramref name="offset"/>, at most <paramref name="limit"/> of
    /// them; only <paramref name="tenantId"/> when it is given.
    /// </summary>
    /// <param name="staleAfter">
    /// How long after its last successful sync a connector, named by its id, counts as stale.
    /// </param>
    Task<IReadOnlyList<TenantUsage>> ReadAsync(
        Guid? tenantId, int offset, int limit, Func<string, TimeSpan> staleAfter, CancellationToken ct);
}
