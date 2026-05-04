using Nocturne.API.Services.Treatments;
using Nocturne.Core.Contracts.Notifications;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Core.Models;

namespace Nocturne.API.Services.NotificationActionHandlers;

/// <summary>
/// Handles user actions (accept/reject/dismiss) on <c>meal_matching.suggested_match</c>
/// in-app notifications produced by <see cref="MealMatchingService"/>. Accept is deferred to
/// <c>MealMatchingController</c>; reject and dismiss are handled here by signalling archive.
/// </summary>
/// <seealso cref="INotificationActionHandler"/>
public class MealMatchActionHandler(
    IConnectorFoodEntryRepository foodEntryRepository,
    ILogger<MealMatchActionHandler> logger
) : INotificationActionHandler
{
    public string NotificationType => "meal_matching.suggested_match";

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
                // Accept action is handled via MealMatchingController; archive here.
                return NotificationActionResult.Completed;

            case "dismiss":
                if (sourceId != null && Guid.TryParse(sourceId, out var foodEntryId))
                {
                    await foodEntryRepository.UpdateStatusAsync(
                        foodEntryId,
                        ConnectorFoodEntryStatus.Standalone,
                        cancellationToken);
                }
                return NotificationActionResult.Dismissed;

            case "review":
                // Review opens a dialog client-side, no archive
                return NotificationActionResult.HandledNoArchive;

            default:
                logger.LogWarning(
                    "Unknown action {ActionId} for meal match notification {NotificationId}",
                    actionId, notificationId);
                return NotificationActionResult.NotHandled;
        }
    }
}
