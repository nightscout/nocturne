using Nocturne.Core.Models;

namespace Nocturne.API.Services.Platform;

/// <summary>
/// The one source of store size: the dbstats figures of a ddata frame and the <c>dbsize</c>
/// property both read it, and so does the platform capacity endpoint.
/// </summary>
/// <remarks>
/// PostgreSQL holds every tenant in the same relations, so a tenant's size is an estimate: for each
/// tenant-scoped table, the share of the table's rows that are the tenant's, applied to the table's
/// data and index bytes. Catalogue figures for the whole relation or database never leave this type
/// except through <see cref="ReadDatabaseSizeAsync"/>, which only a platform administrator reaches.
/// </remarks>
public interface IStoreSizeReader
{
    /// <summary>
    /// The current tenant's estimate, cached. A public share or a history-clamped reader gets zero
    /// and neither computes nor touches the cache.
    /// </summary>
    Task<DbStats> ReadAsync(CancellationToken ct = default);

    /// <summary>Estimates <paramref name="tenantId"/>'s size without the cache.</summary>
    Task<DbStats> EstimateAsync(Guid tenantId, CancellationToken ct = default);

    /// <summary>The whole database's size, across every tenant.</summary>
    Task<DatabaseSize> ReadDatabaseSizeAsync(CancellationToken ct = default);
}

/// <summary>Whole-database size in bytes: <paramref name="TotalBytes"/> includes <paramref name="IndexBytes"/>.</summary>
public sealed record DatabaseSize(long TotalBytes, long IndexBytes);
