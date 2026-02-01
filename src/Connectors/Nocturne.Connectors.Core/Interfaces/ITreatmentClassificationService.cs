using Nocturne.Connectors.Core.Models;

namespace Nocturne.Connectors.Core.Interfaces;

/// <summary>
/// Service for consistent treatment classification across connectors.
/// Classifies bolus and carb events into Meal Bolus, Correction Bolus, or Carb Correction.
/// </summary>
public interface ITreatmentClassificationService
{
    /// <summary>
    /// Classifies a single treatment event based on carbs and insulin values.
    /// </summary>
    /// <param name="carbs">Carbohydrate amount in grams (null or 0 means no carbs)</param>
    /// <param name="insulin">Insulin amount in units (null or 0 means no insulin)</param>
    /// <returns>The classified event type string (Meal Bolus, Correction Bolus, or Carb Correction)</returns>
    /// <remarks>
    /// Classification rules:
    /// - Carbs > 0 AND Insulin > 0 → Meal Bolus
    /// - Carbs > 0 AND Insulin ≤ 0 → Carb Correction
    /// - Carbs ≤ 0 AND Insulin > 0 → Correction Bolus
    /// </remarks>
    string ClassifyTreatment(double? carbs, double? insulin);

    /// <summary>
    /// Pre-processes a batch of carb and insulin events to match them within a time window.
    /// Returns a context that can be used to look up matched carbs for a given insulin event.
    /// </summary>
    /// <param name="carbEvents">Collection of carb events with timestamps</param>
    /// <param name="insulinEvents">Collection of insulin events with timestamps</param>
    /// <param name="options">Configuration options for matching behavior</param>
    /// <returns>A classification context with matched carb-insulin pairs</returns>
    /// <remarks>
    /// The matching algorithm:
    /// 1. For each insulin event with insulin > 0:
    ///    a. If it has embedded carbs and EmbeddedCarbsTakePrecedence is true, use those
    ///    b. Otherwise, find the closest unmatched carb event within the time window
    /// 2. Matched carb events are tracked for suppression
    /// </remarks>
    TreatmentClassificationContext CreateMatchingContext(
        IEnumerable<TreatmentEvent> carbEvents,
        IEnumerable<TreatmentEvent> insulinEvents,
        TreatmentClassificationOptions options);

    /// <summary>
    /// Classifies an insulin event using a pre-computed matching context.
    /// </summary>
    /// <param name="insulinEvent">The insulin event to classify</param>
    /// <param name="context">The pre-computed matching context</param>
    /// <returns>The classified event type string</returns>
    string ClassifyWithContext(TreatmentEvent insulinEvent, TreatmentClassificationContext context);

    /// <summary>
    /// Gets the matched carbs for an insulin event from a pre-computed context.
    /// </summary>
    /// <param name="insulinEventKey">The event key of the insulin event</param>
    /// <param name="context">The pre-computed matching context</param>
    /// <returns>The matched carb amount, or null if no match</returns>
    double? GetMatchedCarbs(string insulinEventKey, TreatmentClassificationContext context);
}
