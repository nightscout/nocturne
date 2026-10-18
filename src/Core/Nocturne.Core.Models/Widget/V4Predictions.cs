using System.Text.Json.Serialization;

namespace Nocturne.Core.Models.Widget;

/// <summary>
/// Predicted glucose values for widget display.
/// Contains a series of predicted values at regular intervals.
/// </summary>
public class V4Predictions
{
    /// <summary>
    /// Predicted glucose values in mg/dL.
    /// Values are ordered chronologically starting from StartMills.
    /// </summary>
    [JsonPropertyName("values")]
    public List<double>? Values { get; set; }

    /// <summary>
    /// Start timestamp for predictions in milliseconds since Unix epoch.
    /// </summary>
    [JsonPropertyName("startMills")]
    public long StartMills { get; set; }

    /// <summary>
    /// Interval between predicted values in milliseconds.
    /// Typically 5 minutes (300000ms) for standard CGM intervals.
    /// </summary>
    [JsonPropertyName("intervalMills")]
    public long IntervalMills { get; set; }

    /// <summary>
    /// Source of the predictions (e.g., "loop", "openaps", "oref").
    /// </summary>
    [JsonPropertyName("source")]
    public string? Source { get; set; }
}
