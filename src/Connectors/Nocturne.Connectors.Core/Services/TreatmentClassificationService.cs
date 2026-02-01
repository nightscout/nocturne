using Microsoft.Extensions.Logging;
using Nocturne.Connectors.Core.Constants;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;

namespace Nocturne.Connectors.Core.Services;

/// <summary>
/// Provides consistent treatment classification logic across all connectors.
/// </summary>
public class TreatmentClassificationService : ITreatmentClassificationService
{
    private readonly ILogger<TreatmentClassificationService> _logger;

    public TreatmentClassificationService(ILogger<TreatmentClassificationService> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc/>
    public string ClassifyTreatment(double? carbs, double? insulin)
    {
        var hasCarbs = carbs is > 0;
        var hasInsulin = insulin is > 0;

        return (hasCarbs, hasInsulin) switch
        {
            (true, true) => TreatmentTypes.MealBolus,
            (true, false) => TreatmentTypes.CarbCorrection,
            (false, true) => TreatmentTypes.CorrectionBolus,
            // Edge case: no carbs and no insulin - default to Correction Bolus
            // This shouldn't happen in normal data but provides a fallback
            _ => TreatmentTypes.CorrectionBolus
        };
    }

    /// <inheritdoc/>
    public TreatmentClassificationContext CreateMatchingContext(
        IEnumerable<TreatmentEvent> carbEvents,
        IEnumerable<TreatmentEvent> insulinEvents,
        TreatmentClassificationOptions options)
    {
        var context = new TreatmentClassificationContext { Options = options };
        var carbList = carbEvents.ToList();

        foreach (var insulinEvent in insulinEvents)
        {
            // Skip insulin events with no insulin (0u or null)
            if (insulinEvent.Insulin is not > 0)
            {
                continue;
            }

            // If insulin event has embedded carbs and that takes precedence, use those
            if (options.EmbeddedCarbsTakePrecedence && insulinEvent.EmbeddedCarbs is > 0)
            {
                context.InsulinToCarbMatches[insulinEvent.EventKey] = insulinEvent.EmbeddedCarbs.Value;

                // Suppress any nearby carb events within the window
                if (options.SuppressMatchedCarbEvents)
                {
                    foreach (var carbEvent in carbList.Where(c =>
                        Math.Abs(c.TimestampMs - insulinEvent.TimestampMs) <= options.MatchingWindowMs))
                    {
                        context.SuppressedCarbTimestamps.Add(carbEvent.TimestampMs);
                        context.MatchedCarbEventKeys.Add(carbEvent.EventKey);
                    }
                }

                _logger.LogDebug(
                    "Insulin event {EventKey} using embedded carbs {Carbs}g",
                    insulinEvent.EventKey, insulinEvent.EmbeddedCarbs.Value);
                continue;
            }

            // Find the closest unmatched carb event within the time window
            var closestCarb = carbList
                .Where(c => !context.MatchedCarbEventKeys.Contains(c.EventKey))
                .Where(c => c.Carbs is > 0)
                .Where(c => Math.Abs(c.TimestampMs - insulinEvent.TimestampMs) <= options.MatchingWindowMs)
                .OrderBy(c => Math.Abs(c.TimestampMs - insulinEvent.TimestampMs))
                .FirstOrDefault();

            if (closestCarb != null)
            {
                context.InsulinToCarbMatches[insulinEvent.EventKey] = closestCarb.Carbs!.Value;
                context.MatchedCarbEventKeys.Add(closestCarb.EventKey);

                if (options.SuppressMatchedCarbEvents)
                {
                    context.SuppressedCarbTimestamps.Add(closestCarb.TimestampMs);
                }

                _logger.LogDebug(
                    "Matched insulin event {InsulinKey} with carb event {CarbKey} ({Carbs}g, delta={DeltaMs}ms)",
                    insulinEvent.EventKey,
                    closestCarb.EventKey,
                    closestCarb.Carbs,
                    Math.Abs(closestCarb.TimestampMs - insulinEvent.TimestampMs));
            }
        }

        _logger.LogDebug(
            "Created classification context: {MatchCount} insulin-carb matches, {SuppressedCount} suppressed carb events",
            context.InsulinToCarbMatches.Count,
            context.SuppressedCarbTimestamps.Count);

        return context;
    }

    /// <inheritdoc/>
    public string ClassifyWithContext(TreatmentEvent insulinEvent, TreatmentClassificationContext context)
    {
        // Check if this insulin event has matched carbs from the context
        if (context.InsulinToCarbMatches.TryGetValue(insulinEvent.EventKey, out var matchedCarbs))
        {
            return ClassifyTreatment(matchedCarbs, insulinEvent.Insulin);
        }

        // Use embedded carbs if available (fallback if context didn't capture them)
        if (insulinEvent.EmbeddedCarbs is > 0)
        {
            return ClassifyTreatment(insulinEvent.EmbeddedCarbs, insulinEvent.Insulin);
        }

        // No carbs - classify based on insulin only
        return ClassifyTreatment(null, insulinEvent.Insulin);
    }

    /// <inheritdoc/>
    public double? GetMatchedCarbs(string insulinEventKey, TreatmentClassificationContext context)
    {
        return context.GetMatchedCarbs(insulinEventKey);
    }
}
