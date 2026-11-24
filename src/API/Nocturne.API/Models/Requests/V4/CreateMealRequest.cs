using Nocturne.Core.Models.V4;

namespace Nocturne.API.Models.Requests.V4;

/// <summary>
/// Request body for creating a correlated meal event (a single bolus + single carb
/// intake sharing a CorrelationId, created atomically).
/// </summary>
/// <seealso cref="Validators.V4.CreateMealRequestValidator"/>
/// <seealso cref="Nocturne.API.Controllers.V4.Treatments.NutritionController"/>
public class CreateMealRequest : IV4RecordHeader
{
    /// <summary>
    /// When the meal occurred.
    /// </summary>
    public DateTimeOffset Timestamp { get; set; }

    /// <summary>
    /// UTC offset in minutes at the time of the event, for local-time display.
    /// </summary>
    public int? UtcOffset { get; set; }

    /// <summary>
    /// Total insulin amount in units for the meal bolus.
    /// </summary>
    public double Insulin { get; set; }

    /// <summary>
    /// Amount of carbohydrates consumed in grams.
    /// </summary>
    public double Carbs { get; set; }

    /// <summary>
    /// Bolus delivery pattern (normal, square wave, dual wave, etc.).
    /// </summary>
    public BolusType? BolusType { get; set; }

    /// <summary>
    /// Extended/square bolus duration in minutes.
    /// </summary>
    public double? Duration { get; set; }

    /// <summary>
    /// Expected carb absorption duration in minutes.
    /// </summary>
    public int? AbsorptionTime { get; set; }

    /// <summary>
    /// Minutes from bolus time to expected carb absorption start (pre-bolus offset).
    /// </summary>
    public double? CarbTime { get; set; }

    /// <summary>
    /// Type or brand of insulin used (e.g. "Humalog", "NovoRapid").
    /// </summary>
    public string? InsulinType { get; set; }

    /// <summary>
    /// Identifier of the device that delivered the bolus.
    /// </summary>
    public string? Device { get; set; }

    /// <summary>
    /// Name of the application that submitted this record.
    /// </summary>
    public string? App { get; set; }

    /// <summary>
    /// Upstream data source identifier; required when <see cref="SyncIdentifier"/> is supplied.
    /// </summary>
    public string? DataSource { get; set; }

    /// <summary>
    /// Upstream sync identifier for deduplication, paired with <see cref="DataSource"/>.
    /// </summary>
    public string? SyncIdentifier { get; set; }

    /// <summary>
    /// Links this meal to the bolus calculation that recommended the insulin dose.
    /// </summary>
    public Guid? BolusCalculationId { get; set; }

    /// <summary>
    /// Caller-supplied correlation identifier; if omitted, the server generates one.
    /// </summary>
    public Guid? CorrelationId { get; set; }
}
