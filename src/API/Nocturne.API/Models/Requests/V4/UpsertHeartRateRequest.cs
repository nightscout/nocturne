namespace Nocturne.API.Models.Requests.V4;

/// <summary>
/// Request body for upserting a heart rate measurement via the V4 API.
/// </summary>
/// <seealso cref="Nocturne.API.Controllers.V4.Health.HeartRateController"/>
public class UpsertHeartRateRequest
{
    /// <summary>
    /// When the heart rate measurement was taken.
    /// </summary>
    public DateTimeOffset Timestamp { get; set; }

    /// <summary>
    /// UTC offset in minutes at the time of the event, for local-time display.
    /// </summary>
    public int? UtcOffset { get; set; }

    /// <summary>
    /// Heart rate in beats per minute.
    /// </summary>
    public int Bpm { get; set; }

    /// <summary>
    /// Sensor accuracy indicator (device-specific scale).
    /// </summary>
    public int Accuracy { get; set; }

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
