using Nocturne.Core.Models.V4;
using Nocturne.Core.Contracts.V4;

namespace Nocturne.Core.Contracts.V4.Repositories;

/// <summary>
/// Repository for <see cref="BasalInjection"/> records representing discrete basal insulin doses.
/// </summary>
/// <remarks>
/// Extends <see cref="IV4Repository{T}"/> with sync-identifier lookup and deletion to support
/// idempotent ingestion of records originating from external data sources.
/// </remarks>
/// <seealso cref="BasalInjection"/>
/// <seealso cref="IV4Repository{T}"/>
public interface IBasalInjectionRepository : IV4Repository<BasalInjection>
{
    /// <summary>Delete <see cref="BasalInjection"/> records matching the given data source and sync identifier.</summary>
    /// <param name="dataSource">The external data source name.</param>
    /// <param name="syncIdentifier">The external sync identifier (e.g., UUID from the uploading system).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Number of records deleted.</returns>
    Task<int> DeleteBySyncIdentifierAsync(string dataSource, string syncIdentifier, WriteOrigin origin, CancellationToken ct = default);

    /// <summary>Find a single <see cref="BasalInjection"/> matching the given data source and sync identifier.</summary>
    /// <param name="dataSource">The external data source name.</param>
    /// <param name="syncIdentifier">The external sync identifier (e.g., UUID from the uploading system).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The matching record, or <c>null</c> if not found.</returns>
    Task<BasalInjection?> FindBySyncIdentifierAsync(string dataSource, string syncIdentifier, CancellationToken ct = default);

    /// <summary>
    /// Bulk-create basal injection records with LegacyId-based deduplication.
    /// Records whose <see cref="BasalInjection.LegacyId"/> already exists for the tenant are skipped.
    /// </summary>
    /// <param name="records">The records to create.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The created records (excludes duplicates).</returns>
    Task<IEnumerable<BasalInjection>> BulkCreateAsync(IEnumerable<BasalInjection> records, WriteOrigin origin, CancellationToken ct = default);

    /// <summary>
    /// Retrieve the timestamp of the most recently stored <see cref="BasalInjection"/>, optionally scoped to a data source.
    /// </summary>
    /// <remarks>Used by connectors to resume per-source sync without re-fetching already-stored data.</remarks>
    /// <param name="source">Optional data source filter. Pass <c>null</c> to search across all sources.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<DateTime?> GetLatestTimestampAsync(string? source = null, CancellationToken ct = default);
}
