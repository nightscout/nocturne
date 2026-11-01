using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;

namespace Nocturne.Core.Contracts.Treatments;

/// <summary>
/// A treatment batch could not be stored for a reason a retry may not meet again (the store was
/// unreachable, timed out or was cancelled), or a stored treatment could not be read back.
/// </summary>
/// <remarks>
/// A batch is written table by table, each table in one operation, so a failure part way leaves the
/// records of the tables before it stored: a treatment with any record committed is in
/// <see cref="Written"/>, and one with only some of its records stored is there too. Reads see them,
/// and a resend upserts onto them.
/// </remarks>
public sealed class TreatmentBatchFailedException : Exception
{
    public TreatmentBatchFailedException(
        BulkWrite<Treatment> written, int? failedIndex, int submitted, bool failedOneStored, Exception innerException)
        : base(Describe(written.Count, written.Rejected.Count, failedIndex, submitted, failedOneStored), innerException)
    {
        Written = written;
        FailedIndex = failedIndex;
    }

    /// <summary>The treatments the batch stored a record of before it stopped, in submitted order.</summary>
    public BulkWrite<Treatment> Written { get; }

    /// <summary>
    /// The zero-based position of the stored treatment that could not be read back, or of the first
    /// treatment the failure left with nothing stored that was meant to store something. A treatment
    /// refused for its data, or of a kind that stores nothing, is never named. Null when every
    /// treatment that was meant to store something stored a record before the failure.
    /// </summary>
    public int? FailedIndex { get; }

    private static string Describe(int stored, int refused, int? failedIndex, int submitted, bool failedOneStored) =>
        (failedOneStored, failedIndex) switch
        {
            (true, { } index) => $"Treatment {index + 1} of {submitted} was stored but could not be read back; "
                + $"the batch stored {stored} treatment(s)",
            (false, { } index) => $"The batch of {submitted} treatment(s) stopped with treatment {index + 1} the first with nothing stored; "
                + $"{stored} treatment(s) were stored, each possibly in part",
            _ => $"The batch of {submitted} treatment(s) stopped with no treatment left to store; "
                + $"{stored} treatment(s) were stored, each possibly in part, and {refused} refused for their data",
        };
}
