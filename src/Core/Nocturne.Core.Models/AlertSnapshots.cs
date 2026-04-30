using Nocturne.Core.Models.Alerts;

namespace Nocturne.Core.Models;

/// <summary>
/// Immutable snapshot of an <see cref="AlertRule"/> for evaluation, avoiding database round-trips during alert processing.
/// </summary>
/// <seealso cref="AlertRule"/>
public record AlertRuleSnapshot(Guid Id, Guid TenantId, string Name, AlertConditionType ConditionType,
    string ConditionParams, int HysteresisMinutes, int ConfirmationReadings,
    AlertRuleSeverity Severity, string ClientConfiguration, int SortOrder,
    bool AutoResolveEnabled = false, string? AutoResolveParams = null);

/// <summary>
/// Immutable snapshot of an alert schedule (time window and days when an <see cref="AlertRule"/> is active).
/// </summary>
public record AlertScheduleSnapshot(Guid Id, Guid AlertRuleId, string Name, bool IsDefault,
    string? DaysOfWeek, TimeOnly? StartTime, TimeOnly? EndTime, string Timezone);

/// <summary>
/// Immutable snapshot of a single escalation step within an alert schedule.
/// </summary>
public record AlertEscalationStepSnapshot(Guid Id, Guid AlertScheduleId, int StepOrder, int DelaySeconds);

/// <summary>
/// Immutable snapshot of a live alert instance being escalated or snoozed.
/// </summary>
/// <seealso cref="AlertExcursion"/>
public record AlertInstanceSnapshot(Guid Id, Guid TenantId, Guid AlertExcursionId, Guid AlertScheduleId,
    int CurrentStepOrder, string Status, DateTime TriggeredAt,
    DateTime? NextEscalationAt, DateTime? SnoozedUntil, int SnoozeCount);

/// <summary>
/// Request to create a new alert instance when an <see cref="AlertExcursion"/> is first detected.
/// </summary>
public record CreateAlertInstanceRequest(Guid TenantId, Guid ExcursionId, Guid ScheduleId,
    int InitialStepOrder, string Status, DateTime TriggeredAt, DateTime? NextEscalationAt);

/// <summary>
/// Request to update an existing alert instance (escalation, snooze, status change).
/// </summary>
public record UpdateAlertInstanceRequest(Guid Id, int? CurrentStepOrder = null, string? Status = null,
    DateTime? NextEscalationAt = null, DateTime? SnoozedUntil = null, int? SnoozeCount = null);

/// <summary>
/// Snapshot of an <see cref="AlertExcursion"/> in the hysteresis cooldown period, used to check if cooldown has elapsed.
/// </summary>
public record HysteresisExcursionSnapshot(Guid Id, Guid AlertRuleId, DateTime? HysteresisStartedAt, int HysteresisMinutes);

/// <summary>
/// Tenant-level context for alert evaluation, providing subject identity and data freshness.
/// </summary>
public record TenantAlertContext(Guid TenantId, string SubjectName, string? Slug, string? DisplayName,
    bool IsActive, DateTime? LastReadingAt);

/// <summary>
/// Snapshot of a signal-loss <see cref="AlertRule"/> for timeout evaluation without loading the full rule.
/// </summary>
public record SignalLossRuleSnapshot(Guid Id, Guid TenantId, string ConditionParams);

/// <summary>
/// Snapshot of a snoozed alert instance, combining instance and rule data for post-snooze re-evaluation.
/// </summary>
public record SnoozedInstanceSnapshot(Guid InstanceId, Guid TenantId, Guid AlertExcursionId,
    Guid AlertScheduleId, int CurrentStepOrder, string Status, int SnoozeCount,
    Guid AlertRuleId, AlertConditionType ConditionType, string ConditionParams, string ClientConfiguration);
