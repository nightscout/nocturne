using Nocturne.Core.Alerts.Native;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Jobs;

namespace Nocturne.API.Services.Alerts;

/// <summary>
/// Recomputes the <c>scope_class</c> of each of a tenant's stored alert rules through
/// <see cref="IRuleScopeClassifier"/>, for scoped Do Not Disturb (ADR 0004). The controller computes
/// <c>scope_class</c> on every create and update, so only rules stored before the classifier last
/// changed differ.
/// </summary>
/// <remarks>
/// A job that returns <see cref="JobOutcome.Done"/> never comes due again for its row, so a change to
/// what <see cref="IRuleScopeClassifier"/> answers is applied to stored rules by a new
/// <see cref="JobName"/>, which enrols every tenant afresh.
/// Only rows whose recomputed class differs from the stored one are written, each distinct class in
/// one call to <see cref="IAlertRuleRepository.SetScopeClassAsync"/>, which skips a rule edited since the
/// job read it.
/// </remarks>
/// <seealso cref="RuleScopeClassifier"/>
internal sealed class RuleScopeClassBackfillJob(ILogger<RuleScopeClassBackfillJob> logger) : IScheduledJob
{
    internal const string JobName = "alert-rule-scope-class-backfill-v2";

    public string Name => JobName;

    public JobEnrolment Enrolment => JobEnrolment.PerTenant;

    public async Task<JobOutcome> RunAsync(JobRun run, CancellationToken ct)
    {
        var tenantId = run.Tenant!.TenantId;
        var classifier = run.Services.GetRequiredService<IRuleScopeClassifier>();
        var rules = run.Services.GetRequiredService<IAlertRuleRepository>();

        var reclassified = (await run.Services.GetRequiredService<IAlertRepository>().GetRuleSnapshotsAsync(tenantId, ct))
            .Select(r => (Rule: new ClassifiedRule(r.Id, r.ConditionType, r.ConditionParams),
                Computed: classifier.Classify(r.ConditionType, r.ConditionParams), r.ScopeClass))
            .Where(r => r.ScopeClass != r.Computed)
            .GroupBy(r => r.Computed, r => r.Rule);

        var updated = 0;
        foreach (var group in reclassified)
            updated += await rules.SetScopeClassAsync(tenantId, group.Key, group.ToList(), ct);

        if (updated > 0)
            logger.LogInformation("Backfilled scope_class for {Count} alert rule(s) of tenant {TenantId}", updated, tenantId);

        return JobOutcome.Done;
    }
}
