namespace Nocturne.Core.Contracts;

/// <summary>
/// Service for matching connector food entries to treatments
/// </summary>
public interface IMealMatchingService
{
    /// <summary>
    /// Process newly imported food entries and create match notifications
    /// </summary>
    Task ProcessNewFoodEntriesAsync(string userId, IEnumerable<Guid> foodEntryIds, CancellationToken ct = default);

    /// <summary>
    /// Process a newly created treatment and create match notifications for pending food entries
    /// </summary>
    Task ProcessNewTreatmentAsync(string userId, Guid treatmentId, CancellationToken ct = default);

    /// <summary>
    /// Accept a meal match, creating a TreatmentFood entry
    /// </summary>
    /// <param name="foodEntryId">The connector food entry ID</param>
    /// <param name="treatmentId">The treatment to link to</param>
    /// <param name="carbs">The carb amount (may be adjusted from original)</param>
    /// <param name="timeOffsetMinutes">Minutes offset from treatment time (0 = ate at bolus time)</param>
    Task AcceptMatchAsync(Guid foodEntryId, Guid treatmentId, decimal carbs, int timeOffsetMinutes, CancellationToken ct = default);

    /// <summary>
    /// Dismiss a match, marking the food entry as standalone
    /// </summary>
    Task DismissMatchAsync(Guid foodEntryId, CancellationToken ct = default);

    /// <summary>
    /// Get suggested matches for pending food entries in a date range
    /// </summary>
    /// <param name="from">Start of date range</param>
    /// <param name="to">End of date range</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>List of suggested matches with scores</returns>
    Task<IReadOnlyList<SuggestedMealMatchResult>> GetSuggestionsAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default);
}

/// <summary>
/// A suggested meal match result
/// </summary>
public record SuggestedMealMatchResult(
    Guid FoodEntryId,
    string? FoodName,
    string? MealName,
    decimal Carbs,
    DateTimeOffset ConsumedAt,
    Guid TreatmentId,
    decimal TreatmentCarbs,
    long TreatmentMills,
    double MatchScore
);
