using Microsoft.Extensions.Logging;
using Nocturne.Connectors.Core.Constants;
using Nocturne.Connectors.Tidepool.Models;
using Nocturne.Core.Models;

namespace Nocturne.Connectors.Tidepool.Mappers;

public class TidepoolTreatmentMapper(ILogger logger, string connectorSource)
{
    private const string EnteredBySource = "Tidepool";

    private readonly string _connectorSource =
        connectorSource ?? throw new ArgumentNullException(nameof(connectorSource));

    private readonly ILogger _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>
    ///     Merges boluses, food entries, and physical activities into Nightscout treatments.
    ///     Boluses and food at the same timestamp are merged into a single treatment.
    /// </summary>
    public IEnumerable<Treatment> MapTreatments(
        TidepoolBolus[]? boluses,
        TidepoolFood[]? foods,
        TidepoolPhysicalActivity[]? activities)
    {
        var treatments = new Dictionary<DateTime, Treatment>();

        // Process boluses first
        if (boluses != null)
        {
            // Deduplicate by timestamp, take first
            var uniqueBoluses = boluses
                .Where(b => b.Time.HasValue)
                .GroupBy(b => b.Time!.Value)
                .Select(g => g.First());

            foreach (var bolus in uniqueBoluses)
            {
                try
                {
                    var treatment = MapBolus(bolus);
                    if (treatment != null)
                        treatments[bolus.Time!.Value] = treatment;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error mapping Tidepool bolus: {Id}", bolus.Id);
                }
            }
        }

        // Process food entries - merge with existing bolus treatments at same timestamp
        if (foods != null)
        {
            var uniqueFoods = foods
                .Where(f => f.Time.HasValue)
                .GroupBy(f => f.Time!.Value)
                .Select(g => g.First());

            foreach (var food in uniqueFoods)
            {
                try
                {
                    var carbs = food.Nutrition?.Carbohydrate?.Net;
                    if (!carbs.HasValue || carbs.Value <= 0) continue;

                    if (treatments.TryGetValue(food.Time!.Value, out var existing))
                    {
                        // Merge carbs into existing bolus treatment
                        existing.Carbs = carbs.Value;
                        existing.EventType = TreatmentTypes.MealBolus;
                    }
                    else
                    {
                        // Standalone carb entry
                        treatments[food.Time!.Value] = new Treatment
                        {
                            Id = $"tidepool_{food.Id}",
                            EventType = TreatmentTypes.CarbCorrection,
                            Carbs = carbs.Value,
                            EnteredBy = EnteredBySource,
                            DataSource = _connectorSource,
                            Created_at = food.Time.Value.ToString("o")
                        };
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error mapping Tidepool food: {Id}", food.Id);
                }
            }
        }

        // Process physical activities as separate treatments
        if (activities != null)
        {
            foreach (var activity in activities.Where(a => a.Time.HasValue))
            {
                try
                {
                    var treatment = MapActivity(activity);
                    if (treatment != null)
                    {
                        // Use a key that won't conflict with bolus/food timestamps
                        // by checking for collision and adding to list directly
                        if (!treatments.TryAdd(activity.Time!.Value, treatment))
                        {
                            // Timestamp collision with bolus/food - use a slightly offset key
                            treatments[activity.Time!.Value.AddMilliseconds(1)] = treatment;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error mapping Tidepool activity: {Id}", activity.Id);
                }
            }
        }

        return treatments.Values.OrderBy(t => t.Created_at).ToList();
    }

    private Treatment? MapBolus(TidepoolBolus bolus)
    {
        if (!bolus.Time.HasValue) return null;

        var totalInsulin = (bolus.Normal ?? 0) + (bolus.Extended ?? 0);
        if (totalInsulin <= 0) return null;

        var eventType = bolus.Extended.HasValue && bolus.Extended > 0
            ? TreatmentTypes.ComboBolus
            : TreatmentTypes.CorrectionBolus;

        var treatment = new Treatment
        {
            Id = $"tidepool_{bolus.Id}",
            EventType = eventType,
            Insulin = totalInsulin,
            EnteredBy = EnteredBySource,
            DataSource = _connectorSource,
            Created_at = bolus.Time.Value.ToString("o")
        };

        // Set duration for extended/combo boluses
        if (bolus.Duration.HasValue)
            treatment.Duration = bolus.Duration.Value.TotalMinutes;

        return treatment;
    }

    private Treatment? MapActivity(TidepoolPhysicalActivity activity)
    {
        if (!activity.Time.HasValue) return null;

        var treatment = new Treatment
        {
            Id = $"tidepool_{activity.Id}",
            EventType = "Exercise",
            EnteredBy = EnteredBySource,
            DataSource = _connectorSource,
            Notes = activity.Name,
            Created_at = activity.Time.Value.ToString("o")
        };

        if (activity.Duration != null)
        {
            // Convert to minutes regardless of source unit
            treatment.Duration = activity.Duration.Units.ToLowerInvariant() switch
            {
                "seconds" => activity.Duration.Value / 60.0,
                "minutes" => activity.Duration.Value,
                "hours" => activity.Duration.Value * 60.0,
                _ => activity.Duration.Value
            };
        }

        return treatment;
    }
}
