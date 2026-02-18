using System.Text.Json.Serialization;
using Nocturne.Core.Models.Attributes;

namespace Nocturne.Core.Models;

/// <summary>
/// Represents a heart rate reading, compatible with xDrip heart rate uploads.
/// Heart rate data is time-series sensor data similar to glucose entries,
/// typically sourced from wearable devices via xDrip.
/// </summary>
public class HeartRate : ProcessableDocumentBase
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
    /// Gets or sets the timestamp in milliseconds since Unix epoch
    /// </summary>
    [JsonPropertyName("mills")]
    public override long Mills { get; set; }

    /// <summary>
    /// Gets or sets the UTC offset in minutes
    /// </summary>
    [JsonPropertyName("utcOffset")]
    public override int? UtcOffset { get; set; }

    /// <summary>
    /// Gets or sets the heart rate in beats per minute
    /// </summary>
    [JsonPropertyName("bpm")]
    public int Bpm { get; set; }

    /// <summary>
    /// Gets or sets the accuracy of the heart rate reading.
    /// Higher values indicate greater confidence in the measurement.
    /// </summary>
    [JsonPropertyName("accuracy")]
    public int Accuracy { get; set; }

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
}
