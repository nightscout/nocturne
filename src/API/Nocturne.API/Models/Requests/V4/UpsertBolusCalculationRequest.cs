using Nocturne.Core.Models.V4;

namespace Nocturne.API.Models.Requests.V4;

/// <summary>
/// Request body for upserting a bolus calculator wizard record via the V4 API.
/// Captures the inputs and recommendation from a bolus calculation event.
/// </summary>
/// <seealso cref="Validators.V4.UpsertBolusCalculationRequestValidator"/>
/// <seealso cref="Nocturne.API.Controllers.V4.Treatments.BolusCalculationController"/>
public class UpsertBolusCalculationRequest : IBulkUpsertRequest
{
    /// <summary>
    /// When the bolus calculation was performed.
    /// </summary>
    public DateTimeOffset Timestamp { get; set; }

    /// <summary>
    /// UTC offset in minutes at the time of the event, for local-time display.
    /// </summary>
    public int? UtcOffset { get; set; }

    /// <summary>
    /// Identifier of the device that ran the calculation.
    /// </summary>
    public string? Device { get; set; }

    /// <summary>
    /// Name of the application that submitted this record.
    /// </summary>
    public string? App { get; set; }

    /// <summary>
    /// Upstream data source identifier.
    /// </summary>
    public string? DataSource { get; set; }

    /// <summary>
    /// Blood glucose value used as input to the calculator.
    /// </summary>
    public double? BloodGlucoseInput { get; set; }

    /// <summary>
    /// Source of the BG input (e.g. "CGM", "Manual", "Meter").
    /// </summary>
    public string? BloodGlucoseInputSource { get; set; }

    /// <summary>
    /// Carbohydrate amount (grams) used as input to the calculator.
    /// </summary>
    public double? CarbInput { get; set; }

    /// <summary>
    /// Insulin on board at the time of calculation, in units.
    /// </summary>
    public double? InsulinOnBoard { get; set; }

    /// <summary>
    /// Total insulin dose recommended by the calculator, in units.
    /// </summary>
    public double? InsulinRecommendation { get; set; }

    /// <summary>
    /// Insulin-to-carb ratio used in the calculation (grams per unit). Must be strictly positive.
    /// </summary>
    public double? CarbRatio { get; set; }

    /// <summary>
    /// Type of calculation performed (e.g. correction only, meal only, combined).
    /// </summary>
    public CalculationType? CalculationType { get; set; }

    /// <summary>
    /// Portion of the recommendation attributable to carb coverage.
    /// </summary>
    public double? InsulinRecommendationForCarbs { get; set; }

    /// <summary>
    /// Insulin amount actually programmed into the pump.
    /// </summary>
    public double? InsulinProgrammed { get; set; }

    /// <summary>
    /// Insulin amount manually entered by the user.
    /// </summary>
    public double? EnteredInsulin { get; set; }

    /// <summary>
    /// Percentage of a dual-wave bolus delivered immediately.
    /// </summary>
    public double? SplitNow { get; set; }

    /// <summary>
    /// Percentage of a dual-wave bolus delivered as extended.
    /// </summary>
    public double? SplitExt { get; set; }

    /// <summary>
    /// Pre-bolus time in minutes (insulin given before eating).
    /// </summary>
    public double? PreBolus { get; set; }

    // Bolus calculations carry no sync key.
    string? IBulkUpsertRequest.SyncIdentifier => null;
}
