using Nocturne.Core.Contracts.Monitoring;
using Nocturne.Core.Contracts.Notifications;

namespace Nocturne.API.Services.NotificationActionHandlers;

/// <summary>
/// Handles user actions (accept/dismiss) on <c>tracker.suggested_match</c> in-app notifications
/// produced by the tracker subsystem. Accept completes the current tracker instance and starts
/// a new one. Both actions return the orchestrator's archive directive directly from the
/// tracker service.
/// </summary>
/// <seealso cref="INotificationActionHandler"/>
public class TrackerSuggestionActionHandler(
    ITrackerSuggestionService trackerSuggestionService,
    ILogger<TrackerSuggestionActionHandler> logger
) : INotificationActionHandler
{
    public string NotificationType => "tracker.suggested_match";

    public async Task<NotificationActionResult> HandleAsync(
        Guid notificationId,
        string actionId,
        string userId,
        string? sourceId,
        Dictionary<string, object>? metadata,
        CancellationToken cancellationToken = default)
    {
        switch (actionId.ToLowerInvariant())
        {
            case "accept":
                return await trackerSuggestionService.AcceptSuggestionAsync(
                    notificationId, userId, cancellationToken);

            case "dismiss":
                return await trackerSuggestionService.DismissSuggestionAsync(
                    notificationId, userId, cancellationToken);

            default:
                logger.LogWarning(
                    "Unknown action {ActionId} for tracker suggestion notification {NotificationId}",
                    actionId, notificationId);
                return NotificationActionResult.NotHandled;
        }
    }
}
