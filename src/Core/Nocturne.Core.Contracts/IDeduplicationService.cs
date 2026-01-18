using Nocturne.Core.Models;

namespace Nocturne.Core.Contracts;

/// <summary>
/// Service for deduplicating records from multiple data sources.
/// Links records that represent the same underlying event and provides unified views.
/// </summary>
public interface IDeduplicationService
{
    /// <summary>
    /// Find an existing canonical group that matches the given criteria, or create a new one
    /// </summary>
    /// <param name="recordType">The type of record being deduplicated</param>
    /// <param name="mills">The timestamp of the record in milliseconds</param>
    /// <param name="criteria">Matching criteria for finding duplicates</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The canonical ID for the matching group (existing or newly created)</returns>
    Task<Guid> GetOrCreateCanonicalIdAsync(
        RecordType recordType,
        long mills,
        MatchCriteria criteria,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Link a record to a canonical group
    /// </summary>
    /// <param name="canonicalId">The canonical group ID</param>
    /// <param name="recordType">The type of record being linked</param>
    /// <param name="recordId">The ID of the record to link</param>
    /// <param name="mills">The timestamp of the record in milliseconds</param>
    /// <param name="dataSource">The data source identifier</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task LinkRecordAsync(
        Guid canonicalId,
        RecordType recordType,
        Guid recordId,
        long mills,
        string dataSource,
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
    /// Get the unified/merged view for a canonical group of entries
    /// </summary>
    /// <param name="canonicalId">The canonical group ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>A merged entry with data from all sources</returns>
    Task<Entry?> GetUnifiedEntryAsync(
        Guid canonicalId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Get the unified/merged view for a canonical group of treatments
    /// </summary>
    /// <param name="canonicalId">The canonical group ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>A merged treatment with data from all sources</returns>
    Task<Treatment?> GetUnifiedTreatmentAsync(
        Guid canonicalId,
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
}
