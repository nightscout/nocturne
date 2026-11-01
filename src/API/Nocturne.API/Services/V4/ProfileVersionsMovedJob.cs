using Nocturne.API.Services.Caching;
using Nocturne.Core.Contracts.Jobs;

namespace Nocturne.API.Services.V4;

/// <summary>
/// For a tenant whose profile versions a migration moved without a write: re-resolves its
/// schedule-derived percent temp basal rates (<see cref="ITempBasalRateReresolver.AfterProfilesChangedAsync"/>)
/// and drops its cached reads, as a profile write would have.
/// </summary>
/// <remarks>
/// Its rows come from the migration that moved the versions, so it enrols no tenant itself. The
/// re-resolution logs its own failure rather than throw, so a failed one is not retried.
/// </remarks>
internal sealed class ProfileVersionsMovedJob(ILogger<ProfileVersionsMovedJob> logger) : IScheduledJob
{
    /// <summary>The name the migrations that request the job write into <c>job_ledger</c>.</summary>
    internal const string JobName = "profile-versions-moved";

    public string Name => JobName;

    public JobEnrolment Enrolment => JobEnrolment.OnRequest;

    public async Task<JobOutcome> RunAsync(JobRun run, CancellationToken ct)
    {
        var tenantId = run.Tenant!.TenantId;
        await run.Services.GetRequiredService<ITempBasalRateReresolver>().AfterProfilesChangedAsync([], ct);
        await run.Services.GetRequiredService<ITenantCache>().RemoveTenantAsync(tenantId, ct);
        logger.LogInformation(
            "Re-resolved the percent temp basal rates of tenant {TenantId}, whose profile versions a migration moved",
            tenantId);
        return JobOutcome.Done;
    }
}
