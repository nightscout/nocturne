using System.Text.Json.Serialization;

namespace Nocturne.Core.Models.Widget;

/// <summary>
/// Represents predicted glucose values for widget display.
/// Contains a series of predicted values at regular intervals.
/// </summary>
public class V4Predictions
{
    /// <summary>
    /// Gets or sets the predicted glucose values in mg/dL.
    /// Values are ordered chronologically starting from StartMills.
    /// </summary>
    [JsonPropertyName("values")]
    public List<double>? Values { get; set; }

    /// <summary>
    /// Gets or sets the start timestamp for predictions in milliseconds since Unix epoch.
    /// </summary>
    [JsonPropertyName("startMills")]
    public long StartMills { get; set; }

    /// <summary>
    /// Gets or sets the interval between predicted values in milliseconds.
    /// Typically 5 minutes (300000ms) for standard CGM intervals.
    /// </summary>
    [JsonPropertyName("intervalMills")]
    public long IntervalMills { get; set; }

    /// <summary>
    /// Gets or sets the source of the predictions (e.g., "loop", "openaps", "oref").
    /// </summary>
    [JsonPropertyName("source")]
    public string? Source { get; set; }
}
