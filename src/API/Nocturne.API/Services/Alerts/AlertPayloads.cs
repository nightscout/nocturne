using Nocturne.Core.Models;

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
        DateTime now) => new()
    {
        AlertType = rule.ConditionType,
        RuleName = rule.Name,
        GlucoseValue = context.LatestValue,
        Trend = null,
        TrendRate = context.TrendRate,
        ReadingTimestamp = context.LatestTimestamp ?? now,
        ExcursionId = excursionId,
        InstanceId = instanceId,
        TenantId = tenantId,
        SubjectName = tenant?.SubjectName ?? tenant?.DisplayName ?? "Unknown",
        ActiveExcursionCount = activeExcursionCount,
        Severity = rule.Severity,
    };
}
