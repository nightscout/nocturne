using Nocturne.Core.Models;

namespace Nocturne.Core.Contracts.Notifications;

/// <summary>
/// Outcome returned by an <see cref="INotificationActionHandler"/>. The orchestrator
/// (<see cref="IInAppNotificationService.ExecuteActionAsync"/>) owns the side effect of
/// archiving so handlers stay free of a back-reference to the notification service —
/// preventing a captive-dependency cycle when handlers are injected as
/// <c>IEnumerable&lt;INotificationActionHandler&gt;</c>.
/// </summary>
/// <param name="Handled">Whether the action was understood and processed successfully.</param>
/// <param name="Archive">If non-null, the orchestrator archives the notification with this reason.</param>
public readonly record struct NotificationActionResult(
    bool Handled,
    NotificationArchiveReason? Archive = null)
{
    public static NotificationActionResult NotHandled => new(false);
    public static NotificationActionResult HandledNoArchive => new(true);
    public static NotificationActionResult Completed => new(true, NotificationArchiveReason.Completed);
    public static NotificationActionResult Dismissed => new(true, NotificationArchiveReason.Dismissed);
}

/// <summary>
/// Handles notification-specific actions. Registered per notification type string.
/// </summary>
public interface INotificationActionHandler
{
    /// <summary>
    /// The notification type string this handler is responsible for (e.g., "SuggestedMealMatch")
    /// </summary>
    string NotificationType { get; }

    /// <summary>
    /// Handle an action on a notification of this type.
    /// </summary>
    /// <param name="notificationId">The notification ID being acted on.</param>
    /// <param name="actionId">The specific action to execute (e.g., "accept", "dismiss").</param>
    /// <param name="userId">The user executing the action.</param>
    /// <param name="sourceId">The source entity ID from the notification, if any.</param>
    /// <param name="metadata">Notification-specific metadata, if any.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<NotificationActionResult> HandleAsync(
        Guid notificationId,
        string actionId,
        string userId,
        string? sourceId,
        Dictionary<string, object>? metadata,
        CancellationToken cancellationToken = default);
}
