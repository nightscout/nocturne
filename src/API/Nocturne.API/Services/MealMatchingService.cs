using Nocturne.Core.Contracts;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Configuration;
using Nocturne.Infrastructure.Data.Repositories;

namespace Nocturne.API.Services;

/// <summary>
/// Service for matching MFP food entries to treatments
/// </summary>
public class MealMatchingService : IMealMatchingService
{
    private readonly IConnectorFoodEntryRepository _foodEntryRepository;
    private readonly TreatmentRepository _treatmentRepository;
    private readonly ITreatmentFoodService _treatmentFoodService;
    private readonly IInAppNotificationService _notificationService;
    private readonly IMyFitnessPalMatchingSettingsService _settingsService;
    private readonly ILogger<MealMatchingService> _logger;

    public MealMatchingService(
        IConnectorFoodEntryRepository foodEntryRepository,
        TreatmentRepository treatmentRepository,
        ITreatmentFoodService treatmentFoodService,
        IInAppNotificationService notificationService,
        IMyFitnessPalMatchingSettingsService settingsService,
        ILogger<MealMatchingService> logger)
    {
        _foodEntryRepository = foodEntryRepository;
        _treatmentRepository = treatmentRepository;
        _treatmentFoodService = treatmentFoodService;
        _notificationService = notificationService;
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
            await ProcessFoodEntryAsync(userId, entry, settings, ct);
        }
    }

    public async Task ProcessNewTreatmentAsync(string userId, Guid treatmentId, CancellationToken ct = default)
    {
        var settings = await GetSettingsAsync(ct);
        if (!settings.EnableMatchNotifications)
        {
            _logger.LogDebug("Match notifications disabled, skipping processing");
            return;
        }

        var treatment = await _treatmentRepository.GetTreatmentByIdAsync(treatmentId.ToString(), ct);
        if (treatment == null)
        {
            _logger.LogWarning("Treatment {TreatmentId} not found", treatmentId);
            return;
        }

        // Only process meal treatments
        if (treatment.Carbs is null or <= 0 &&
            (treatment.EventType == null || !treatment.EventType.Contains("Meal", StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        var treatmentTime = DateTimeOffset.FromUnixTimeMilliseconds(treatment.Mills);
        var timeWindow = TimeSpan.FromMinutes(settings.MatchTimeWindowMinutes);

        var pendingEntries = await _foodEntryRepository.GetPendingInTimeRangeAsync(
            treatmentTime - timeWindow,
            treatmentTime + timeWindow,
            ct);

        foreach (var entry in pendingEntries)
        {
            if (IsMatch(entry, treatment, settings))
            {
                await CreateMatchNotificationAsync(userId, entry, treatment, ct);
            }
        }
    }

    public async Task AcceptMatchAsync(Guid foodEntryId, Guid treatmentId, decimal carbs, int timeOffsetMinutes, CancellationToken ct = default)
    {
        var foodEntry = await _foodEntryRepository.GetByIdAsync(foodEntryId, ct);
        if (foodEntry == null)
        {
            _logger.LogWarning("Food entry {FoodEntryId} not found", foodEntryId);
            return;
        }

        // Create TreatmentFood entry
        var treatmentFood = new TreatmentFood
        {
            Id = Guid.CreateVersion7(),
            TreatmentId = treatmentId,
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
            treatmentId,
            ct);

        _logger.LogInformation(
            "Accepted meal match: food entry {FoodEntryId} linked to treatment {TreatmentId}",
            foodEntryId,
            treatmentId);
    }

    public async Task DismissMatchAsync(Guid foodEntryId, CancellationToken ct = default)
    {
        await _foodEntryRepository.UpdateStatusAsync(
            foodEntryId,
            ConnectorFoodEntryStatus.Standalone,
            null,
            ct);

        _logger.LogInformation("Dismissed meal match for food entry {FoodEntryId}", foodEntryId);
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

        // Expand the search window for treatments to account for matching window
        var treatmentsFrom = from - timeWindow;
        var treatmentsTo = to + timeWindow;
        var treatments = await _treatmentRepository.GetMealTreatmentsInTimeRangeAsync(
            treatmentsFrom,
            treatmentsTo,
            ct);

        var results = new List<SuggestedMealMatchResult>();

        foreach (var entry in pendingEntries)
        {
            foreach (var treatment in treatments)
            {
                if (!IsMatch(entry, treatment, settings))
                {
                    continue;
                }

                var score = CalculateMatchScore(entry, treatment, settings);
                if (treatment.DbId == null)
                {
                    _logger.LogWarning("Treatment {TreatmentId} has no DbId, skipping", treatment.Id);
                    continue;
                }
                results.Add(new SuggestedMealMatchResult(
                    FoodEntryId: entry.Id,
                    FoodName: entry.Food?.Name,
                    MealName: entry.MealName,
                    Carbs: entry.Carbs,
                    ConsumedAt: entry.ConsumedAt,
                    TreatmentId: treatment.DbId.Value,
                    TreatmentCarbs: (decimal)(treatment.Carbs ?? 0),
                    TreatmentMills: treatment.Mills,
                    MatchScore: score
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
        var treatments = await _treatmentRepository.GetMealTreatmentsInTimeRangeAsync(
            entry.ConsumedAt - timeWindow,
            entry.ConsumedAt + timeWindow,
            ct);

        var bestMatch = FindBestMatch(entry, treatments, settings);
        if (bestMatch != null)
        {
            await CreateMatchNotificationAsync(userId, entry, bestMatch, ct);
        }
    }

    private Treatment? FindBestMatch(
        ConnectorFoodEntry entry,
        IReadOnlyList<Treatment> treatments,
        MyFitnessPalMatchingSettings settings)
    {
        Treatment? bestMatch = null;
        double bestScore = 0;

        foreach (var treatment in treatments)
        {
            if (!IsMatch(entry, treatment, settings))
            {
                continue;
            }

            var score = CalculateMatchScore(entry, treatment, settings);
            if (score > bestScore)
            {
                bestScore = score;
                bestMatch = treatment;
            }
        }

        return bestMatch;
    }

    private bool IsMatch(ConnectorFoodEntry entry, Treatment treatment, MyFitnessPalMatchingSettings settings)
    {
        var treatmentTime = DateTimeOffset.FromUnixTimeMilliseconds(treatment.Mills);
        var timeDiff = Math.Abs((entry.ConsumedAt - treatmentTime).TotalMinutes);

        if (timeDiff > settings.MatchTimeWindowMinutes)
        {
            return false;
        }

        var treatmentCarbs = treatment.Carbs ?? 0;
        var carbDiff = Math.Abs((double)(entry.Carbs - (decimal)treatmentCarbs));
        var carbPercent = treatmentCarbs > 0 ? (carbDiff / treatmentCarbs) * 100 : 100;

        return carbDiff <= settings.MatchCarbToleranceGrams ||
               carbPercent <= settings.MatchCarbTolerancePercent;
    }

    private double CalculateMatchScore(
        ConnectorFoodEntry entry,
        Treatment treatment,
        MyFitnessPalMatchingSettings settings)
    {
        var treatmentTime = DateTimeOffset.FromUnixTimeMilliseconds(treatment.Mills);
        var timeDiff = Math.Abs((entry.ConsumedAt - treatmentTime).TotalMinutes);
        var timeScore = 1 - (timeDiff / settings.MatchTimeWindowMinutes);

        var treatmentCarbs = treatment.Carbs ?? 0;
        var carbDiff = Math.Abs((double)(entry.Carbs - (decimal)treatmentCarbs));
        var carbRatio = treatmentCarbs > 0 ? carbDiff / treatmentCarbs : 1;
        var carbScore = 1 - Math.Min(carbRatio, 1);

        return (timeScore * 0.6) + (carbScore * 0.4);
    }

    private async Task CreateMatchNotificationAsync(
        string userId,
        ConnectorFoodEntry entry,
        Treatment treatment,
        CancellationToken ct)
    {
        var foodName = entry.Food?.Name ?? entry.MealName;
        var treatmentTime = DateTimeOffset.FromUnixTimeMilliseconds(treatment.Mills);
        var timeDisplay = FormatTimeDisplay(treatmentTime);

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
            ["treatmentId"] = treatment.Id!,
            ["treatmentCarbs"] = treatment.Carbs ?? 0,
            ["treatmentMills"] = treatment.Mills,
            ["foodEntryCarbs"] = entry.Carbs,
            ["consumedAtMills"] = entry.ConsumedAt.ToUnixTimeMilliseconds(),
        };

        await _notificationService.CreateNotificationAsync(
            userId,
            InAppNotificationType.SuggestedMealMatch,
            NotificationUrgency.Info,
            title,
            subtitle,
            sourceId: entry.Id.ToString(),
            actions: actions,
            metadata: metadata,
            cancellationToken: ct);

        _logger.LogInformation(
            "Created meal match notification for food entry {FoodEntryId} and treatment {TreatmentId}",
            entry.Id,
            treatment.Id);
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
