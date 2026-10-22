using Nocturne.Core.Models;

namespace Nocturne.Core.Contracts.Infrastructure;

/// <summary>
/// Service for deduplicating records from multiple data sources.
/// Links records that represent the same underlying event and provides unified views.
/// </summary>
/// <seealso cref="ITreatmentService"/>
public interface IDeduplicationService
{
    /// <summary>
    /// Deduplicate a batch of records: find or create canonical groups and link all records in bulk.
    /// </summary>
    /// <returns>Stats about the batch operation</returns>
    Task<DeduplicationBatchResult> DeduplicateBatchAsync(
        RecordType recordType,
        IReadOnlyList<DeduplicationInput> records,
        CancellationToken cancellationToken = default);

    /// <param name="canonicalId">The canonical group ID</param>
    /// <returns>All linked records in the group</returns>
    Task<IEnumerable<LinkedRecord>> GetLinkedRecordsAsync(
        Guid canonicalId,
        CancellationToken cancellationToken = default);

    /// <returns>The linked record info, or null if not linked</returns>
    Task<LinkedRecord?> GetLinkedRecordAsync(
        RecordType recordType,
        Guid recordId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Get the unified/merged view for a canonical group of state spans
    /// </summary>
    /// <param name="canonicalId">The canonical group ID</param>
    /// <returns>A merged state span with data from all sources</returns>
    Task<StateSpan?> GetUnifiedStateSpanAsync(
        Guid canonicalId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Run deduplication on all existing records (admin job)
    /// </summary>
    /// <param name="progress">Optional progress reporter</param>
    /// <returns>Result of the deduplication job</returns>
    Task<DeduplicationResult> DeduplicateAllAsync(
        IProgress<DeduplicationProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Start a deduplication job in the background
    /// </summary>
    /// <returns>The job ID for tracking progress</returns>
    Task<Guid> StartDeduplicationJobAsync(CancellationToken cancellationToken = default);

    /// <returns>The job status, or null if not found</returns>
    Task<DeduplicationJobStatus?> GetJobStatusAsync(
        Guid jobId,
        CancellationToken cancellationToken = default);

    /// <returns>True if the job was cancelled, false if not found or already completed</returns>
    Task<bool> CancelJobAsync(Guid jobId, CancellationToken cancellationToken = default);

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
}
