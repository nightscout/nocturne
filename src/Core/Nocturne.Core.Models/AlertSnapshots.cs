using Nocturne.Core.Models.Alerts;

namespace Nocturne.Core.Models;

/// <summary>
/// Immutable snapshot of an <see cref="AlertRule"/> for evaluation, avoiding database round-trips during alert processing.
/// </summary>
/// <seealso cref="AlertRule"/>
public record AlertRuleSnapshot(Guid Id, Guid TenantId, string Name, AlertConditionType ConditionType,
    string ConditionParams, AlertRuleSeverity Severity, string ClientConfiguration, int SortOrder,
    bool AutoResolveEnabled, string? AutoResolveParams, bool AllowThroughDnd = false);

/// <summary>
/// Immutable snapshot of a single delivery channel attached to an <see cref="AlertRule"/>.
/// Replaces the legacy schedule/escalation-step/step-channel chain — channels live directly
/// on the rule and are dispatched in parallel when the rule fires.
/// </summary>
public record AlertRuleChannelSnapshot(
    Guid Id,
    Guid AlertRuleId,
    Nocturne.Core.Models.Alerts.ChannelType ChannelType,
    string Destination,
    string? DestinationLabel,
    int SortOrder);

/// <summary>
/// Immutable snapshot of a live alert instance.
/// </summary>
/// <seealso cref="AlertExcursion"/>
public record AlertInstanceSnapshot(Guid Id, Guid TenantId, Guid AlertExcursionId,
    string Status, DateTime TriggeredAt,
    DateTime? SnoozedUntil, int SnoozeCount);

/// <summary>
/// Request to create a new alert instance when an <see cref="AlertExcursion"/> is first detected.
/// </summary>
public record CreateAlertInstanceRequest(Guid TenantId, Guid ExcursionId,
    string Status, DateTime TriggeredAt);

/// <summary>
/// Request to update an existing alert instance (snooze, status change).
/// </summary>
public record UpdateAlertInstanceRequest(Guid Id, string? Status = null,
    DateTime? SnoozedUntil = null, int? SnoozeCount = null);

/// <summary>
/// Snapshot of an <see cref="AlertExcursion"/> in the hysteresis cooldown period, used to check if cooldown has elapsed.
/// </summary>
public record HysteresisExcursionSnapshot(Guid Id, Guid TenantId, Guid AlertRuleId, DateTime? HysteresisStartedAt);

/// <summary>
/// Pairing of an open excursion with its owning rule, restricted to rules
/// that have <see cref="AlertRule.AutoResolveEnabled"/> set. Returned by
/// <see cref="Nocturne.Core.Contracts.Alerts.IAlertRepository.GetAutoResolveExcursionsAsync"/>
/// for periodic auto-resolve evaluation by the sweep service.
/// </summary>
public record AutoResolveExcursionSnapshot(
    Guid ExcursionId,
    Guid TenantId,
    AlertRuleSnapshot Rule);

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
    string Status, int SnoozeCount,
    Guid AlertRuleId, AlertConditionType ConditionType, string ConditionParams, string ClientConfiguration);

/// <summary>
/// Snapshot of the tenant's <c>tenant_alert_settings</c> row used by the orchestrator and the
/// <c>do_not_disturb</c> condition evaluator. Both manual and scheduled DND collapse into one
/// effective state via <see cref="IsActive"/>.
/// </summary>
/// <param name="DndManualActive">True when the user has manually toggled DND on.</param>
/// <param name="DndManualUntil">UTC instant at which a manually-activated DND auto-expires; null = indefinitely.</param>
/// <param name="DndManualStartedAt">UTC instant when manual DND was most recently activated. Used as the anchor for <c>do_not_disturb</c>'s sustained <c>for_minutes</c> when manual DND is the active path.</param>
/// <param name="DndScheduleEnabled">True when a recurring scheduled DND window is configured.</param>
/// <param name="DndScheduleStart">Local-time start of the scheduled window.</param>
/// <param name="DndScheduleEnd">Local-time end (cross-midnight allowed: start &gt; end interpreted as wrapping).</param>
/// <param name="Timezone">IANA timezone (e.g. <c>Europe/London</c>) used to interpret the schedule.</param>
public record TenantAlertSettingsSnapshot(
    bool DndManualActive,
    DateTime? DndManualUntil,
    DateTime? DndManualStartedAt,
    bool DndScheduleEnabled,
    TimeOnly? DndScheduleStart,
    TimeOnly? DndScheduleEnd,
    string Timezone)
{
    /// <summary>An "everything off" snapshot used when no row exists for the tenant.</summary>
    public static TenantAlertSettingsSnapshot Empty { get; } =
        new(false, null, null, false, null, null, "UTC");

    /// <summary>
    /// The active DND projection produced by <see cref="Resolve"/>. <c>null</c> when DND is off.
    /// <see cref="StartedAt"/> is always meaningful when this projection is non-null — callers
    /// don't need to guard against placeholder timestamps.
    /// </summary>
    public sealed record ActiveProjection(DateTime StartedAt, string Source);

    /// <summary>
    /// Computes whether DND is currently active by either path (manual with optional auto-expire,
    /// or scheduled window). The manual path takes precedence for the snapshot's <c>StartedAt</c>
    /// anchor when both paths are active simultaneously. Returns <c>null</c> when DND is off.
    /// </summary>
    public ActiveProjection? Resolve(DateTime nowUtc)
    {
        // Manual path
        var manualActive = DndManualActive
            && (DndManualUntil is null || nowUtc < DndManualUntil.Value);
        if (manualActive)
        {
            // StartedAt missing on legacy rows defaults to now — still produces a sensible
            // ForMinutes anchor (newly-active DND has zero elapsed time).
            return new ActiveProjection(DndManualStartedAt ?? nowUtc, "manual");
        }

        // Scheduled path
        if (DndScheduleEnabled && DndScheduleStart is { } start && DndScheduleEnd is { } end)
        {
            TimeZoneInfo tz;
            try { tz = TimeZoneInfo.FindSystemTimeZoneById(Timezone); }
            catch { tz = TimeZoneInfo.Utc; }

            var localNow = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, tz);
            var nowTime = TimeOnly.FromDateTime(localNow);

            bool inWindow;
            DateTime windowStartedAtLocal;
            if (start <= end)
            {
                // Same-day window: 22:00–23:30 etc.
                inWindow = nowTime >= start && nowTime < end;
                windowStartedAtLocal = localNow.Date + start.ToTimeSpan();
            }
            else
            {
                // Cross-midnight window: 22:00–07:00.
                inWindow = nowTime >= start || nowTime < end;
                // If we're past midnight (in the second leg), the window started yesterday at `start`.
                windowStartedAtLocal = nowTime < end
                    ? localNow.Date.AddDays(-1) + start.ToTimeSpan()
                    : localNow.Date + start.ToTimeSpan();
            }

            if (inWindow)
            {
                // DST: on a spring-forward day the local "start" timestamp can fall in the
                // skipped hour, in which case ConvertTimeToUtc throws. Treat that as
                // "started at the DST transition boundary" by bumping forward by the gap —
                // the practical impact on a `for_minutes` anchor is at most one hour and
                // matches what the user perceives ("DND has been on since the schedule
                // boundary"). Falling back to nowUtc would wipe the elapsed-time anchor
                // entirely and is a worse failure mode.
                var localStart = DateTime.SpecifyKind(windowStartedAtLocal, DateTimeKind.Unspecified);
                if (tz.IsInvalidTime(localStart))
                {
                    var rule = tz.GetAdjustmentRules()
                        .FirstOrDefault(r => r.DateStart <= localStart && localStart <= r.DateEnd);
                    var delta = rule?.DaylightDelta ?? TimeSpan.FromHours(1);
                    localStart = localStart + delta;
                }
                DateTime startedUtc;
                try
                {
                    startedUtc = TimeZoneInfo.ConvertTimeToUtc(localStart, tz);
                }
                catch (ArgumentException)
                {
                    // Belt-and-braces: still throws on an ambiguous-time edge that
                    // IsInvalidTime didn't flag. Fall back to nowUtc rather than failing.
                    startedUtc = nowUtc;
                }
                return new ActiveProjection(startedUtc, "scheduled");
            }
        }

        return null;
    }
}
