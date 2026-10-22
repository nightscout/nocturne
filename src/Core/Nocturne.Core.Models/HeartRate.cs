using System.Text.Json.Serialization;
using Nocturne.Core.Models.Attributes;

namespace Nocturne.Core.Models;

/// <summary>
/// Heart rate reading, compatible with xDrip heart rate uploads.
/// Heart rate data is time-series sensor data similar to glucose entries,
/// typically sourced from wearable devices via xDrip.
/// </summary>
/// <seealso cref="StepCount"/>
/// <seealso cref="ProcessableDocumentBase"/>
public class HeartRate : ProcessableDocumentBase
{
    /// <summary>
    /// MongoDB ObjectId
    /// </summary>
    [JsonPropertyName("_id")]
    public override string? Id { get; set; }

    /// <summary>
    /// ISO 8601 formatted creation timestamp
    /// </summary>
    [JsonPropertyName("created_at")]
    public override string? CreatedAt { get; set; } =
        DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

    /// <summary>
    /// Timestamp in milliseconds since Unix epoch.
    /// Setting Mills converts the value to Timestamp internally (v1 compatibility).
    /// </summary>
    [JsonPropertyName("mills")]
    public override long Mills
    {
        get => new DateTimeOffset(Timestamp, TimeSpan.Zero).ToUnixTimeMilliseconds();
        set => Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(value).UtcDateTime;
    }

    /// <summary>
    /// Canonical timestamp as UTC DateTime (source of truth)
    /// </summary>
    [JsonPropertyName("timestamp")]
    public DateTime Timestamp { get; set; }

    /// <summary>
    /// UTC offset in minutes
    /// </summary>
    [JsonPropertyName("utcOffset")]
    public override int? UtcOffset { get; set; }

    /// <summary>
    /// Heart rate in beats per minute
    /// </summary>
    [JsonPropertyName("bpm")]
    public int Bpm { get; set; }

    /// <summary>
    /// Accuracy of the heart rate reading.
    /// Higher values indicate greater confidence in the measurement.
    /// </summary>
    [JsonPropertyName("accuracy")]
    public int Accuracy { get; set; }

    /// <summary>
    /// The legacy <c>type</c> the record was uploaded with (xDrip sends <c>hr-bpm</c>), returned on
    /// the v1 activity read-back.
    /// </summary>
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    /// <summary>
    /// Device identifier that recorded this reading
    /// </summary>
    [JsonPropertyName("device")]
    [Sanitizable]
    public string? Device { get; set; }

    [JsonPropertyName("enteredBy")]
    [Sanitizable]
    public string? EnteredBy { get; set; }

    /// <summary>
    /// Data source identifier indicating where this reading originated from
    /// </summary>
    [JsonPropertyName("data_source")]
    [NocturneOnly]
    public string? DataSource { get; set; }

    /// <summary>
    /// Stable per-source identifier for synchronization. When paired with <see cref="DataSource"/>,
    /// re-uploading the same measurement updates the existing record in place rather than duplicating it.
    /// </summary>
    [JsonPropertyName("syncIdentifier")]
    [NocturneOnly]
    public string? SyncIdentifier { get; set; }
}
