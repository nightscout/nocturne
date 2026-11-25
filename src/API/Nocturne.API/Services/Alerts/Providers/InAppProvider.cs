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
/// The notification's <c>type</c> is the discriminator <see cref="NotificationType"/> and
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
    /// <summary>
    /// Stable notification-type discriminator used by both the provider (creation) and the
    /// action handler (archival on resolve / ack action). Keep in sync with
    /// <c>AlertActionHandler.NotificationType</c>.
    /// </summary>
    public const string NotificationType = "alert.firing";

    /// <summary>
    /// The type of the notice that a rule cannot be evaluated. Its title is the rule's name and its
    /// subtitle the label key <see cref="UnevaluableSubtitle"/>, which the web app renders. It has
    /// no actions: there is no excursion to acknowledge.
    /// </summary>
    public const string UnevaluableNotificationType = "alert.unevaluable";

    public const string UnevaluableSubtitle = "alert_unevaluable_subtitle";

    /// <summary>
    /// The type of the notice that a rule's hold reached the engine's bound, titled and without
    /// actions as <see cref="UnevaluableNotificationType"/>. Its subtitle is the label key
    /// <see cref="HeldSubtitle"/>, or <see cref="HeldClosedSubtitle"/> when it closed an alert.
    /// </summary>
    public const string HeldNotificationType = "alert.held";

    public const string HeldSubtitle = "alert_held_subtitle";

    public const string HeldClosedSubtitle = "alert_held_closed_subtitle";

    /// <summary>Every notification type an excursion or a notice can create.</summary>
    public static readonly IReadOnlyList<string> AllNotificationTypes =
        [NotificationType, UnevaluableNotificationType, HeldNotificationType];

    /// <summary>The type of the notifications the excursion, or notice, of <paramref name="rule"/> created.</summary>
    public static string NotificationTypeFor(ExcursionRuleSnapshot rule) =>
        rule.IsUnevaluableNotice ? UnevaluableNotificationType
        : rule.IsHeldNotice ? HeldNotificationType
        : NotificationType;

    private static string NotificationTypeFor(AlertPayloadKind kind) => kind switch
    {
        AlertPayloadKind.Unevaluable => UnevaluableNotificationType,
        AlertPayloadKind.Held or AlertPayloadKind.HeldClosed => HeldNotificationType,
        _ => NotificationType,
    };

    private static string? NoticeSubtitle(AlertPayloadKind kind) => kind switch
    {
        AlertPayloadKind.Unevaluable => UnevaluableSubtitle,
        AlertPayloadKind.Held => HeldSubtitle,
        AlertPayloadKind.HeldClosed => HeldClosedSubtitle,
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

        try
        {
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
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogError(ex,
                "Failed to create InApp notification for instance {InstanceId}, user {UserId}",
                payload.InstanceId, userId);
            throw;
        }
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
