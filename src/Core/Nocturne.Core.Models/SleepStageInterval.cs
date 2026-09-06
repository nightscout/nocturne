using System.Text.Json.Serialization;

namespace Nocturne.Core.Models;

/// <summary>
/// A contiguous interval of a single sleep stage within a session.
/// </summary>
public class SleepStageInterval
{
    /// <summary>
    /// Gets or sets the UTC start time of this stage interval.
    /// </summary>
    [JsonPropertyName("startTime")]
    public DateTime StartTime { get; set; }

    /// <summary>
    /// Gets or sets the UTC end time of this stage interval.
    /// </summary>
    [JsonPropertyName("endTime")]
    public DateTime EndTime { get; set; }

    /// <summary>
    /// Gets or sets the sleep stage classification.
    /// </summary>
    [JsonPropertyName("stage")]
    public SleepStageType Stage { get; set; }

    /// <summary>
    /// Gets or sets the zero-based position of this interval within the session.
    /// </summary>
    [JsonPropertyName("ordinal")]
    public int Ordinal { get; set; }
}
