namespace Nocturne.Core.Models.Alerts;

/// <summary>
/// What a snooze on an alert instance means. A snoozed instance produces no notification on any
/// channel (push, in-app, webhook, chat bot, Home Assistant, device actuation) until
/// <c>SnoozedUntil</c>; it stays visible as active, and it is neither an acknowledgement nor a
/// change to the excursion — <c>alert_state</c> conditions, and so escalation rules built on
/// them, still see it as firing and unacknowledged.
/// </summary>
/// <remarks>
/// The window is exclusive at its end: at <c>SnoozedUntil</c> the instance is live again, so no
/// reader can hold a notification past it. Re-notifying once it lapses is the sweep's job
/// (<c>AlertSnoozeService.ResumeAsync</c>).
/// </remarks>
public static class AlertSnooze
{
    public static bool IsSnoozed(DateTime? snoozedUntil, DateTime now) =>
        snoozedUntil is { } until && until > now;

    /// <summary>
    /// <paramref name="snoozedUntil"/> while the snooze is in force, otherwise null — the value
    /// clients are given, so a lapsed-but-not-yet-swept snooze never reads as snoozed.
    /// </summary>
    public static DateTime? ActiveUntil(DateTime? snoozedUntil, DateTime now) =>
        IsSnoozed(snoozedUntil, now) ? snoozedUntil : null;
}

public enum SnoozeResult
{
    Snoozed,
    NotFound,

    /// <summary>The instance or its excursion is already resolved.</summary>
    NotActive,

    /// <summary>
    /// The instance has used the rule's <c>snooze.maxCount</c>, a count manual snoozes and
    /// smart-snooze extensions share.
    /// </summary>
    LimitReached,
}

public sealed record SnoozeOutcome(SnoozeResult Result, DateTime? SnoozedUntil = null);
