using System.Text.Json.Serialization;

namespace Nocturne.Core.Models.Widget;

/// <summary>
/// Represents a glucose reading optimized for widget display.
/// Contains essential glucose data including value, direction, and trend information.
/// </summary>
public class V4GlucoseReading
{
    /// <summary>
    /// Gets or sets the sensor glucose value in mg/dL.
    /// </summary>
    [JsonPropertyName("sgv")]
    public double Sgv { get; set; }

    /// <summary>
    /// Gets or sets the direction of glucose trend.
    /// </summary>
    [JsonPropertyName("direction")]
    public Direction Direction { get; set; }

    /// <summary>
    /// Gets or sets the rate of glucose change in mg/dL per minute.
    /// Positive values indicate rising glucose, negative values indicate falling.
    /// </summary>
    [JsonPropertyName("trendRate")]
    public double? TrendRate { get; set; }

    /// <summary>
    /// Gets or sets the delta (change) from the previous reading in mg/dL.
    /// </summary>
    [JsonPropertyName("delta")]
    public double? Delta { get; set; }

    /// <summary>
    /// Gets or sets the timestamp in milliseconds since Unix epoch.
    /// </summary>
    [JsonPropertyName("mills")]
    public long Mills { get; set; }

    /// <summary>
    /// Gets or sets the noise level (0-4) indicating signal quality.
    /// </summary>
    [JsonPropertyName("noise")]
    public int? Noise { get; set; }
}
