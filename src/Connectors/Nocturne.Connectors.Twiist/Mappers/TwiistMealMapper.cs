using Microsoft.Extensions.Logging;
using Nocturne.Connectors.Twiist.Models;
using Nocturne.Core.Constants;
using Nocturne.Core.Models.V4;

namespace Nocturne.Connectors.Twiist.Mappers;

/// <summary>
/// Maps Twiist meal history to V4 CarbIntake models.
/// </summary>
public class TwiistMealMapper(ILogger logger)
{
    private readonly ILogger _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public IEnumerable<CarbIntake> MapMeals(List<TwiistMeal>? meals)
    {
        if (meals == null || meals.Count == 0) return [];

        var now = DateTime.UtcNow;

        return meals
            .Where(m => m.Grams is > 0)
            .Select(m => ConvertMeal(m, now))
            .Where(c => c != null)
            .Cast<CarbIntake>()
            .OrderBy(c => c.Mills)
            .ToList();
    }

    private CarbIntake? ConvertMeal(TwiistMeal meal, DateTime now)
    {
        try
        {
            var timestamp = (meal.StartDate ?? meal.AddedDate)?.ToUniversalTime();
            if (timestamp == null || meal.Grams == null)
                return null;

            var absorptionMinutes = meal.AbsorptionTimeSeconds.HasValue
                ? (int)(meal.AbsorptionTimeSeconds.Value / 60m)
                : (int?)null;

            return new CarbIntake
            {
                Id = Guid.CreateVersion7(),
                Timestamp = timestamp.Value,
                LegacyId = $"twiist_meal_{new DateTimeOffset(timestamp.Value, TimeSpan.Zero).ToUnixTimeMilliseconds()}",
                Device = DataSources.TwiistConnector,
                DataSource = DataSources.TwiistConnector,
                Carbs = (double)meal.Grams.Value,
                AbsorptionTime = absorptionMinutes,
                CreatedAt = now,
                ModifiedAt = now
            };
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or OverflowException)
        {
            _logger.LogWarning(ex, "Error converting Twiist meal at {Date}", meal.StartDate);
            return null;
        }
    }
}
