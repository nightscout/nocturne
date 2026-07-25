using System.Globalization;
using Microsoft.Extensions.Logging;
using Nocturne.Connectors.MyFitnessPal.Configurations;
using Nocturne.Connectors.MyFitnessPal.Models;
using Nocturne.Core.Constants;
using Nocturne.Core.Models;

namespace Nocturne.Connectors.MyFitnessPal.Mappers;

/// <summary>
///     Maps MyFitnessPal diary entries to connector food entry imports for the meal matching pipeline.
/// </summary>
public class MyFitnessPalFoodEntryMapper(ILogger logger)
{
    private readonly ILogger _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>
    ///     Maps the entries consumed within the requested window; entries outside it are dropped.
    /// </summary>
    public List<ConnectorFoodEntryImport> Map(
        IEnumerable<MfpFoodDiaryEntryNode> entries,
        MyFitnessPalConnectorConfiguration config,
        DateTimeOffset from,
        DateTimeOffset to)
    {
        var imports = new List<ConnectorFoodEntryImport>();

        foreach (var entry in entries)
        {
            if (!DateOnly.TryParse(entry.Date, CultureInfo.InvariantCulture, out var date))
            {
                _logger.LogWarning("Could not parse date {Date} from MFP diary", entry.Date);
                continue;
            }

            var consumedAt = ResolveConsumedAt(entry, date, config);
            if (consumedAt < from || consumedAt > to)
                continue;

            // consumedNutrientSet is already scaled to the logged quantity; the food's own
            // nutrientSet is per serving.
            var nutrition = entry.ConsumedNutrientSet;
            var food = entry.Food;

            imports.Add(new ConnectorFoodEntryImport
            {
                ConnectorSource = DataSources.MyFitnessPalConnector,
                ExternalEntryId = entry.Id,
                ExternalFoodId = food?.Id ?? string.Empty,
                ConsumedAt = consumedAt,
                LoggedAt = TryParseTimestamp(entry.LoggedAt),
                MealName = entry.EatingOccasion ?? string.Empty,
                Carbs = nutrition?.Carbs ?? 0,
                Protein = nutrition?.Protein ?? 0,
                Fat = nutrition?.Fat ?? 0,
                Energy = nutrition?.Calories ?? 0,
                Servings = entry.Quantity,
                ServingDescription = FormatServingDescription(entry.ServingSize, entry.Quantity),
                Food = food != null
                    ? new ConnectorFoodImport
                    {
                        ExternalId = food.Id,
                        Name = food.Description ?? string.Empty,
                        BrandName = food.Brand,
                        Carbs = food.NutrientSet?.Carbs ?? 0,
                        Protein = food.NutrientSet?.Protein ?? 0,
                        Fat = food.NutrientSet?.Fat ?? 0,
                        Energy = food.NutrientSet?.Calories ?? 0,
                        Portion = entry.ServingSize?.Amount ?? 1,
                        Unit = entry.ServingSize?.Unit,
                    }
                    : null,
            });
        }

        return imports;
    }

    /// <summary>
    ///     Resolves the consumed-at time for an entry. Entries always carry a date but only
    ///     sometimes an exact time, so the eating occasion supplies an approximate hour. Occasions
    ///     can be renamed or added by the user, so an unrecognised name falls back to its diary slot.
    /// </summary>
    public static DateTimeOffset ResolveConsumedAt(
        MfpFoodDiaryEntryNode entry,
        DateOnly date,
        MyFitnessPalConnectorConfiguration config)
    {
        if (entry.ConsumedAt != null
            && DateTimeOffset.TryParse(entry.ConsumedAt, CultureInfo.InvariantCulture, out var parsed))
            return parsed.ToUniversalTime();

        var mealHour = entry.EatingOccasion?.ToLowerInvariant() switch
        {
            "breakfast" => 8,
            "lunch" => 12,
            "dinner" => 18,
            "snack" or "snacks" => 15,
            _ => entry.EatingOccasionSlot switch
            {
                0 => 8,
                1 => 12,
                2 => 18,
                3 => 15,
                _ => 12,
            },
        };

        var dateTime = date.ToDateTime(new TimeOnly(mealHour, 0));
        return new DateTimeOffset(dateTime, TimeSpan.FromHours(config.TimezoneOffset)).ToUniversalTime();
    }

    private static DateTimeOffset? TryParseTimestamp(string? timestamp)
    {
        if (string.IsNullOrEmpty(timestamp))
            return null;

        return DateTimeOffset.TryParse(timestamp, CultureInfo.InvariantCulture, out var result)
            ? result.ToUniversalTime()
            : null;
    }

    public static string? FormatServingDescription(MfpServingSize? servingSize, decimal quantity)
    {
        if (servingSize == null)
            return null;

        return quantity == 1
            ? $"{servingSize.Amount} {servingSize.Unit}"
            : $"{quantity} x {servingSize.Amount} {servingSize.Unit}";
    }
}
