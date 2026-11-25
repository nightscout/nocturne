using Nocturne.Core.Models.Alerts;

namespace Nocturne.Core.Models;

/// <summary>
/// Immutable snapshot of an <see cref="AlertRule"/> for evaluation, avoiding database round-trips during alert processing.
/// </summary>
/// <seealso cref="AlertRule"/>
public record AlertRuleSnapshot(Guid Id, Guid TenantId, string Name, AlertConditionType ConditionType,
    string ConditionParams, AlertRuleSeverity Severity, string ClientConfiguration, int SortOrder,
    bool AutoResolveEnabled, string? AutoResolveParams, bool AllowThroughDnd = false,
    RuleScopeClass ScopeClass = RuleScopeClass.Undirected, string OnUnknown = AlertRuleOnUnknown.Hold,
    int? GlucoseFreshnessMinutes = null, DateTime? EnabledAt = null);

/// <summary>
/// Immutable snapshot of a single delivery channel attached to an <see cref="AlertRule"/>.
/// Replaces the legacy schedule/escalation-step/step-channel chain — channels live directly
/// on the rule and are dispatched in parallel when the rule fires.
/// </summary>
/// <param name="Secret">
/// Webhook signing secret as ciphertext; the dispatching provider decrypts it. Null for every
/// other channel type and for a webhook whose receiver does not verify signatures.
/// </param>
/// <param name="AddedBySubjectId">
/// Who added a channel whose destination was typed in. Null for every other channel type, and for
/// one saved by no member or before the column existed.
/// </param>
/// <param name="StopsAt">
/// When such a channel stops delivering: the end of the access of the member who added it. Null
/// when it does not stop.
/// </param>
/// <param name="RecipientSubjectId">
/// The member a direct message's linked chat account belonged to when the channel was saved, so the
/// channel can still name them once the link is gone.
/// </param>
public record AlertRuleChannelSnapshot(
    Guid Id,
    Guid AlertRuleId,
    Nocturne.Core.Models.Alerts.ChannelType ChannelType,
    string Destination,
    string? DestinationLabel,
    int SortOrder,
    string? Metadata = null,
    string? Secret = null,
    Guid? AddedBySubjectId = null,
    DateTime? StopsAt = null,
    Guid? RecipientSubjectId = null);

/// <summary>
/// A channel that carries a signing secret, with the name of the rule it belongs to.
/// </summary>
/// <param name="Secret">The stored ciphertext.</param>
public sealed record AlertChannelSecret(
    Guid AlertRuleId,
    string RuleName,
    string Destination,
    string? DestinationLabel,
    string Secret);

/// <summary>
/// A stored <see cref="AlertRule"/>, enabled or not, with what the rule editor reads beside its
/// evaluated fields.
/// </summary>
/// <param name="ReviewReason">The stored review reason, one of <see cref="AlertRuleReviewReasons"/>, or null.</param>
/// <param name="Channels">The rule's channels in <see cref="AlertRuleChannelSnapshot.SortOrder"/>.</param>
/// <param name="Unreadable">The columns <see cref="Rule"/> shows a stand-in for.</param>
public sealed record AlertRuleRecord(
    AlertRuleSnapshot Rule,
    string? Description,
    bool IsEnabled,
    string? ManagedBy,
    string? ReviewReason,
    IReadOnlyList<AlertRuleChannelSnapshot> Channels,
    AlertRuleUnreadableValues Unreadable = AlertRuleUnreadableValues.None);

/// <summary>
/// A tenant's enabled rules, with the enabled rules left out because their stored condition type or
/// severity names no member the engine has.
/// </summary>
public sealed record EnabledAlertRules(
    IReadOnlyList<AlertRuleSnapshot> Rules,
    IReadOnlyList<UnreadableAlertRule> Unreadable);

/// <summary>
/// An enabled rule that cannot be read into an <see cref="AlertRuleSnapshot"/> because its stored
/// condition type, severity or scope class names no member the engine has, with the columns that
/// can be read.
/// </summary>
/// <param name="StoredConditionType">The stored condition type, whether or not it names a member.</param>
/// <param name="StoredSeverity">The stored severity, whether or not it names a member.</param>
/// <param name="Severity">
/// The stored severity when it names a member, else <see cref="AlertRuleSeverity.Critical"/>, so
/// that a rule the engine cannot read is not silenced by Do Not Disturb.
/// </param>
/// <param name="StoredScopeClass">The stored scope class, whether or not it names a member.</param>
/// <param name="ScopeClass">
/// The stored scope class when it names a member, else <see cref="RuleScopeClass.Undirected"/>.
/// </param>
public sealed record UnreadableAlertRule(
    Guid Id,
    string Name,
    string StoredConditionType,
    string ConditionParams,
    string StoredSeverity,
    AlertRuleSeverity Severity,
    string StoredScopeClass,
    RuleScopeClass ScopeClass,
    bool AllowThroughDnd);

/// <summary>
/// Immutable snapshot of a live alert instance.
/// </summary>
/// <seealso cref="AlertExcursion"/>
public record AlertInstanceSnapshot(Guid Id, Guid TenantId, Guid AlertExcursionId,
    string Status, DateTime TriggeredAt,
    DateTime? SnoozedUntil, int SnoozeCount);

/// <summary>The instance an excursion's opening raised, and whether Do Not Disturb silenced it.</summary>
public record OpenedInstance(Guid Id, bool Suppressed);

/// <summary>
/// Request to update an existing alert instance (snooze, status change).
/// </summary>
public record UpdateAlertInstanceRequest(Guid TenantId, Guid Id, string? Status = null,
    DateTime? SnoozedUntil = null, int? SnoozeCount = null);

/// <summary>
/// Snapshot of an <see cref="AlertExcursion"/> in the hysteresis cooldown period, used to check if cooldown has elapsed.
/// </summary>
public record HysteresisExcursionSnapshot(Guid Id, Guid TenantId, Guid AlertRuleId, DateTime? HysteresisStartedAt);

/// <summary>
/// The <see cref="AlertRule"/> an <see cref="AlertExcursion"/> belongs to, and whether the
/// excursion is the row of a notice rather than of its condition: that the rule cannot be
/// evaluated, or that its hold reached the engine's bound.
/// </summary>
public record ExcursionRuleSnapshot(
    Guid AlertRuleId, string Name, bool IsUnevaluableNotice = false, bool IsHeldNotice = false)
{
    public bool IsNotice => IsUnevaluableNotice || IsHeldNotice;
}

/// <summary>
/// A standing notice about a rule (that it cannot be evaluated, or that its hold reached the
/// bound) and whether it was dispatched.
/// </summary>
public record NoticeRecorded(Guid ExcursionId, Guid InstanceId, bool Dispatched);

/// <summary>
/// A notice that a rule's hold reached the bound that still stands: the rule, the instant the hold
/// it is for began, and whether it was dispatched.
/// </summary>
public record StandingHeldNotice(
    Guid ExcursionId, Guid InstanceId, Guid AlertRuleId, DateTime HeldSince, bool Dispatched);

/// <summary>
/// A rule's hold as its stored tracker state records it: when it began, when the state was last
/// decided, the facts it waits for, and the state string.
/// </summary>
public record HeldRuleState(Guid AlertRuleId, DateTime HeldSince, DateTime UpdatedAt, string? HeldFacts, string State)
{
    /// <summary>
    /// Whether an engine that does not record holds wrote the state after the hold began, so the
    /// engine restarts the hold at its next held evaluation (docs/alerts/engine-semantics.md §6.4):
    /// decided after the hold began and before its <paramref name="bound"/>, or active past it.
    /// </summary>
    public bool WrittenOver(TimeSpan bound) =>
        (UpdatedAt > HeldSince && UpdatedAt < HeldSince + bound)
        || (UpdatedAt >= HeldSince + bound && State == "active");
}

/// <summary>
/// A notice that a rule cannot be evaluated that still stands: the rule, the version of its
/// condition the notice was sent for, and whether it was dispatched.
/// </summary>
public record StandingUnevaluableNotice(
    Guid ExcursionId, Guid InstanceId, Guid AlertRuleId, string ConditionVersion, bool Dispatched);

/// <summary>
/// Pairing of an open excursion with its owning rule, restricted to rules
/// that have <see cref="AlertRule.AutoResolveEnabled"/> set. Returned by
/// <see cref="Nocturne.Core.Contracts.Alerts.IAlertRepository.GetAutoResolveExcursionsAsync"/>
/// for periodic auto-resolve evaluation by the alert sweep.
/// </summary>
public record AutoResolveExcursionSnapshot(
    Guid ExcursionId,
    Guid TenantId,
    AlertRuleSnapshot Rule);

/// <summary>
/// Tenant-level context for alert evaluation, providing subject identity and data freshness.
/// </summary>
/// <param name="SubjectName">The patient's preferred name; null when there is none or it is blank.</param>
public record TenantAlertContext(Guid TenantId, string? SubjectName, string? Slug, string? DisplayName,
    bool IsActive, DateTime? LastReadingAt)
{
    /// <summary>The name an alert shows: the preferred name, else the tenant's display name.</summary>
    public string? AlertSubjectName => SubjectName ?? DisplayName;
}

/// <summary>
/// Snapshot of a snoozed alert instance, combining instance and rule data for post-snooze re-evaluation.
/// </summary>
public record SnoozedInstanceSnapshot(Guid InstanceId, Guid TenantId, Guid AlertExcursionId,
    string Status, int SnoozeCount,
    Guid AlertRuleId, AlertConditionType ConditionType, string ConditionParams, string ClientConfiguration);

/// <summary>
/// Snapshot of the tenant's <c>tenant_alert_settings</c> row used by the enricher to resolve
/// <b>scheduled</b> DND (ADR 0004 D5: manual DND moved to a <c>scope=all</c> <c>dnd_windows</c>
/// row, resolved separately). <see cref="Resolve"/> projects the active scheduled window, if any.
/// </summary>
/// <param name="DndScheduleEnabled">True when a recurring scheduled DND window is configured.</param>
/// <param name="DndScheduleStart">Local-time start of the scheduled window.</param>
/// <param name="DndScheduleEnd">Local-time end (cross-midnight allowed: start &gt; end interpreted as wrapping).</param>
public record TenantAlertSettingsSnapshot(
    bool DndScheduleEnabled,
    TimeOnly? DndScheduleStart,
    TimeOnly? DndScheduleEnd)
{
    /// <summary>An "everything off" snapshot used when no row exists for the tenant.</summary>
    public static TenantAlertSettingsSnapshot Empty { get; } =
        new(false, null, null);

    /// <summary>
    /// The active DND projection produced by <see cref="Resolve"/>. <c>null</c> when scheduled
    /// DND is off. <see cref="StartedAt"/> is always meaningful when this projection is non-null.
    /// </summary>
    public sealed record ActiveProjection(DateTime StartedAt, string Source);

    /// <summary>
    /// Computes whether scheduled DND is currently active. The scheduled window is interpreted in
    /// <paramref name="timezoneId"/> (the patient's IANA timezone from
    /// <see cref="V4.PatientRecord.Timezone"/>); null or unparseable falls back to UTC. Returns
    /// <c>null</c> when scheduled DND is off (manual mutes are resolved from <c>dnd_windows</c>).
    /// </summary>
    public ActiveProjection? Resolve(DateTime nowUtc, string? timezoneId)
    {
        // Scheduled path
        if (DndScheduleEnabled && DndScheduleStart is { } start && DndScheduleEnd is { } end)
        {
            var tz = TimeZoneHelper.GetTimeZoneInfoFromId(timezoneId);

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
                var startedUtc = TimeZoneHelper.LocalToUtc(windowStartedAtLocal, tz);
                return new ActiveProjection(startedUtc, "scheduled");
            }
        }

        return null;
    }
}
