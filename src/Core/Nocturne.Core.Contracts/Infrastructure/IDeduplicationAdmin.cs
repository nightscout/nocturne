using Nocturne.Core.Contracts.Jobs;
using Nocturne.Core.Models;

namespace Nocturne.Core.Contracts.Infrastructure;

/// <summary>
/// The tenant admin's view of deduplication: a full run over the tenant's whole history, run by the
/// job scheduler and followed through its stored status, and the duplicate group of one record.
/// </summary>
public interface IDeduplicationAdmin
{
    /// <summary>The job ledger name of the full run.</summary>
    const string RunJob = "dedup-full-run";

    /// <summary>
    /// Requests a full run for the current tenant, or returns the id of the one still pending or
    /// running: a tenant has one at a time.
    /// </summary>
    /// <returns>The run's id, for <see cref="GetRunStatusAsync"/>.</returns>
    /// <exception cref="UnauthorizedAccessException">The caller's reads are limited to the last 24 hours.</exception>
    Task<Guid> StartRunAsync(CancellationToken cancellationToken = default);

    /// <returns>The status of the current tenant's run <paramref name="runId"/>, or null when it is not the tenant's latest.</returns>
    Task<DeduplicationJobStatus?> GetRunStatusAsync(Guid runId, CancellationToken cancellationToken = default);

    /// <returns>False when <paramref name="runId"/> is not the tenant's latest run or has already ended.</returns>
    Task<bool> CancelRunAsync(Guid runId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs the current tenant's requested full run from <paramref name="from"/> for a bounded slice of
    /// time, storing its progress as it goes.
    /// </summary>
    /// <returns>Where the next slice resumes, or null when the run has ended.</returns>
    Task<JobCursor?> RunAsync(JobCursor? from, CancellationToken cancellationToken = default);

    /// <returns>The link of <paramref name="recordId"/>, or null if it is not linked.</returns>
    Task<LinkedRecord?> GetLinkedRecordAsync(RecordType recordType, Guid recordId, CancellationToken cancellationToken = default);

    /// <returns>Every link of the group <paramref name="canonicalId"/>.</returns>
    Task<IEnumerable<LinkedRecord>> GetLinkedRecordsAsync(Guid canonicalId, CancellationToken cancellationToken = default);
}
