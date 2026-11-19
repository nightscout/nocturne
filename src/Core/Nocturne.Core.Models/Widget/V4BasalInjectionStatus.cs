using System.Text.Json.Serialization;

namespace Nocturne.Core.Models.Widget;

/// <summary>
/// The last long-acting basal dose of a tenant on multiple daily injections, and the rate it
/// amounts to now.
/// </summary>
public class V4BasalInjectionStatus
{
    /// <summary>
    /// Units of the last dose. Doses given at the same instant are summed.
    /// </summary>
    [JsonPropertyName("units")]
    public double Units { get; set; }

    /// <summary>
    /// When the last dose was given, in milliseconds since Unix epoch.
    /// </summary>
    [JsonPropertyName("mills")]
    public long Mills { get; set; }

    /// <summary>
    /// Name of the insulin the dose records. Null when the dose recorded none.
    /// </summary>
    [JsonPropertyName("insulinName")]
    public string? InsulinName { get; set; }

    /// <summary>
    /// The dose spread over the time it covers, in units per hour. Null when no dose covers now,
    /// that is, when the last one has run out.
    /// </summary>
    [JsonPropertyName("estimatedRate")]
    public double? EstimatedRate { get; set; }
}
