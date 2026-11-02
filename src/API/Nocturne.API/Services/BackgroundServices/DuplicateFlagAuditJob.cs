using Nocturne.Core.Contracts.Infrastructure;
using Nocturne.Core.Contracts.Jobs;

namespace Nocturne.API.Services.BackgroundServices;

/// <summary>
/// Once a day, repairs an active tenant's stored duplicate flags that disagree with their links
/// (<see cref="IDuplicateReconciler.AuditDuplicateFlagsAsync(CancellationToken)"/>) and logs a
/// warning per record type that had any, since every disagreement is a writer's bug.
/// </summary>
/// <remarks>
/// The run keeps no cursor: a run cut short is read again whole next time, and a repair rewrites only
/// rows that still disagree. The scheduler's scope reads as no one, so no history clamp narrows the
/// read. An inactive tenant's row is parked for <see cref="IScheduledJob.InactiveTenantRecheck"/>.
/// </remarks>
internal sealed class DuplicateFlagAuditJob(ILogger<DuplicateFlagAuditJob> logger) : IScheduledJob
{
    internal const string JobName = "duplicate-flag-audit";

    internal static readonly TimeSpan Interval = TimeSpan.FromDays(1);

    public string Name => JobName;

    public JobEnrolment Enrolment => JobEnrolment.PerTenant;

    public TimeSpan Spread => Interval;

    public async Task<JobOutcome> RunAsync(JobRun run, CancellationToken ct)
    {
        if (!run.Tenant!.IsActive)
            return JobOutcome.Resume(null, run.StartedAt + IScheduledJob.InactiveTenantRecheck);

        var drift = await run.Services.GetRequiredService<IDuplicateReconciler>().AuditDuplicateFlagsAsync(ct);
        foreach (var table in drift)
        {
            logger.LogWarning(
                "Duplicate flags disagreed with their links for tenant {TenantSlug} in {RecordType}: {FlaggedNotDuplicate} flagged with no non-primary link, {DuplicateNotFlagged} with a non-primary link not flagged; {Repaired} repaired",
                run.Tenant.Slug, table.RecordType, table.FlaggedNotDuplicate, table.DuplicateNotFlagged, table.Repaired);
        }

        if (drift.Count == 0)
            logger.LogDebug("Duplicate flags agree with their links for tenant {TenantSlug}", run.Tenant.Slug);

        return JobOutcome.Resume(null, run.StartedAt + Interval);
    }
}
