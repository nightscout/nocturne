using Nocturne.Core.Contracts.Notifications;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Configuration;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Abstractions;

namespace Nocturne.API.Services.Treatments;

/// <summary>
/// Matches connector food entries (primarily from MyFitnessPal) to existing carb intake records
/// within a configurable time window, linking them via <see cref="ITreatmentFoodService"/>. Raises
/// an in-app notification for new matches so users can review auto-linked meals.
/// </summary>
/// <seealso cref="IMealMatchingService"/>
public class MealMatchingService : IMealMatchingService
{
    /// <summary>
    /// Notification type for a suggested match. Registered in <c>BuiltInNotificationTemplates</c>,
    /// which supplies its category, icon and source.
    /// </summary>
    public const string SuggestedMatchNotificationType = "meal_matching.suggested_match";

    /// <summary>Cap on carb intake records pulled per matching window.</summary>
    private const int CandidateLimit = 1000;

    private readonly IConnectorFoodEntryRepository _foodEntryRepository;
    private readonly ICarbIntakeRepository _carbIntakeRepository;
    private readonly ITreatmentFoodService _treatmentFoodService;
    private readonly IInAppNotificationService _notificationService;
    private readonly IInAppNotificationRepository _notificationRepository;
    private readonly IMyFitnessPalMatchingSettingsService _settingsService;
    private readonly ILogger<MealMatchingService> _logger;

    public MealMatchingService(
        IConnectorFoodEntryRepository foodEntryRepository,
        ICarbIntakeRepository carbIntakeRepository,
        ITreatmentFoodService treatmentFoodService,
        IInAppNotificationService notificationService,
        IInAppNotificationRepository notificationRepository,
        IMyFitnessPalMatchingSettingsService settingsService,
        ILogger<MealMatchingService> logger)
    {
        _foodEntryRepository = foodEntryRepository;
        _carbIntakeRepository = carbIntakeRepository;
        _treatmentFoodService = treatmentFoodService;
        _notificationService = notificationService;
        _notificationRepository = notificationRepository;
        _settingsService = settingsService;
        _logger = logger;
    }

    public async Task ProcessNewFoodEntriesAsync(string userId, IEnumerable<Guid> foodEntryIds, CancellationToken ct = default)
    {
        var settings = await GetSettingsAsync(ct);
        if (!settings.EnableMatchNotifications)
        {
            _logger.LogDebug("Match notifications disabled, skipping processing");
            return;
        }

        var foodEntries = await _foodEntryRepository.GetByIdsAsync(foodEntryIds, ct);
        var pendingEntries = foodEntries.Where(e => e.Status == ConnectorFoodEntryStatus.Pending).ToList();

        if (pendingEntries.Count == 0)
        {
            return;
        }

        foreach (var entry in pendingEntries)
        {
            // One entry that cannot be processed — most often the notification source hitting its
            // active-notification cap — must not abandon the rest of the batch.
            try
            {
                await ProcessFoodEntryAsync(userId, entry, settings, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process food entry {FoodEntryId} for matching", entry.Id);
            }
        }
    }

    public async Task AcceptMatchAsync(Guid foodEntryId, Guid carbIntakeId, decimal carbs, int timeOffsetMinutes, CancellationToken ct = default)
    {
        var foodEntry = await _foodEntryRepository.GetByIdAsync(foodEntryId, ct);
        if (foodEntry == null)
        {
            _logger.LogWarning("Food entry {FoodEntryId} not found", foodEntryId);
            return;
        }

        // treatment_foods.carb_intake_id carries no foreign key, so an unknown id here would
        // persist a row no breakdown can ever resolve — and flip the food entry to Matched,
        // so it never resurfaces as a suggestion.
        var carbIntake = await _carbIntakeRepository.GetByIdAsync(carbIntakeId, ct);
        if (carbIntake == null)
        {
            _logger.LogWarning("Carb intake {CarbIntakeId} not found", carbIntakeId);
            return;
        }

        var treatmentFood = new TreatmentFood
        {
            Id = Guid.CreateVersion7(),
            CarbIntakeId = carbIntakeId,
            FoodId = foodEntry.FoodId,
            Portions = foodEntry.Servings,
            Carbs = carbs,
            TimeOffsetMinutes = timeOffsetMinutes,
            FoodName = foodEntry.Food?.Name ?? foodEntry.MealName,
            CarbsPerPortion = foodEntry.Servings > 0 ? carbs / foodEntry.Servings : null,
        };

        await _treatmentFoodService.AddAsync(treatmentFood, ct);

        // Update food entry status
        await _foodEntryRepository.UpdateStatusAsync(
            foodEntryId,
            ConnectorFoodEntryStatus.Matched,
            ct);

        _logger.LogInformation(
            "Accepted meal match: food entry {FoodEntryId} linked to carb intake {CarbIntakeId}",
            foodEntryId,
            carbIntakeId);
    }

    public async Task DismissMatchAsync(Guid foodEntryId, CancellationToken ct = default)
    {
        await _foodEntryRepository.UpdateStatusAsync(
            foodEntryId,
            ConnectorFoodEntryStatus.Standalone,
            ct);

        _logger.LogInformation("Dismissed meal match for food entry {FoodEntryId}", foodEntryId);
    }

    public async Task WithdrawSuggestionAsync(string userId, Guid foodEntryId, CancellationToken ct = default)
    {
        await _notificationService.ArchiveBySourceAsync(
            userId,
            SuggestedMatchNotificationType,
            foodEntryId.ToString(),
            NotificationArchiveReason.ConditionMet,
            ct);
    }

    public async Task<IReadOnlyList<SuggestedMealMatchResult>> GetSuggestionsAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken ct = default)
    {
        var settings = await GetSettingsAsync(ct);
        var timeWindow = TimeSpan.FromMinutes(settings.MatchTimeWindowMinutes);

        // Get pending food entries in the date range
        var pendingEntries = await _foodEntryRepository.GetPendingInTimeRangeAsync(from, to, ct);

        if (pendingEntries.Count == 0)
        {
            return Array.Empty<SuggestedMealMatchResult>();
        }

        // Expand the search window for carb intakes to account for matching window
        var carbIntakes = await GetCarbIntakesInWindowAsync(from - timeWindow, to + timeWindow, ct);

        var results = new List<SuggestedMealMatchResult>();

        foreach (var entry in pendingEntries)
        {
            foreach (var carbIntake in carbIntakes)
            {
                if (!IsMatch(entry, carbIntake, settings))
                {
                    continue;
                }

                results.Add(new SuggestedMealMatchResult(
                    FoodEntryId: entry.Id,
                    FoodName: entry.Food?.Name,
                    MealName: entry.MealName,
                    Carbs: entry.Carbs,
                    ConsumedAt: entry.ConsumedAt,
                    CarbIntakeId: carbIntake.Id,
                    CarbIntakeCarbs: (decimal)carbIntake.Carbs,
                    CarbIntakeMills: carbIntake.Mills,
                    MatchScore: CalculateMatchScore(entry, carbIntake, settings)
                ));
            }
        }

        // Sort by score descending, then by consumed time
        return results
            .OrderByDescending(r => r.MatchScore)
            .ThenByDescending(r => r.ConsumedAt)
            .ToList();
    }

    private async Task ProcessFoodEntryAsync(
        string userId,
        ConnectorFoodEntry entry,
        MyFitnessPalMatchingSettings settings,
        CancellationToken ct)
    {
        var timeWindow = TimeSpan.FromMinutes(settings.MatchTimeWindowMinutes);
        var carbIntakes = await GetCarbIntakesInWindowAsync(
            entry.ConsumedAt - timeWindow,
            entry.ConsumedAt + timeWindow,
            ct);

        var bestMatch = FindBestMatch(entry, carbIntakes, settings);
        if (bestMatch != null)
        {
            await CreateMatchNotificationAsync(userId, entry, bestMatch, ct);
        }
    }

    private async Task<IReadOnlyList<CarbIntake>> GetCarbIntakesInWindowAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken ct)
    {
        // Newest-first: a window wider than CandidateLimit is truncated by the database, and
        // the pending food entries being matched are the recent ones. Fetching oldest-first
        // would spend the budget on the far end of the range and find nothing.
        var carbIntakes = await _carbIntakeRepository.GetAsync(
            from: from.UtcDateTime,
            to: to.UtcDateTime,
            device: null,
            source: null,
            limit: CandidateLimit,
            offset: 0,
            descending: true,
            ct: ct);

        return carbIntakes.ToList();
    }

    private CarbIntake? FindBestMatch(
        ConnectorFoodEntry entry,
        IReadOnlyList<CarbIntake> carbIntakes,
        MyFitnessPalMatchingSettings settings)
    {
        CarbIntake? bestMatch = null;
        double bestScore = 0;

        foreach (var carbIntake in carbIntakes)
        {
            if (!IsMatch(entry, carbIntake, settings))
            {
                continue;
            }

            var score = CalculateMatchScore(entry, carbIntake, settings);
            if (score > bestScore)
            {
                bestScore = score;
                bestMatch = carbIntake;
            }
        }

        return bestMatch;
    }

    private static bool IsMatch(ConnectorFoodEntry entry, CarbIntake carbIntake, MyFitnessPalMatchingSettings settings)
    {
        var timeDiff = Math.Abs((entry.ConsumedAt - CarbIntakeTime(carbIntake)).TotalMinutes);

        if (timeDiff > settings.MatchTimeWindowMinutes)
        {
            return false;
        }

        var carbDiff = Math.Abs((double)entry.Carbs - carbIntake.Carbs);
        var carbPercent = carbIntake.Carbs > 0 ? (carbDiff / carbIntake.Carbs) * 100 : 100;

        return carbDiff <= settings.MatchCarbToleranceGrams ||
               carbPercent <= settings.MatchCarbTolerancePercent;
    }

    private static double CalculateMatchScore(
        ConnectorFoodEntry entry,
        CarbIntake carbIntake,
        MyFitnessPalMatchingSettings settings)
    {
        var timeDiff = Math.Abs((entry.ConsumedAt - CarbIntakeTime(carbIntake)).TotalMinutes);
        var timeScore = 1 - (timeDiff / settings.MatchTimeWindowMinutes);

        var carbDiff = Math.Abs((double)entry.Carbs - carbIntake.Carbs);
        var carbRatio = carbIntake.Carbs > 0 ? carbDiff / carbIntake.Carbs : 1;
        var carbScore = 1 - Math.Min(carbRatio, 1);

        return (timeScore * 0.6) + (carbScore * 0.4);
    }

    private static DateTimeOffset CarbIntakeTime(CarbIntake carbIntake) =>
        DateTimeOffset.FromUnixTimeMilliseconds(carbIntake.Mills);

    private async Task CreateMatchNotificationAsync(
        string userId,
        ConnectorFoodEntry entry,
        CarbIntake carbIntake,
        CancellationToken ct)
    {
        // The notification store does not dedupe on source, and an entry reaches this more than once
        // — re-imported with a corrected consumed time, or restored after a withdrawal — so without
        // this check the same suggestion stacks up until the source's active-notification cap trips
        // and starts throwing.
        var existing = await _notificationRepository.FindBySourceAsync(
            userId,
            SuggestedMatchNotificationType,
            entry.Id.ToString(),
            ct);

        if (existing != null)
        {
            _logger.LogDebug(
                "Match notification for food entry {FoodEntryId} is already active; not raising another",
                entry.Id);
            return;
        }

        var foodName = entry.Food?.Name ?? entry.MealName;
        var timeDisplay = FormatTimeDisplay(CarbIntakeTime(carbIntake));

        var title = $"Confirm you ate \"{foodName}\" {timeDisplay}";
        var subtitle = $"{entry.MealName} · {entry.Carbs:0}g carbs · via MyFitnessPal";

        var actions = new List<NotificationActionDto>
        {
            new() { ActionId = "accept", Label = "Accept", Variant = "default" },
            new() { ActionId = "review", Label = "Review", Variant = "outline" },
            new() { ActionId = "dismiss", Label = "Dismiss", Variant = "outline" },
        };

        var metadata = new Dictionary<string, object>
        {
            ["carbIntakeId"] = carbIntake.Id,
            ["carbIntakeCarbs"] = carbIntake.Carbs,
            ["carbIntakeMills"] = carbIntake.Mills,
            ["foodEntryCarbs"] = entry.Carbs,
            ["consumedAtMills"] = entry.ConsumedAt.ToUnixTimeMilliseconds(),
        };

        await _notificationService.CreateNotificationAsync(
            userId,
            SuggestedMatchNotificationType,
            title,
            subtitle: subtitle,
            sourceId: entry.Id.ToString(),
            actions: actions,
            metadata: metadata,
            cancellationToken: ct);

        _logger.LogInformation(
            "Created meal match notification for food entry {FoodEntryId} and carb intake {CarbIntakeId}",
            entry.Id,
            carbIntake.Id);
    }

    private static string FormatTimeDisplay(DateTimeOffset time)
    {
        var now = DateTimeOffset.UtcNow;
        var localTime = time.ToLocalTime();

        if (localTime.Date == now.Date)
        {
            return $"at {localTime:h:mmtt}".ToLowerInvariant();
        }
        else if (localTime.Date == now.Date.AddDays(-1))
        {
            return $"yesterday at {localTime:h:mmtt}".ToLowerInvariant();
        }
        else
        {
            return $"on {localTime:MMM d} at {localTime:h:mmtt}".ToLowerInvariant();
        }
    }

    private async Task<MyFitnessPalMatchingSettings> GetSettingsAsync(CancellationToken ct)
    {
        return await _settingsService.GetSettingsAsync(ct);
    }
}
