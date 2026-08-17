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
    /// <param name="recordType">The type of all records in the batch</param>
    /// <param name="records">The records to deduplicate</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Stats about the batch operation</returns>
    Task<DeduplicationBatchResult> DeduplicateBatchAsync(
        RecordType recordType,
        IReadOnlyList<DeduplicationInput> records,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Get all records linked to a canonical group
    /// </summary>
    /// <param name="canonicalId">The canonical group ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>All linked records in the group</returns>
    Task<IEnumerable<LinkedRecord>> GetLinkedRecordsAsync(
        Guid canonicalId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Get the linked record information for a specific record
    /// </summary>
    /// <param name="recordType">The type of record</param>
    /// <param name="recordId">The ID of the record</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The linked record info, or null if not linked</returns>
    Task<LinkedRecord?> GetLinkedRecordAsync(
        RecordType recordType,
        Guid recordId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Get the unified/merged view for a canonical group of state spans
    /// </summary>
    /// <param name="canonicalId">The canonical group ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>A merged state span with data from all sources</returns>
    Task<StateSpan?> GetUnifiedStateSpanAsync(
        Guid canonicalId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Run deduplication on all existing records (admin job)
    /// </summary>
    /// <param name="progress">Optional progress reporter</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Result of the deduplication job</returns>
    Task<DeduplicationResult> DeduplicateAllAsync(
        IProgress<DeduplicationProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Start a deduplication job in the background
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The job ID for tracking progress</returns>
    Task<Guid> StartDeduplicationJobAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Get the status of a running deduplication job
    /// </summary>
    /// <param name="jobId">The job ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The job status, or null if not found</returns>
    Task<DeduplicationJobStatus?> GetJobStatusAsync(
        Guid jobId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Cancel a running deduplication job
    /// </summary>
    /// <param name="jobId">The job ID to cancel</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if the job was cancelled, false if not found or already completed</returns>
    Task<bool> CancelJobAsync(Guid jobId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reconcile newly-created linked_records for the current tenant in bounded chunks,
    /// merging duplicate canonical groups. Processes links with SysCreatedAt at or after
    /// the tenant's watermark (minus a small overlap), advancing the watermark per batch.
    /// Stops when caught up or after maxBatches. Returns groups merged and whether caught up.
    /// </summary>
    Task<ReconcileResult> ReconcileNewLinksAsync(int batchSize, int maxBatches, CancellationToken cancellationToken = default);
}
