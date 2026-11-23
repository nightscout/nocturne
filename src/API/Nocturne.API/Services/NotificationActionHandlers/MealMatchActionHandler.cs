using Nocturne.API.Extensions;
using Nocturne.API.Services.Treatments;
using Nocturne.Core.Contracts.Notifications;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;

namespace Nocturne.API.Services.NotificationActionHandlers;

/// <summary>
/// Handles user actions (accept/review/dismiss) on <c>meal_matching.suggested_match</c>
/// in-app notifications produced by <see cref="MealMatchingService"/>. Accept is deferred to
/// <c>MealMatchingController</c>. Dismiss resolves the food entry as the meals page does and signals
/// archive, for a caller holding <see cref="Scope.FoodReadWrite"/>, the scope
/// <c>MealMatchingController.DismissMatch</c> requires; a caller without it, a food entry that is
/// missing and one resolved elsewhere make the handler decline, and the orchestrator archives the
/// notification as dismissed without writing the food entry.
/// </summary>
/// <seealso cref="INotificationActionHandler"/>
public class MealMatchActionHandler(
    IConnectorFoodEntryRepository foodEntryRepository,
    IHttpContextAccessor httpContextAccessor,
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
                if (httpContextAccessor.HttpContext?.HasScope(Scope.FoodReadWrite) != true)
                {
                    return NotificationActionResult.NotHandled;
                }

                if (sourceId == null || !Guid.TryParse(sourceId, out var foodEntryId))
                {
                    return NotificationActionResult.Dismissed;
                }

                var outcome = await foodEntryRepository.DismissAsync(foodEntryId, cancellationToken);
                return outcome switch
                {
                    DismissMatchOutcome.Dismissed or DismissMatchOutcome.AlreadyDismissed =>
                        NotificationActionResult.Dismissed,
                    DismissMatchOutcome.NotFound or DismissMatchOutcome.AlreadyResolved =>
                        NotificationActionResult.NotHandled,
                    _ => throw new InvalidOperationException($"Unhandled dismiss outcome {outcome}."),
                };

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
