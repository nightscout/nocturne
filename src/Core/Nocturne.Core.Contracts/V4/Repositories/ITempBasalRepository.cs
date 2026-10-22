using Nocturne.Core.Models.V4;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.V4;

namespace Nocturne.Core.Contracts.V4.Repositories;

/// <summary>
/// Repository for <see cref="TempBasal"/> records representing temporary basal rate overrides
/// issued by a pump or AID algorithm.
/// </summary>
/// <remarks>
/// <see cref="TempBasal"/> records are also used as the underlying store for the legacy V1/V3
/// temp basal treatment projection. Creates upsert on the sync key like the other
/// <see cref="ISyncKeyedRepository{T}"/> types; on top of that surface it carries the
/// source-window reconcile used during connector re-sync and the active-at lookup.
/// </remarks>
/// <seealso cref="TempBasal"/>
/// <seealso cref="Treatments.IIobCalculator"/>
/// <seealso cref="IStateSpanService"/>
public interface ITempBasalRepository
    : ILegacyKeyedRepository<TempBasal>, IDeviceAttributedRepository<TempBasal>, ISyncKeyedRepository<TempBasal>
{
    /// <inheritdoc cref="IV4Repository{T}.GetAsync"/>
    /// <remarks>Filters and orders on the span start.</remarks>
    new Task<IEnumerable<TempBasal>> GetAsync(
        DateTime? from,
        DateTime? to,
        string? device,
        string? source,
        int limit = 100,
        int offset = 0,
        bool descending = true,
        CancellationToken ct = default
    );

    /// <summary>
    /// Retrieve the start timestamp of the most recently stored <see cref="TempBasal"/>, optionally scoped to a data source.
    /// </summary>
    /// <remarks>Used by connectors to resume per-source sync without re-fetching already-stored data.</remarks>
    /// <param name="source">Optional data source filter. Pass <c>null</c> to search across all sources.</param>
    Task<DateTime?> GetLatestTimestampAsync(string? source = null, CancellationToken ct = default);

    /// <summary>
    /// Temp basals set by <see cref="TempBasal.Percent"/> whose rate follows the schedule (resolved
    /// from it, or still unknown), starting in [<paramref name="from"/>, <paramref name="to"/>) with a
    /// null bound open, oldest
    /// first and after <paramref name="after"/> (start, id) when given. Uploaded rates are not read.
    /// </summary>
    Task<IReadOnlyList<TempBasal>> GetScheduleDerivedAsync(
        DateTime? from, DateTime? to, (DateTime Start, Guid Id)? after, int limit, CancellationToken ct = default);

    /// <summary>Whether any live temp basal has a schedule-derived or unknown rate.</summary>
    Task<bool> HasScheduleDerivedAsync(CancellationToken ct = default);

    /// <summary>
    /// Sets the schedule-derived rate of one temp basal, only while it is live, still has
    /// <paramref name="percent"/>, still has the <paramref name="readRate"/> it was read with and
    /// still has a schedule-derived or unknown rate, so a concurrent edit, upload, delete or
    /// re-resolution is never overwritten. A null <paramref name="rate"/> leaves it unknown.
    /// </summary>
    /// <returns>Whether the row was updated.</returns>
    Task<bool> SetScheduleDerivedRateAsync(
        Guid id, double percent, double? readRate, double? rate, double? scheduledRate, CancellationToken ct = default);

    /// <summary>The live temp basals set by <see cref="TempBasal.Percent"/> under these legacy ids.</summary>
    Task<IReadOnlyList<TempBasal>> GetRelativeByLegacyIdsAsync(
        IReadOnlyCollection<string> legacyIds, CancellationToken ct = default);

    /// <summary>
    /// Idempotently reconciles a source's temp basals within a date range: soft-deletes only the
    /// rows this source no longer reports (their legacy id is absent from
    /// <paramref name="keepLegacyIds"/>), leaving still-reported rows untouched.
    /// </summary>
    /// <remarks>
    /// Used by connector re-sync. Pair with <see cref="IBulkCreateRepository{TRecord}.BulkCreateAsync"/> — which skips legacy ids
    /// that are already active — so re-importing an unchanged window is a no-op rather than a
    /// delete-the-whole-window-then-reinsert sweep. The old sweep re-created every record as a new
    /// row each cycle (system-scope deletes don't block re-insertion), accumulating millions of
    /// soft-deleted tombstones for high-volume connectors.
    /// </remarks>
    /// <param name="source">Data source identifier (e.g., connector name).</param>
    /// <param name="from">Inclusive start of the reconcile range.</param>
    /// <param name="to">Inclusive end of the reconcile range.</param>
    /// <param name="keepLegacyIds">Legacy ids still reported by the source in this window; rows with
    /// these ids are kept, all other active rows of this source in range are soft-deleted.</param>
    /// <returns>Number of records soft-deleted.</returns>
    Task<int> SoftDeleteAbsentBySourceAndDateRangeAsync(
        string source,
        DateTime from,
        DateTime to,
        IReadOnlySet<string> keepLegacyIds,
        CancellationToken ct = default
    );

    /// <summary>
    /// Returns the <see cref="TempBasal"/> active at <paramref name="at"/>
    /// (<c>StartTimestamp &lt;= at &lt; EndTimestamp</c>), or <c>null</c> if no
    /// temp is active. When multiple records overlap the instant, the one with
    /// the most recent <c>StartTimestamp</c> wins.
    /// </summary>
    /// <remarks>
    /// A <c>null</c> <see cref="TempBasal.EndTimestamp"/> represents an open-ended
    /// temp basal (still running) and is treated as active for any <paramref name="at"/>
    /// at or after its start.
    /// </remarks>
    /// <param name="at">The instant to evaluate.</param>
    Task<TempBasal?> GetActiveAtAsync(DateTime at, CancellationToken ct = default);
}
