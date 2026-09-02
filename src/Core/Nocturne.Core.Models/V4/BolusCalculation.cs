using NJsonSchema.Annotations;

namespace Nocturne.Core.Models.V4;

/// <summary>
/// Bolus calculator/wizard record capturing the inputs and recommendations used to compute an insulin dose.
/// </summary>
/// <remarks>
/// Corresponds to the "Bolus Wizard" or "Bolus Calculator" event in legacy <see cref="Treatment"/> records.
/// When a <see cref="Bolus"/> is produced from a <see cref="BolusCalculation"/>, the bolus references
/// this record via <see cref="Bolus.BolusCalculationId"/>.
/// </remarks>
/// <seealso cref="Treatment"/>
/// <seealso cref="IV4Record"/>
/// <seealso cref="Bolus"/>
/// <seealso cref="CalculationType"/>
[JsonSchemaFlatten]
public class BolusCalculation : V4RecordBase
{
    /// <summary>
    /// Blood glucose input value used for the calculation
    /// </summary>
    public double? BloodGlucoseInput { get; set; }

    /// <summary>
    /// Source of blood glucose input (varies by APS system)
    /// </summary>
    public string? BloodGlucoseInputSource { get; set; }

    /// <summary>
    /// Carbohydrate input value in grams
    /// </summary>
    public double? CarbInput { get; set; }

    /// <summary>
    /// Insulin on board at the time of calculation
    /// </summary>
    public double? InsulinOnBoard { get; set; }

    /// <summary>
    /// Recommended insulin dose from the calculator
    /// </summary>
    public double? InsulinRecommendation { get; set; }

    /// <summary>
    /// Carb-to-insulin ratio used in the calculation
    /// </summary>
    public double? CarbRatio { get; set; }

    /// <summary>
    /// How this calculation was determined (Suggested, Manual, Automatic)
    /// </summary>
    public CalculationType? CalculationType { get; set; }

    /// <summary>
    /// Insulin recommended specifically for carb coverage
    /// </summary>
    public double? InsulinRecommendationForCarbs { get; set; }

    /// <summary>
    /// Total insulin programmed for delivery
    /// </summary>
    public double? InsulinProgrammed { get; set; }

    /// <summary>
    /// Manually entered insulin amount
    /// </summary>
    public double? EnteredInsulin { get; set; }

    /// <summary>
    /// Percentage of combo bolus delivered immediately
    /// </summary>
    public double? SplitNow { get; set; }

    /// <summary>
    /// Percentage of combo bolus delivered as extended
    /// </summary>
    public double? SplitExt { get; set; }

    /// <summary>
    /// Pre-bolus time in minutes
    /// </summary>
    public double? PreBolus { get; set; }
}
