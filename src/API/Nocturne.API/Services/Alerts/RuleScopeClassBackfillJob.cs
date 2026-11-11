using Nocturne.Core.Alerts.Native;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Jobs;

namespace Nocturne.API.Services.Alerts;

/// <summary>
/// Stamps <c>scope_class</c> on each of a tenant's alert rules, once, for scoped Do Not Disturb
/// (ADR 0004). Rules created before classification existed default to <c>undirected</c> (all-only)
/// from the D2 migration; this recomputes each one through <see cref="IRuleScopeClassifier"/> so
/// scoped <c>lows</c>/<c>highs</c> windows can narrow-match them. The controller computes
/// <c>scope_class</c> on every create and update, so a tenant's rules stay classified after it.
/// </summary>
/// <remarks>
/// Only rows whose recomputed class differs from the stored one are written, each distinct class in
/// one statement through <see cref="IAlertRuleRepository.SetScopeClassAsync"/>.
/// </remarks>
/// <seealso cref="RuleScopeClassifier"/>
internal sealed class RuleScopeClassBackfillJob(ILogger<RuleScopeClassBackfillJob> logger) : IScheduledJob
{
    internal const string JobName = "alert-rule-scope-class-backfill";

    public string Name => JobName;

    public JobEnrolment Enrolment => JobEnrolment.PerTenant;

    public async Task<JobOutcome> RunAsync(JobRun run, CancellationToken ct)
    {
        var tenantId = run.Tenant!.TenantId;
        var classifier = run.Services.GetRequiredService<IRuleScopeClassifier>();
        var rules = run.Services.GetRequiredService<IAlertRuleRepository>();

        var reclassified = (await run.Services.GetRequiredService<IAlertRepository>().GetRuleSnapshotsAsync(tenantId, ct))
            .Select(r => (r.Id, Computed: classifier.Classify(r.ConditionType, r.ConditionParams), r.ScopeClass))
            .Where(r => r.ScopeClass != r.Computed)
            .GroupBy(r => r.Computed, r => r.Id);

        var updated = 0;
        foreach (var group in reclassified)
            updated += await rules.SetScopeClassAsync(tenantId, group.Key, group.ToList(), ct);

        if (updated > 0)
            logger.LogInformation("Backfilled scope_class for {Count} alert rule(s) of tenant {TenantId}", updated, tenantId);

        return JobOutcome.Done;
    }
}
