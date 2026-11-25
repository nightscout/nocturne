using Nocturne.Core.Contracts.Jobs;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4;

namespace Nocturne.API.Services.Treatments;

/// <summary>
/// For a tenant that holds treatment records stored live with <c>isValid: false</c>, from before such an
/// upload was stored deleted: deletes each as its client's <c>isValid: false</c> deletes it
/// (<see cref="ITreatmentDecomposer.InvalidateAsync"/>), with what that delete settles around it.
/// </summary>
/// <remarks>
/// Its rows come from the migration that found the records, so it enrols no tenant itself. A second run
/// finds nothing to delete.
/// </remarks>
internal sealed class RetireInvalidTreatmentsJob(ILogger<RetireInvalidTreatmentsJob> logger) : IScheduledJob
{
    /// <summary>The name the migration that requests the job writes into <c>job_ledger</c>.</summary>
    internal const string JobName = "retire-invalid-treatments";

    private const int LegacyIdsPerDelete = 100;

    public string Name => JobName;

    public JobEnrolment Enrolment => JobEnrolment.OnRequest;

    public async Task<JobOutcome> RunAsync(JobRun run, CancellationToken ct)
    {
        var legacyIds = await run.Services.GetRequiredService<IDecomposedTreatmentKeys>().GetLiveInvalidLegacyIdsAsync(ct);
        var decomposer = run.Services.GetRequiredService<ITreatmentDecomposer>();
        var deleted = 0;
        foreach (var chunk in legacyIds.Chunk(LegacyIdsPerDelete))
            deleted += await decomposer.InvalidateAsync(chunk, WriteOrigin.Backfill, ct);

        logger.LogInformation(
            "Deleted {Records} records of {Treatments} treatments stored live with isValid false for tenant {TenantId}",
            deleted, legacyIds.Count, run.Tenant!.TenantId);
        return JobOutcome.Done;
    }
}
