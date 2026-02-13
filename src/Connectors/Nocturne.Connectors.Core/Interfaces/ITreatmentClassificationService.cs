namespace Nocturne.Connectors.Core.Interfaces;

/// <summary>
///     Service for consistent treatment classification across connectors.
///     Classifies bolus and carb events into Meal Bolus, Correction Bolus, or Carb Correction.
/// </summary>
public interface ITreatmentClassificationService
{
    /// <summary>
    ///     Classifies a single treatment event based on carbs and insulin values.
    /// </summary>
    /// <param name="carbs">Carbohydrate amount in grams (null or 0 means no carbs)</param>
    /// <param name="insulin">Insulin amount in units (null or 0 means no insulin)</param>
    /// <returns>The classified event type string (Meal Bolus, Correction Bolus, or Carb Correction)</returns>
    /// <remarks>
    ///     Classification rules:
    ///     - Carbs > 0 AND Insulin > 0 → Meal Bolus
    ///     - Carbs > 0 AND Insulin ≤ 0 → Carb Correction
    ///     - Carbs ≤ 0 AND Insulin > 0 → Correction Bolus
    /// </remarks>
    string ClassifyTreatment(double? carbs, double? insulin);
}