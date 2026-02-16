namespace Nocturne.Core.Models.V4;

/// <summary>
/// Bolus calculator/wizard record capturing the inputs and recommendations
/// </summary>
public class BolusCalculation : IV4Record
{
    /// <summary>
    /// UUID v7 primary key
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Canonical timestamp in Unix milliseconds
    /// </summary>
    public long Mills { get; set; }

    /// <summary>
    /// UTC offset in minutes
    /// </summary>
    public int? UtcOffset { get; set; }

    /// <summary>
    /// Device identifier that performed this calculation
    /// </summary>
    public string? Device { get; set; }

    /// <summary>
    /// Application that uploaded this calculation
    /// </summary>
    public string? App { get; set; }

    /// <summary>
    /// Origin data source identifier
    /// </summary>
    public string? DataSource { get; set; }

    /// <summary>
    /// Links records that were split from the same legacy Treatment
    /// </summary>
    public Guid? CorrelationId { get; set; }

    /// <summary>
    /// Original v1/v3 record ID for migration traceability
    /// </summary>
    public string? LegacyId { get; set; }

    /// <summary>
    /// When this record was created
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// When this record was last modified
    /// </summary>
    public DateTime ModifiedAt { get; set; }

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
