namespace Nocturne.API.Models.Requests.V4;

/// <summary>
/// Request body for upserting an activity record via the V4 API.
/// </summary>
/// <seealso cref="Nocturne.API.Controllers.V4.Health.ActivityController"/>
public class UpsertActivityRequest
{
    /// <summary>
    /// When the activity occurred, as a Unix millisecond timestamp.
    /// </summary>
    public long Mills { get; set; }

    /// <summary>
    /// UTC offset in minutes at the time of the event, for local-time display.
    /// </summary>
    public int? UtcOffset { get; set; }

    /// <summary>
    /// Activity type or category (e.g., "exercise", "walk", "run").
    /// </summary>
    public string? Type { get; set; }

    /// <summary>
    /// Activity description or notes.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Duration of the activity in minutes.
    /// </summary>
    public double? Duration { get; set; }

    /// <summary>
    /// Intensity level of the activity.
    /// </summary>
    public string? Intensity { get; set; }

    /// <summary>
    /// Additional notes about the activity.
    /// </summary>
    public string? Notes { get; set; }

    /// <summary>
    /// Name of the application or person that submitted this record.
    /// </summary>
    public string? EnteredBy { get; set; }

    /// <summary>
    /// Distance covered during the activity.
    /// </summary>
    public double? Distance { get; set; }

    /// <summary>
    /// Units for distance (e.g., "meters", "kilometers", "miles").
    /// </summary>
    public string? DistanceUnits { get; set; }

    /// <summary>
    /// Energy expended during the activity (calories).
    /// </summary>
    public double? Energy { get; set; }

    /// <summary>
    /// Units for energy (e.g., "calories", "kilocalories", "joules").
    /// </summary>
    public string? EnergyUnits { get; set; }

    /// <summary>
    /// Name or title of the activity.
    /// </summary>
    public string? Name { get; set; }
}
