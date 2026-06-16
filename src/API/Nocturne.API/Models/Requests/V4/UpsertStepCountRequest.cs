namespace Nocturne.API.Models.Requests.V4;

/// <summary>
/// Request body for upserting a step count measurement via the V4 API.
/// </summary>
/// <seealso cref="Nocturne.API.Controllers.V4.Health.StepCountController"/>
public class UpsertStepCountRequest
{
    /// <summary>
    /// When the step count was recorded.
    /// </summary>
    public DateTimeOffset Timestamp { get; set; }

    /// <summary>
    /// UTC offset in minutes at the time of the event, for local-time display.
    /// </summary>
    public int? UtcOffset { get; set; }

    /// <summary>
    /// Step count metric value for the measurement period.
    /// </summary>
    public int Metric { get; set; }

    /// <summary>
    /// Source identifier for the step data (device-specific).
    /// </summary>
    public int Source { get; set; }

    /// <summary>
    /// Identifier of the wearable or sensor device.
    /// </summary>
    public string? Device { get; set; }

    /// <summary>
    /// Name of the application that submitted this record.
    /// </summary>
    public string? App { get; set; }

    /// <summary>
    /// Upstream data source identifier; paired with <see cref="SyncIdentifier"/> for dedup.
    /// </summary>
    public string? DataSource { get; set; }

    /// <summary>
    /// Stable per-source identifier. When paired with <see cref="DataSource"/>, re-uploading the
    /// same measurement updates the existing record in place rather than creating a duplicate.
    /// </summary>
    public string? SyncIdentifier { get; set; }
}
