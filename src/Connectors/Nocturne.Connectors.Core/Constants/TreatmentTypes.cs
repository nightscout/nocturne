namespace Nocturne.Connectors.Core.Constants;

/// <summary>
///     Standard treatment type strings for consistency across connectors.
///     These match Nightscout's expected event types.
/// </summary>
public static class TreatmentTypes
{
    /// <summary>
    ///     Treatment with both carbs and insulin (meal with bolus).
    /// </summary>
    public const string MealBolus = "Meal Bolus";

    /// <summary>
    ///     Treatment with insulin only (correction for high blood glucose).
    /// </summary>
    public const string CorrectionBolus = "Correction Bolus";

    /// <summary>
    ///     Treatment with carbs only (eating without bolusing).
    /// </summary>
    public const string CarbCorrection = "Carb Correction";
}