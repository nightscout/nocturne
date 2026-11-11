using System.Runtime.ExceptionServices;
using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Jobs;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Services.Monitoring;

/// <summary>
/// Synthesises managed alert rules for a tenant's tracker notification thresholds that have none:
/// thresholds created before the tracker→alert-rule unification, and thresholds whose rule was
/// deleted (the FK is SET NULL, so a lost rule shows up here as an unlinked threshold and heals).
/// </summary>
/// <remarks>
/// The trackers controller syncs on every definition write, so a daily pass only finds what a lost
/// rule left. A definition that fails is logged and the rest are still synced; the run then fails
/// with the first error, so the ledger retries it.
/// </remarks>
/// <seealso cref="TrackerAlertRuleSyncService"/>
internal sealed class TrackerAlertRuleBackfillJob(ILogger<TrackerAlertRuleBackfillJob> logger) : IScheduledJob
{
    internal const string JobName = "tracker-alert-rule-backfill";

    internal static readonly TimeSpan Interval = TimeSpan.FromDays(1);

    public string Name => JobName;

    public JobEnrolment Enrolment => JobEnrolment.PerTenant;

    public async Task<JobOutcome> RunAsync(JobRun run, CancellationToken ct)
    {
        var tenantId = run.Tenant!.TenantId;

        List<Guid> definitionIds;
        await using (var db = await run.Services.GetRequiredService<INocturneDbContextFactory>().CreateAsync(ct))
        {
            definitionIds = await db.TrackerNotificationThresholds
                .AsNoTracking()
                .Where(t => t.AlertRuleId == null)
                .Select(t => t.TrackerDefinitionId)
                .Distinct()
                .ToListAsync(ct);
        }

        var sync = run.Services.GetRequiredService<ITrackerAlertRuleSyncService>();
        var failures = new List<Exception>();
        foreach (var definitionId in definitionIds)
        {
            try
            {
                await sync.SyncDefinitionAsync(definitionId, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failures.Add(ex);
                logger.LogError(ex,
                    "Error backfilling managed alert rules for tracker definition {DefinitionId} on tenant {TenantId}",
                    definitionId, tenantId);
            }
        }

        if (failures.Count > 0)
            ExceptionDispatchInfo.Throw(failures[0]);

        if (definitionIds.Count > 0)
        {
            logger.LogInformation(
                "Backfilled managed alert rules for {Count} tracker definition(s) of tenant {TenantId}",
                definitionIds.Count, tenantId);
        }

        return JobOutcome.Resume(null, run.StartedAt + Interval);
    }
}
