using System.Text.Json.Serialization;
using Nocturne.Core.Models.Attributes;

namespace Nocturne.Core.Models;

/// <summary>
/// Represents a step count (PebbleMovement) record, compatible with xDrip step count uploads.
/// Step count data is time-series movement data typically sourced from wearable devices via xDrip.
/// </summary>
/// <seealso cref="HeartRate"/>
/// <seealso cref="ProcessableDocumentBase"/>
public class StepCount : ProcessableDocumentBase
{
    /// <summary>
    /// Gets or sets the MongoDB ObjectId
    /// </summary>
    [JsonPropertyName("_id")]
    public override string? Id { get; set; }

    /// <summary>
    /// Gets or sets the ISO 8601 formatted creation timestamp
    /// </summary>
    [JsonPropertyName("created_at")]
    public override string? CreatedAt { get; set; } =
        DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

    /// <summary>
    /// Gets or sets the timestamp in milliseconds since Unix epoch.
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
    /// Gets or sets the UTC offset in minutes
    /// </summary>
    [JsonPropertyName("utcOffset")]
    public override int? UtcOffset { get; set; }

    /// <summary>
    /// Gets or sets the step count or movement metric value.
    /// May be an absolute total or a delta depending on <see cref="Source"/>.
    /// </summary>
    [JsonPropertyName("metric")]
    public int Metric { get; set; }

    /// <summary>
    /// Gets or sets the source bitmask.
    /// Bit 0 (value 1) indicates an absolute step count total; otherwise the value is a delta.
    /// </summary>
    [JsonPropertyName("source")]
    public int Source { get; set; }

    /// <summary>
    /// Gets or sets the device identifier that recorded this reading
    /// </summary>
    [JsonPropertyName("device")]
    [Sanitizable]
    public string? Device { get; set; }

    /// <summary>
    /// Gets or sets who entered this record
    /// </summary>
    [JsonPropertyName("enteredBy")]
    [Sanitizable]
    public string? EnteredBy { get; set; }

    /// <summary>
    /// Gets or sets the data source identifier indicating where this reading originated from
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
