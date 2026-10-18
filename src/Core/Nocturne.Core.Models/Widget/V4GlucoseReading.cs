using System.Text.Json.Serialization;
using Nocturne.Core.Models.V4;

namespace Nocturne.Core.Models.Widget;

/// <summary>
/// Glucose reading optimized for widget display.
/// Contains essential glucose data including value, direction, and trend information.
/// </summary>
public class V4GlucoseReading
{
    /// <summary>
    /// Sensor glucose value in mg/dL.
    /// </summary>
    [JsonPropertyName("sgv")]
    public double Sgv { get; set; }

    [JsonPropertyName("direction")]
    public Direction Direction { get; set; }

    /// <summary>
    /// Rate of glucose change in mg/dL per minute.
    /// Positive values indicate rising glucose, negative values indicate falling.
    /// </summary>
    [JsonPropertyName("trendRate")]
    public double? TrendRate { get; set; }

    /// <summary>
    /// Delta (change) from the previous reading in mg/dL.
    /// </summary>
    [JsonPropertyName("delta")]
    public double? Delta { get; set; }

    /// <summary>
    /// Timestamp in milliseconds since Unix epoch.
    /// </summary>
    [JsonPropertyName("mills")]
    public long Mills { get; set; }

    /// <summary>
    /// Noise level (0-4) indicating signal quality.
    /// </summary>
    [JsonPropertyName("noise")]
    public int? Noise { get; set; }

    /// <summary>
    /// Server's classification of this reading against the tenant's thresholds.
    /// Set on the current reading only; null on history.
    /// </summary>
    [JsonPropertyName("status")]
    public GlucoseStatus? Status { get; set; }
}
