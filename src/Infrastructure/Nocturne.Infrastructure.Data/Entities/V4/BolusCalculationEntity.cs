using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Nocturne.Infrastructure.Data.Entities.V4;

/// <summary>
/// PostgreSQL entity for bolus calculator/wizard records
/// Maps to Nocturne.Core.Models.V4.BolusCalculation
/// </summary>
[Table("bolus_calculations")]
public class BolusCalculationEntity : V4TimeSeriesEntityBase
{
    /// <summary>
    /// Blood glucose input value used for the calculation
    /// </summary>
    [Column("blood_glucose_input")]
    public double? BloodGlucoseInput { get; set; }

    /// <summary>
    /// Source of blood glucose input (varies by APS system)
    /// </summary>
    [Column("blood_glucose_input_source")]
    [MaxLength(256)]
    public string? BloodGlucoseInputSource { get; set; }

    /// <summary>
    /// Carbohydrate input value in grams
    /// </summary>
    [Column("carb_input")]
    public double? CarbInput { get; set; }

    /// <summary>
    /// Insulin on board at the time of calculation
    /// </summary>
    [Column("insulin_on_board")]
    public double? InsulinOnBoard { get; set; }

    /// <summary>
    /// Recommended insulin dose from the calculator
    /// </summary>
    [Column("insulin_recommendation")]
    public double? InsulinRecommendation { get; set; }

    /// <summary>
    /// Carb-to-insulin ratio used in the calculation
    /// </summary>
    [Column("carb_ratio")]
    public double? CarbRatio { get; set; }

    /// <summary>
    /// How this calculation was determined (enum stored as string: Suggested, Manual, Automatic)
    /// </summary>
    [Column("calculation_type")]
    [MaxLength(32)]
    public string? CalculationType { get; set; }

    /// <summary>
    /// Recommended amount of insulin for carbohydrates.
    /// </summary>
    [Column("insulin_recommendation_for_carbs")]
    public double? InsulinRecommendationForCarbs { get; set; }

    /// <summary>
    /// Total amount of insulin programmed for delivery.
    /// </summary>
    [Column("insulin_programmed")]
    public double? InsulinProgrammed { get; set; }

    /// <summary>
    /// The amount of insulin entered by the user.
    /// </summary>
    [Column("entered_insulin")]
    public double? EnteredInsulin { get; set; }

    /// <summary>
    /// Portion of dual/square bolus to be delivered immediately.
    /// </summary>
    [Column("split_now")]
    public double? SplitNow { get; set; }

    /// <summary>
    /// Extended portion of dual/square bolus.
    /// </summary>
    [Column("split_ext")]
    public double? SplitExt { get; set; }

    /// <summary>
    /// Amount of insulin delivered as a pre-bolus.
    /// </summary>
    [Column("pre_bolus")]
    public double? PreBolus { get; set; }
}
