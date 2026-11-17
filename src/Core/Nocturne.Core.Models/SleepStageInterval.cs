using System.Text.Json.Serialization;

namespace Nocturne.Core.Models;

/// <summary>
/// A contiguous interval of a single sleep stage within a session.
/// </summary>
public class SleepStageInterval
{
    /// <summary>
    /// UTC start time of this stage interval.
    /// </summary>
    [JsonPropertyName("startTime")]
    public DateTime StartTime { get; set; }

    /// <summary>
    /// UTC end time of this stage interval.
    /// </summary>
    [JsonPropertyName("endTime")]
    public DateTime EndTime { get; set; }

    [JsonPropertyName("stage")]
    public SleepStageType Stage { get; set; }

    /// <summary>
    /// Zero-based position of this interval within the session.
    /// </summary>
    [JsonPropertyName("ordinal")]
    public int Ordinal { get; set; }
}
