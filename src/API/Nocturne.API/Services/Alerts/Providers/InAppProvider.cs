using Nocturne.Core.Contracts.Notifications;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;

namespace Nocturne.API.Services.Alerts.Providers;

/// <summary>
/// Delivery channel that surfaces an alert as an in-app notification (bell/toast).
/// Persists through <see cref="IInAppNotificationService"/> so the notification
/// outlives the SignalR push and remains in the user's notification list until they
/// acknowledge, dismiss, or the excursion auto-archives on resolve.
/// </summary>
/// <remarks>
/// The notification's <c>type</c> is the discriminator <see cref="InAppNotificationType.AlertFiring"/> and
/// <c>sourceId</c> is the excursion id. <c>CreateNotificationAsync</c> does NOT dedupe by
/// (type, sourceId), so each escalation step appends a fresh row — the user sees one
/// notification per step. The pair is consumed by <c>AlertActionHandler</c> (action
/// archival on ack/dismiss) and by <c>ExcursionResolutionHandler</c>
/// (<c>ArchiveBySourceAsync</c> on close, which clears the most-recently-active row per
/// recipient).
/// </remarks>
internal sealed class InAppProvider(
    IInAppNotificationService notificationService,
    ILogger<InAppProvider> logger)
{
    /// <summary>Every notification type an excursion or a notice can create.</summary>
    public static readonly IReadOnlyList<string> AllNotificationTypes =
    [
        InAppNotificationType.AlertFiring.ToWireString(),
        InAppNotificationType.AlertUnevaluable.ToWireString(),
        InAppNotificationType.AlertHeld.ToWireString(),
    ];

    /// <summary>The type of the notifications the excursion, or notice, of <paramref name="rule"/> created.</summary>
    public static string NotificationTypeFor(ExcursionRuleSnapshot rule) =>
        rule.IsUnevaluableNotice ? InAppNotificationType.AlertUnevaluable.ToWireString()
        : rule.IsHeldNotice ? InAppNotificationType.AlertHeld.ToWireString()
        : InAppNotificationType.AlertFiring.ToWireString();

    private static string NotificationTypeFor(AlertPayloadKind kind) => kind switch
    {
        AlertPayloadKind.Unevaluable or AlertPayloadKind.PartlyUnevaluable => InAppNotificationType.AlertUnevaluable.ToWireString(),
        AlertPayloadKind.Held or AlertPayloadKind.HeldClosed => InAppNotificationType.AlertHeld.ToWireString(),
        _ => InAppNotificationType.AlertFiring.ToWireString(),
    };

    private static string? NoticeSubtitle(AlertPayloadKind kind) => kind switch
    {
        AlertPayloadKind.Unevaluable => NotificationLabel.AlertUnevaluableSubtitle.ToWireString(),
        AlertPayloadKind.PartlyUnevaluable => NotificationLabel.AlertPartlyUnevaluableSubtitle.ToWireString(),
        AlertPayloadKind.Held => NotificationLabel.AlertHeldSubtitle.ToWireString(),
        AlertPayloadKind.HeldClosed => NotificationLabel.AlertHeldClosedSubtitle.ToWireString(),
        _ => null,
    };

    public const string AckActionId = "ack";
    public const string DismissActionId = "dismiss";

    private static readonly List<NotificationActionDto> AlertActions = new()
    {
        new NotificationActionDto { ActionId = AckActionId, Label = "Acknowledge" },
        new NotificationActionDto { ActionId = DismissActionId, Label = "Dismiss" },
    };

    /// <summary>
    /// Creates the notification for the subject <paramref name="destination"/> names. False when it
    /// names no subject: nothing is created, so there is no one the alert reached.
    /// </summary>
    public async Task<bool> SendAsync(string destination, AlertPayload payload, CancellationToken ct)
    {
        if (!Guid.TryParse(destination, out var subjectId))
        {
            logger.LogWarning("InApp delivery for instance {InstanceId} names no subject", payload.InstanceId);
            return false;
        }

        var userId = subjectId.ToString();

        var notice = payload.Kind.IsNotice();
        var urgency = MapSeverity(payload);

        await notificationService.CreateNotificationAsync(
            userId: userId,
            type: NotificationTypeFor(payload.Kind),
            title: payload.RuleName,
            category: NotificationCategory.Alert,
            urgency: urgency,
            subtitle: notice ? NoticeSubtitle(payload.Kind) : BuildSubtitle(payload),
            sourceId: payload.ExcursionId.ToString(),
            actions: notice ? null : AlertActions,
            cancellationToken: ct);

        logger.LogDebug(
            "InApp notification created for instance {InstanceId} (user {UserId}, urgency {Urgency})",
            payload.InstanceId, userId, urgency);
        return true;
    }

    private static NotificationUrgency MapSeverity(AlertPayload payload) => payload.Severity switch
    {
        AlertRuleSeverity.Critical => NotificationUrgency.Urgent,
        AlertRuleSeverity.Warning => NotificationUrgency.Warn,
        AlertRuleSeverity.Info => NotificationUrgency.Info,
        _ => NotificationUrgency.Warn,
    };

    private static string? BuildSubtitle(AlertPayload payload)
    {
        if (payload.GlucoseValue is null) return null;
        var arrow = TrendArrow(payload.TrendRate);
        return arrow is null
            ? $"{payload.GlucoseValue:0}"
            : $"{payload.GlucoseValue:0} {arrow}";
    }

    private static string? TrendArrow(decimal? trendRate) => trendRate switch
    {
        null => null,
        >= 3m => "↑↑",
        >= 1m => "↑",
        >= 0.5m => "↗",
        > -0.5m => "→",
        > -1m => "↘",
        > -3m => "↓",
        _ => "↓↓",
    };
}
