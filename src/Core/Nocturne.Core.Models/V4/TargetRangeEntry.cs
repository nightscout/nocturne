namespace Nocturne.Core.Models.V4;

/// <summary>
/// A single time-of-day glucose target range entry with low and high bounds
/// </summary>
public class TargetRangeEntry
{
    /// <summary>
    /// Time in HH:mm format (e.g., "06:00")
    /// </summary>
    public string Time { get; set; } = "00:00";

    /// <summary>
    /// Low glucose target in mg/dL
    /// </summary>
    public double Low { get; set; }

    /// <summary>
    /// High glucose target in mg/dL
    /// </summary>
    public double High { get; set; }

    /// <summary>
    /// Time converted to seconds since midnight for faster lookups
    /// </summary>
    public int? TimeAsSeconds { get; set; }

    /// <summary>
    /// Optional VeryLow boundary (mg/dL). Glucose strictly below this falls into the VeryLow bucket.
    /// Stored inside the parent <c>target_range_schedules.entries_json</c> JSONB column;
    /// null on legacy rows defaults to <c>54</c> (clinical convention) at consumption time.
    /// </summary>
    public short? VeryLow { get; set; }

    /// <summary>
    /// Optional TightLow boundary (mg/dL). Glucose in <c>[Low, TightHigh]</c> falls into the TightRange bucket
    /// when above <see cref="VeryLow"/>. Stored inside <c>entries_json</c>; null defaults to <c>70</c>.
    /// </summary>
    public short? TightLow { get; set; }

    /// <summary>
    /// Optional TightHigh boundary (mg/dL). Glucose in <c>[Low, TightHigh]</c> falls into the TightRange bucket.
    /// Stored inside <c>entries_json</c>; null defaults to <c>140</c>.
    /// </summary>
    public short? TightHigh { get; set; }

    /// <summary>
    /// Optional VeryHigh boundary (mg/dL). Glucose strictly above this falls into the VeryHigh bucket.
    /// Stored inside <c>entries_json</c>; null defaults to <c>250</c>.
    /// </summary>
    public short? VeryHigh { get; set; }
}
