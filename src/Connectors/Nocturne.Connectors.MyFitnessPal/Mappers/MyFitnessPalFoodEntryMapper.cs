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
    /// <param name="mealNames">
    ///     Entry id to meal name, from <see cref="MyFitnessPalMealAttributor"/>. Entries missing
    ///     from it are imported without a meal name.
    /// </param>
    public List<ConnectorFoodEntryImport> Map(
        IEnumerable<MfpFoodDiaryEntryNode> entries,
        MyFitnessPalConnectorConfiguration config,
        DateTimeOffset from,
        DateTimeOffset to,
        IReadOnlyDictionary<string, string>? mealNames = null)
    {
        var imports = new List<ConnectorFoodEntryImport>();

        foreach (var entry in entries)
        {
            if (!DateOnly.TryParse(entry.Date, CultureInfo.InvariantCulture, out var date))
            {
                _logger.LogWarning("Could not parse date {Date} from MFP diary", entry.Date);
                continue;
            }

            var mealName = mealNames?.GetValueOrDefault(entry.Id);
            var (consumedAt, isTimeInferred) = ResolveConsumedAt(entry, date, mealName, config);
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
                MealName = mealName ?? string.Empty,
                Carbs = nutrition?.Carbs ?? 0,
                Protein = nutrition?.Protein ?? 0,
                Fat = nutrition?.Fat ?? 0,
                Energy = nutrition?.Calories ?? 0,
                Servings = entry.Quantity,
                ServingDescription = FormatServingDescription(entry.ServingSize, entry.Quantity),
                IsTimeInferred = isTimeInferred,
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
    ///     Resolves the consumed-at time for an entry, preferring the time MyFitnessPal reports over
    ///     one derived from the meal name. Users can rename meals, so an unrecognised name falls
    ///     back to midday.
    /// </summary>
    /// <remarks>
    ///     Production sends <c>consumedAt</c> as a bare local time of day — <c>"09:13:00"</c>, with
    ///     no date — for most entries, and null for the rest. It has to be combined with the entry's
    ///     own <paramref name="date"/>: handing a time-only string to
    ///     <see cref="DateTimeOffset.TryParse(string, IFormatProvider, out DateTimeOffset)"/> yields
    ///     that time on the *current* date, which silently redates the entire history to the day of
    ///     the sync. Confirmed against the live graph: an entry dated 2025-05-23 with
    ///     <c>consumedAt</c> 13:12:00 has <c>loggedAt</c> 2025-05-23T03:12:10Z, exactly 13:12 at the
    ///     account's +10 offset, so the pair is a local date and a local wall-clock time.
    /// </remarks>
    /// <returns>
    ///     The resolved time, and whether it was inferred rather than reported. A reported time is
    ///     never inferred; a meal-name derivation always is, and must not overwrite a stored value —
    ///     see <see cref="ConnectorFoodEntryImport.IsTimeInferred"/>.
    /// </returns>
    public static (DateTimeOffset ConsumedAt, bool IsTimeInferred) ResolveConsumedAt(
        MfpFoodDiaryEntryNode entry,
        DateOnly date,
        string? mealName,
        MyFitnessPalConnectorConfiguration config)
    {
        // DateTimeOffset rejects an offset that is not a whole number of minutes, and
        // TimezoneOffset is a double validated only against a range, so round before using it.
        var offset = TimeSpan.FromMinutes(Math.Round(config.TimezoneOffset * 60));

        if (TryParseLocalTimeOfDay(entry.ConsumedAt, out var timeOfDay))
            return (new DateTimeOffset(date.ToDateTime(timeOfDay), offset).ToUniversalTime(), false);

        // Not the shape production sends, but honour a full instant if it ever does.
        if (entry.ConsumedAt != null
            && DateTimeOffset.TryParse(entry.ConsumedAt, CultureInfo.InvariantCulture, out var parsed))
            return (parsed.ToUniversalTime(), false);

        var mealHour = mealName?.ToLowerInvariant() switch
        {
            "breakfast" => 8,
            "lunch" => 12,
            "dinner" => 18,
            "snack" or "snacks" => 15,
            _ => 12,
        };

        var dateTime = date.ToDateTime(new TimeOnly(mealHour, 0));
        return (new DateTimeOffset(dateTime, offset).ToUniversalTime(), true);
    }

    /// <summary>
    ///     Matches only a bare time of day, so a full timestamp falls through to instant parsing.
    /// </summary>
    private static bool TryParseLocalTimeOfDay(string? value, out TimeOnly timeOfDay)
    {
        timeOfDay = default;
        return !string.IsNullOrEmpty(value)
               && TimeOnly.TryParseExact(
                   value,
                   ["HH:mm:ss", "HH:mm:ss.FFF", "HH:mm"],
                   CultureInfo.InvariantCulture,
                   DateTimeStyles.None,
                   out timeOfDay);
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
