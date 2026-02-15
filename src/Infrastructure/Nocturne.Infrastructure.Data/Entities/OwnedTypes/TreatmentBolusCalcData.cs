namespace Nocturne.Infrastructure.Data.Entities.OwnedTypes;

/// <summary>
/// Bolus calculator data associated with a treatment.
/// EF Core owned type -- stored as columns on the treatments table.
/// </summary>
public class TreatmentBolusCalcData
{
    /// <summary>
    /// Insulin recommended by bolus calculator specifically for carbohydrate coverage
    /// </summary>
    public double? InsulinRecommendationForCarbs { get; set; }

    /// <summary>
    /// Insulin recommended by bolus calculator for glucose correction
    /// </summary>
    public double? InsulinRecommendationForCorrection { get; set; }

    /// <summary>
    /// Total insulin amount programmed for delivery
    /// </summary>
    public double? InsulinProgrammed { get; set; }

    /// <summary>
    /// Actual insulin amount delivered
    /// </summary>
    public double? InsulinDelivered { get; set; }

    /// <summary>
    /// Insulin on board at the time of this treatment
    /// </summary>
    public double? InsulinOnBoard { get; set; }

    /// <summary>
    /// Blood glucose input value used for bolus calculation
    /// </summary>
    public double? BloodGlucoseInput { get; set; }

    /// <summary>
    /// Source of blood glucose input (e.g., "Finger", "Sensor", "CGM")
    /// </summary>
    public string? BloodGlucoseInputSource { get; set; }

    /// <summary>
    /// How this bolus was calculated/initiated (Suggested, Manual, Automatic)
    /// </summary>
    public string? CalculationType { get; set; }

    /// <summary>
    /// Bolus calculator values (stored as JSON)
    /// </summary>
    public string? BolusCalcJson { get; set; }

    /// <summary>
    /// JSON-serialized bolus calculator result from AAPS containing wizard inputs and outputs
    /// </summary>
    public string? BolusCalculatorResult { get; set; }

    /// <summary>
    /// Manually entered insulin amount
    /// </summary>
    public double? EnteredInsulin { get; set; }

    /// <summary>
    /// Percentage of combo bolus delivered immediately
    /// </summary>
    public double? SplitNow { get; set; }

    /// <summary>
    /// Percentage of combo bolus delivered extended
    /// </summary>
    public double? SplitExt { get; set; }

    /// <summary>
    /// Carb ratio
    /// </summary>
    public double? CR { get; set; }

    /// <summary>
    /// Pre-bolus time in minutes
    /// </summary>
    public double? PreBolus { get; set; }
}
