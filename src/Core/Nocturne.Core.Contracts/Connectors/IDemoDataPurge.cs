using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Connectors;

namespace Nocturne.Core.Contracts.Connectors;

/// <summary>
/// Hard-deletes everything the demo service wrote, then tells
/// <see cref="V4.ILegacyReadCacheInvalidation"/> which model types it may have emptied.
/// </summary>
/// <remarks>
/// Hard delete, unlike the audited soft-delete <see cref="ISourceOwnedDataPurge"/> applies to every
/// other source: demo data is regenerated wholesale, and the primary path that does so drops the tenant
/// row and lets the database cascade clear every tenant-scoped table before the seeder refills it. A
/// <c>deleted_by_user</c> stamp would only block that regeneration.
/// <para>
/// A group counts as written once it deleted a row, or once it started and threw, since each of its
/// statements commits on its own and a failure leaves how many unknown. A group that never started is
/// not reported.
/// </para>
/// </remarks>
public interface IDemoDataPurge
{
    /// <summary>
    /// Purges every group as the scope's reader, returning the counts by group. Used where the scope
    /// already resolved the demo tenant.
    /// </summary>
    Task<DemoPurgeCounts> PurgeAsync(CancellationToken ct);

    /// <summary>
    /// Purges the glucose group of <paramref name="tenant"/>, reporting the write as that tenant, for a
    /// caller whose own scope resolved none.
    /// </summary>
    Task<long> PurgeEntriesAsync(TenantContext tenant, CancellationToken ct);

    /// <summary>
    /// Purges the treatment group and then the device status group of <paramref name="tenant"/>,
    /// reporting the write as that tenant, and returns the two counts added.
    /// </summary>
    Task<long> PurgeTreatmentsAsync(TenantContext tenant, CancellationToken ct);
}
