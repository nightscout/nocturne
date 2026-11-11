using Nocturne.Core.Models;

namespace Nocturne.Core.Contracts.Infrastructure;

/// <summary>
/// The current tenant's background upkeep of its duplicate groups: merging groups insert-time linking
/// left apart, moving misplaced primaries, pruning groups that never gained a second record, and
/// auditing the stored duplicate flags against the links.
/// </summary>
public interface IDuplicateReconciler
{
    /// <summary>
    /// Reconcile newly-created linked_records for the current tenant in bounded chunks,
    /// merging duplicate canonical groups. Pages links in (SysCreatedAt, Id) order from the
    /// tenant's persisted cursor up to a short lag behind the present, advancing the cursor per
    /// batch. Stops when caught up or after maxBatches. Returns groups merged and whether caught up.
    /// </summary>
    Task<ReconcileResult> ReconcileNewLinksAsync(int batchSize, int maxBatches, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes, for the current tenant, links that are the only link of their group once reconcile
    /// has passed them and they are a day old, at most maxBatches batches of batchSize. Matching
    /// reads a record with no link that old as the singleton group it was, so a group that never
    /// gained a second record costs no row. Returns the number of links deleted.
    /// </summary>
    Task<int> PruneSingletonLinksAsync(int batchSize, int maxBatches, CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves, for the current tenant, the primary of every recent state span group whose closed
    /// primary a live group mate superseded or is still open beside onto the member the survivor
    /// rule picks. Groups linked before that rule existed can hold one, which reads would show as
    /// ended while a copy runs on. Finds them with one query over groups with a member open or
    /// recently started, so a tenant with none costs only that. Returns the number of groups whose
    /// primary moved.
    /// </summary>
    Task<int> RepairStateSpanPrimariesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the current tenant's linked record tables, soft-deleted rows included, for a
    /// stored duplicate flag that disagrees with the row's links, and rewrites each such flag from the
    /// links. Any disagreement is a bug in a writer. The read is paged, and only a page that found any
    /// opens a transaction. A row that commits while the read passes it is left to the next run. The
    /// caller's reads must not be history-clamped, or older rows go unread.
    /// </summary>
    /// <returns>One entry per record type that had any.</returns>
    Task<IReadOnlyList<DuplicateFlagDrift>> AuditDuplicateFlagsAsync(CancellationToken cancellationToken = default);
}
