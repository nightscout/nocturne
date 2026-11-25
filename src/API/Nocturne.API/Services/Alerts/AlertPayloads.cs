using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;

namespace Nocturne.API.Services.Alerts;

internal static class AlertPayloads
{
    public static AlertPayload Build(
        AlertRuleSnapshot rule,
        SensorContext context,
        Guid tenantId,
        Guid excursionId,
        Guid instanceId,
        TenantAlertContext? tenant,
        int activeExcursionCount,
        DateTime now,
        IReadOnlyList<AlertMetPoint>? metBy = null) => new()
    {
        AlertType = rule.ConditionType,
        MetBy = metBy is { Count: > 0 } ? metBy : null,
        RuleName = rule.Name,
        GlucoseValue = context.LatestValue,
        Trend = null,
        TrendRate = context.TrendRate,
        ReadingTimestamp = context.LatestTimestamp ?? now,
        ExcursionId = excursionId,
        InstanceId = instanceId,
        TenantId = tenantId,
        SubjectName = SubjectNameOf(tenant),
        ActiveExcursionCount = activeExcursionCount,
        Severity = rule.Severity,
    };

    /// <summary>
    /// The notice that a rule cannot be evaluated: no glucose value or trend. The condition type is
    /// null for a rule whose stored one is not a member.
    /// </summary>
    public static AlertPayload Unevaluable(
        string ruleName,
        AlertConditionType? conditionType,
        AlertRuleSeverity severity,
        Guid tenantId,
        Guid excursionId,
        Guid instanceId,
        TenantAlertContext? tenant,
        int activeExcursionCount,
        DateTime now) => new()
    {
        Kind = AlertPayloadKind.Unevaluable,
        AlertType = conditionType,
        RuleName = ruleName,
        GlucoseValue = null,
        Trend = null,
        TrendRate = null,
        ReadingTimestamp = now,
        ExcursionId = excursionId,
        InstanceId = instanceId,
        TenantId = tenantId,
        SubjectName = SubjectNameOf(tenant),
        ActiveExcursionCount = activeExcursionCount,
        Severity = severity,
    };

    /// <summary>
    /// The notice that a rule's hold reached the engine's bound: as <see cref="Unevaluable"/>, with
    /// the facts it waits for, of kind <see cref="AlertPayloadKind.HeldClosed"/> when an alert was
    /// open at the bound and so is closed by it.
    /// </summary>
    public static AlertPayload Held(
        string ruleName,
        AlertConditionType conditionType,
        AlertRuleSeverity severity,
        bool closedAlert,
        IReadOnlyList<HeldFact> facts,
        Guid tenantId,
        Guid excursionId,
        Guid instanceId,
        TenantAlertContext? tenant,
        int activeExcursionCount,
        DateTime now) =>
        Unevaluable(ruleName, conditionType, severity, tenantId, excursionId, instanceId, tenant, activeExcursionCount, now)
            with
            {
                Kind = closedAlert ? AlertPayloadKind.HeldClosed : AlertPayloadKind.Held,
                HeldFacts = facts,
            };

    private static string SubjectNameOf(TenantAlertContext? tenant) => tenant?.AlertSubjectName ?? "Unknown";

    public static AlertPayload Test(
        AlertConditionType type, string ruleName, AlertRuleSeverity severity, Guid tenantId, DateTime now) => new()
    {
        AlertType = type,
        RuleName = $"[Test] {ruleName}",
        Severity = severity,
        TenantId = tenantId,
        GlucoseValue = null,
        Trend = null,
        TrendRate = null,
        ReadingTimestamp = now,
        SubjectName = "Test fire",
        ExcursionId = Guid.Empty,
        InstanceId = Guid.Empty,
        ActiveExcursionCount = 0,
    };
}
