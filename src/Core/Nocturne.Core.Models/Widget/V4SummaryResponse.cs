using System.Text.Json.Serialization;

namespace Nocturne.Core.Models.Widget;

/// <summary>
/// Widget-friendly summary response providing essential diabetes management data.
/// Designed for compact consumption by mobile widgets, watch faces, and other constrained displays.
/// </summary>
public class V4SummaryResponse
{
    /// <summary>
    /// Gets or sets the current (most recent) glucose reading.
    /// Null if no recent reading is available.
    /// </summary>
    [JsonPropertyName("current")]
    public V4GlucoseReading? Current { get; set; }

    /// <summary>
    /// Gets or sets the historical glucose readings for trend display.
    /// Ordered from oldest to newest.
    /// </summary>
    [JsonPropertyName("history")]
    public List<V4GlucoseReading> History { get; set; } = [];

    /// <summary>
    /// Gets or sets the current insulin on board in units.
    /// </summary>
    [JsonPropertyName("iob")]
    public double Iob { get; set; }

    /// <summary>
    /// Gets or sets the current carbs on board in grams.
    /// </summary>
    [JsonPropertyName("cob")]
    public double Cob { get; set; }

    /// <summary>
    /// Gets or sets the current status of active trackers.
    /// Includes device ages, consumable status, and scheduled events.
    /// </summary>
    [JsonPropertyName("trackers")]
    public List<V4TrackerStatus> Trackers { get; set; } = [];

    /// <summary>
    /// Gets or sets the current alarm state.
    /// Null if no alarm is active.
    /// </summary>
    [JsonPropertyName("alarm")]
    public V4AlarmState? Alarm { get; set; }

    /// <summary>
    /// Gets or sets the predicted glucose values.
    /// Null if predictions are not available.
    /// </summary>
    [JsonPropertyName("predictions")]
    public V4Predictions? Predictions { get; set; }

    /// <summary>
    /// Gets or sets the server timestamp when this response was generated in milliseconds since Unix epoch.
    /// Useful for calculating data freshness on the client.
    /// </summary>
    [JsonPropertyName("serverMills")]
    public long ServerMills { get; set; }
}
