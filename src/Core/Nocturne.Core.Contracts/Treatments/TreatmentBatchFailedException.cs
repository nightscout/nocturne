using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;

namespace Nocturne.Core.Contracts.Treatments;

/// <summary>
/// A treatment in a batch could not be stored. The batch stops there, as cgm-remote-monitor's
/// ordered <c>bulkWrite</c> does: the treatments before it stay stored and the ones after it are
/// not attempted.
/// </summary>
public sealed class TreatmentBatchFailedException : Exception
{
    public TreatmentBatchFailedException(
        BulkWrite<Treatment> written, int failedIndex, int submitted, Exception innerException)
        : base(
            $"Treatment {failedIndex + 1} of {submitted} could not be stored; "
            + $"the {failedIndex} before it were stored and the rest were not attempted",
            innerException)
    {
        Written = written;
        FailedIndex = failedIndex;
    }

    /// <summary>What the treatments before the failing one wrote.</summary>
    public BulkWrite<Treatment> Written { get; }

    /// <summary>The zero-based position of the failing treatment in the submitted batch.</summary>
    public int FailedIndex { get; }
}
