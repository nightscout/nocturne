using System.Globalization;
using Nocturne.API.Extensions;
using Nocturne.API.Services.Treatments;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Core.Contracts.Notifications;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;

namespace Nocturne.API.Services.NotificationActionHandlers;

/// <summary>
/// Handles user actions (accept/review/dismiss) on <c>meal_matching.suggested_match</c>
/// in-app notifications produced by <see cref="MealMatchingService"/>.
/// Accept links the food entry to the suggested carb intake through <see cref="IMealMatchLinker"/>,
/// as the meals page does: the entry's current carbs, at the carb intake's time. It reads only the
/// carb intake id from the notification, so carbs the entry has changed since are not written
/// from a stale snapshot. It needs <see cref="Scope.TreatmentsReadWrite"/>, the scope
/// <c>MealMatchingController.AcceptMatch</c> requires. A caller without that scope is refused
/// through <see cref="NotificationActionResult.RefusedFor"/>. The handler declines, so the notification
/// stays and the caller is told the action failed, for a notification
/// that lacks the carb intake, and a food entry or carb intake that is missing. A food entry
/// already resolved elsewhere archives the stale suggestion as
/// <see cref="NotificationArchiveReason.ConditionMet"/>.
/// Review opens the dialog where a person picks the carbs and the time.
/// Dismiss resolves the food entry as the meals page does and signals archive. It needs
/// <see cref="Scope.FoodReadWrite"/>, the scope <c>MealMatchingController.DismissMatch</c> requires.
/// The handler declines for a caller without it, for a food entry that is missing and for one
/// resolved elsewhere, and the orchestrator archives the notification as dismissed without writing
/// the food entry.
/// </summary>
/// <seealso cref="INotificationActionHandler"/>
public class MealMatchActionHandler(
    IConnectorFoodEntryRepository foodEntryRepository,
    IMealMatchLinker linker,
    IHttpContextAccessor httpContextAccessor,
    ILogger<MealMatchActionHandler> logger
) : INotificationActionHandler
{
    public string NotificationType => IMealMatchingService.SuggestedMatchNotificationType;

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
                if (httpContextAccessor.HttpContext?.HasScope(Scope.TreatmentsReadWrite) != true)
                {
                    return NotificationActionResult.RefusedFor(Scope.TreatmentsReadWrite);
                }

                if (!Guid.TryParse(sourceId, out var acceptedFoodEntryId)
                    || !TryReadCarbIntakeId(metadata, out var carbIntakeId))
                {
                    logger.LogWarning(
                        "Meal match notification {NotificationId} does not carry what an accept needs",
                        notificationId);
                    return NotificationActionResult.NotHandled;
                }

                var foodEntry = await foodEntryRepository.GetByIdAsync(acceptedFoodEntryId, cancellationToken);
                if (foodEntry is null)
                {
                    return NotificationActionResult.NotHandled;
                }

                var accepted = await linker.LinkAsync(
                    acceptedFoodEntryId, carbIntakeId, foodEntry.Carbs, timeOffsetMinutes: 0, cancellationToken);
                return accepted switch
                {
                    AcceptMatchOutcome.Linked or AcceptMatchOutcome.AlreadyLinked =>
                        NotificationActionResult.Completed,
                    AcceptMatchOutcome.AlreadyResolved =>
                        new NotificationActionResult(true, NotificationArchiveReason.ConditionMet),
                    AcceptMatchOutcome.NotFound => NotificationActionResult.NotHandled,
                    _ => throw new InvalidOperationException($"Unhandled accept outcome {accepted}."),
                };

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

    private static bool TryReadCarbIntakeId(Dictionary<string, object>? metadata, out Guid carbIntakeId)
    {
        carbIntakeId = Guid.Empty;
        return metadata is not null && Guid.TryParse(Text(metadata, "carbIntakeId"), out carbIntakeId);
    }

    private static string? Text(Dictionary<string, object> metadata, string key) =>
        metadata.TryGetValue(key, out var value) ? Convert.ToString(value, CultureInfo.InvariantCulture) : null;
}
