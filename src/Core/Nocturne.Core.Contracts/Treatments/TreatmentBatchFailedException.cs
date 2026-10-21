using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;

namespace Nocturne.Core.Contracts.Treatments;

/// <summary>
/// A treatment in a batch could not be stored for a reason a retry may not meet again (the store
/// was unreachable, timed out or was cancelled). The batch stops there, as cgm-remote-monitor's
/// ordered <c>bulkWrite</c> does: the treatments before it stay stored and the ones after it are not
/// attempted.
/// </summary>
/// <remarks>
/// A treatment is written as several records, each in its own operation, so one that fails part way
/// may have left some of them stored. Those are not in <see cref="Written"/>; reads see them once
/// the cache is invalidated, and a resend upserts onto them.
/// </remarks>
public sealed class TreatmentBatchFailedException : Exception
{
    public TreatmentBatchFailedException(
        BulkWrite<Treatment> written, int failedIndex, int submitted, bool failedOneStored, Exception innerException)
        : base(Describe(written.Count, failedIndex, submitted, failedOneStored), innerException)
    {
        Written = written;
        FailedIndex = failedIndex;
    }

    /// <summary>
    /// What the batch stored before it stopped, the failing treatment included when it was stored
    /// but could not be read back.
    /// </summary>
    public BulkWrite<Treatment> Written { get; }

    /// <summary>The zero-based position of the failing treatment in the submitted batch.</summary>
    public int FailedIndex { get; }

    private static string Describe(int stored, int failedIndex, int submitted, bool failedOneStored) =>
        failedOneStored
            ? $"Treatment {failedIndex + 1} of {submitted} was stored but could not be read back; "
              + $"the batch stored {stored} treatment(s) up to it and did not attempt the rest"
            : $"Treatment {failedIndex + 1} of {submitted} could not be stored and may be stored in part; "
              + $"the batch stored {stored} treatment(s) before it and did not attempt the rest";
}
