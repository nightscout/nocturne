using System.Text.Json.Serialization;

namespace Nocturne.Core.Models.V4;

/// <summary>
/// Server-side classification of a tenant's latest glucose reading against
/// its resolved thresholds, including data-quality states.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<GlucoseStatus>))]
public enum GlucoseStatus
{
    /// <summary>No reading available to classify.</summary>
    Unknown,

    /// <summary>The most recent reading is older than the staleness window.</summary>
    Stale,

    /// <summary>Reading below the urgent-low threshold.</summary>
    UrgentLow,

    /// <summary>Reading below the low threshold.</summary>
    Low,

    /// <summary>Reading within the target range.</summary>
    InRange,

    /// <summary>Reading above the high threshold.</summary>
    High,

    /// <summary>Reading above the urgent-high threshold.</summary>
    UrgentHigh,
}
