using Nocturne.Core.Models.V4;
using Nocturne.Core.Contracts.V4;

namespace Nocturne.Core.Contracts.V4.Repositories;

/// <summary>
/// Repository for <see cref="PumpSnapshot"/> records that capture the complete state of an insulin pump
/// at a moment in time (reservoir level, active insulin, cartridge info, etc.).
/// </summary>
/// <remarks>
/// Pump snapshots are typically produced by the uploader or connector on each sync cycle.
/// They differ from <see cref="ApsSnapshot"/> records, which capture loop algorithm decision state.
/// </remarks>
/// <seealso cref="PumpSnapshot"/>
/// <seealso cref="IApsSnapshotRepository"/>
/// <seealso cref="IUploaderSnapshotRepository"/>
/// <seealso cref="IV4Repository{T}"/>
public interface IPumpSnapshotRepository : ILegacyKeyedRepository<PumpSnapshot>
{
    /// <summary>Retrieve a page of <see cref="PumpSnapshot"/> records filtered by time range, device, and source.</summary>
    /// <param name="from">Inclusive start of the time window, or <c>null</c> for no lower bound.</param>
    /// <param name="to">Inclusive end of the time window, or <c>null</c> for no upper bound.</param>
    /// <param name="device">Optional device identifier filter.</param>
    /// <param name="source">Optional data source filter.</param>
    /// <param name="limit">Maximum number of records to return (default 100).</param>
    /// <param name="offset">Number of records to skip for pagination (default 0).</param>
    /// <param name="descending">When <c>true</c>, results are ordered newest-first (default).</param>
    /// <param name="ct">Cancellation token.</param>
    new Task<IEnumerable<PumpSnapshot>> GetAsync(DateTime? from, DateTime? to, string? device, string? source, int limit = 100, int offset = 0, bool descending = true, CancellationToken ct = default);

    /// <summary>Retrieve <see cref="PumpSnapshot"/> records matching any of the given correlation IDs.</summary>
    /// <param name="correlationIds">Correlation IDs to match.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IEnumerable<PumpSnapshot>> GetByCorrelationIdsAsync(IEnumerable<Guid> correlationIds, CancellationToken ct = default);

    /// <summary>
    /// Returns the most recent <see cref="PumpSnapshot"/> with <c>Timestamp &lt; <paramref name="timestamp"/></c>,
    /// or <c>null</c> if none exists.
    /// </summary>
    /// <remarks>
    /// Strict less-than comparison so callers can pass a freshly upserted snapshot's timestamp
    /// without retrieving the snapshot they just wrote.
    /// Use <see cref="GetLatestAsync"/> for inclusive freshness reads.
    /// </remarks>
    /// <param name="timestamp">Exclusive upper bound on Timestamp.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<PumpSnapshot?> GetLatestBeforeAsync(DateTime timestamp, CancellationToken ct = default);

    /// <summary>
    /// Returns the latest <see cref="PumpSnapshot"/> for the current tenant, or <c>null</c>
    /// if none exists.
    /// </summary>
    /// <remarks>
    /// Uses inclusive <c>&lt;=</c> comparison so callers can pin replay to a specific timestamp.
    /// Use <see cref="GetLatestBeforeAsync"/> for strict-prior transition detection.
    /// </remarks>
    /// <param name="asOf">When non-null, restricts to snapshots with <c>Timestamp &lt;= asOf</c>;
    /// when <c>null</c>, returns the absolute latest.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<PumpSnapshot?> GetLatestAsync(DateTime? asOf, CancellationToken ct = default);

    /// <summary>
    /// Returns the timestamp of the most recent <see cref="PumpSnapshot"/> for the current tenant,
    /// optionally scoped to a single connector data source, or <c>null</c> if none exist. When
    /// <paramref name="source"/> is non-null, only snapshots with a matching
    /// <see cref="PumpSnapshot.DataSource"/> are considered.
    /// </summary>
    /// <param name="source">Optional connector data source filter.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<DateTime?> GetLatestTimestampAsync(string? source = null, CancellationToken ct = default);

    /// <summary>
    /// Bulk create-or-update by (DataSource, SyncIdentifier): rows matched by that key are updated
    /// in place, so uploader retries of the same loop cycle stay idempotent. Everything else inserts
    /// through the LegacyId-dedup path of <see cref="BulkCreateAsync"/>.
    /// </summary>
    /// <param name="records">Records to upsert.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>All persisted records: updated rows first, then inserted rows.</returns>
    Task<IEnumerable<PumpSnapshot>> BulkUpsertAsync(
        IEnumerable<PumpSnapshot> records,
        WriteOrigin origin, CancellationToken ct = default);
}
