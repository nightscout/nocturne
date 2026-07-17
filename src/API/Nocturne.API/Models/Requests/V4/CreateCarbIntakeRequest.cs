namespace Nocturne.API.Models.Requests.V4;

/// <summary>
/// Request body for creating a new carbohydrate intake record via the V4 API.
/// </summary>
/// <seealso cref="Validators.V4.CreateCarbIntakeRequestValidator"/>
/// <seealso cref="Nocturne.API.Controllers.V4.Treatments.NutritionController"/>
public class CreateCarbIntakeRequest
{
    /// <summary>
    /// When the carbs were consumed.
    /// </summary>
    public DateTimeOffset Timestamp { get; set; }

    /// <summary>
    /// UTC offset in minutes at the time of the event, for local-time display.
    /// </summary>
    public int? UtcOffset { get; set; }

    /// <summary>
    /// Identifier of the device that recorded the intake.
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
    /// Amount of carbohydrates consumed in grams.
    /// </summary>
    public double Carbs { get; set; }

    /// <summary>
    /// Upstream sync identifier for deduplication, paired with <see cref="DataSource"/>.
    /// </summary>
    public string? SyncIdentifier { get; set; }

    /// <summary>
    /// Minutes from bolus time to expected carb absorption start (pre-bolus offset).
    /// </summary>
    public double? CarbTime { get; set; }

    /// <summary>
    /// Expected carb absorption duration in minutes.
    /// </summary>
    public int? AbsorptionTime { get; set; }

    /// <summary>
    /// Fat consumed in grams, when the source reports macros. Native fields replace the
    /// synthesized FPU fake-carb series legacy uploaders emit for Nightscout.
    /// </summary>
    public double? FatGrams { get; set; }

    /// <summary>
    /// Protein consumed in grams, when the source reports macros.
    /// </summary>
    public double? ProteinGrams { get; set; }

    /// <summary>
    /// Correlation identifier for grouping related events (e.g. a meal bolus and carb intake).
    /// </summary>
    public Guid? CorrelationId { get; set; }
}
