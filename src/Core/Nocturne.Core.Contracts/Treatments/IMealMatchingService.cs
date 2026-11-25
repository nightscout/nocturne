using Nocturne.Core.Contracts.Connectors;
using Nocturne.Core.Models;

namespace Nocturne.Core.Contracts.Treatments;

/// <summary>
/// Service for matching connector food entries to treatments.
/// </summary>
/// <seealso cref="IConnectorFoodEntryService"/>
/// <seealso cref="ITreatmentFoodService"/>
/// <seealso cref="IInAppNotificationService"/>
public interface IMealMatchingService
{
    /// <summary>
    /// Notification type for a suggested match. Registered in <c>BuiltInNotificationTemplates</c>,
    /// which supplies its category, icon and source.
    /// </summary>
    const string SuggestedMatchNotificationType = "meal_matching.suggested_match";

    /// <summary>
    /// Process newly imported food entries and create match notifications.
    /// </summary>
    /// <param name="userId">The user ID for notification delivery.</param>
    /// <param name="foodEntryIds">IDs of the newly imported food entries to evaluate.</param>
    Task ProcessNewFoodEntriesAsync(string userId, IEnumerable<Guid> foodEntryIds, CancellationToken ct = default);

    /// <summary>
    /// Dismiss a match, marking the pending food entry as standalone
    /// </summary>
    /// <returns>
    /// What the dismiss did. See <see cref="IConnectorFoodEntryRepository.DismissAsync"/> for the outcomes.
    /// </returns>
    Task<DismissMatchOutcome> DismissMatchAsync(Guid foodEntryId, CancellationToken ct = default);

    /// <summary>
    /// Withdraws any live match suggestion for a food entry that no longer exists upstream.
    /// </summary>
    /// <param name="userId">The user whose suggestion is withdrawn.</param>
    /// <param name="foodEntryId">The connector food entry that has gone away.</param>
    Task WithdrawSuggestionAsync(string userId, Guid foodEntryId, CancellationToken ct = default);

    /// <summary>
    /// Get suggested matches for pending food entries in a date range
    /// </summary>
    /// <param name="from">Start of date range</param>
    /// <param name="to">End of date range</param>
    /// <returns>List of suggested matches with scores</returns>
    Task<IReadOnlyList<SuggestedMealMatchResult>> GetSuggestionsAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default);
}

/// <summary>
/// A suggested meal match pairing a connector food entry with a carb intake.
/// </summary>
/// <param name="FoodEntryId">The ID of the pending <see cref="ConnectorFoodEntry"/>.</param>
/// <param name="FoodName">Individual food item name, if available.</param>
/// <param name="MealName">Meal name from the connector, if available.</param>
/// <param name="Carbs">Carbohydrate amount in grams from the food entry.</param>
/// <param name="ConsumedAt">When the food was consumed according to the connector.</param>
/// <param name="CarbIntakeId">The ID of the candidate matching carb intake.</param>
/// <param name="CarbIntakeCarbs">Carbohydrate amount in grams recorded on the carb intake.</param>
/// <param name="CarbIntakeMills">Carb intake timestamp in Unix milliseconds.</param>
/// <param name="MatchScore">Computed match score (higher is a better match).</param>
public record SuggestedMealMatchResult(
    Guid FoodEntryId,
    string? FoodName,
    string? MealName,
    decimal Carbs,
    DateTimeOffset ConsumedAt,
    Guid CarbIntakeId,
    decimal CarbIntakeCarbs,
    long CarbIntakeMills,
    double MatchScore
);
