namespace Nocturne.Core.Models.Sleep.Report;

/// <summary>Per-night summary for the 30-night trends views.</summary>
public class SleepNightSummary
{
    public Guid SessionId { get; set; }
    /// <summary>Session start date formatted as "MMM d" (e.g. "May 16").</summary>
    public string Date { get; set; } = string.Empty;
    /// <summary>
    /// The display-night date (YYYY-MM-DD) this session buckets to under the noon
    /// rule, in the session's timezone. Authoritative key for deep-linking the
    /// single-night report — the frontend must not recompute it from timestamps.
    /// </summary>
    public string DisplayDate { get; set; } = string.Empty;
    /// <summary>Abbreviated weekday name (e.g. "Thu").</summary>
    public string Weekday { get; set; } = string.Empty;
    public DateTime InBedAt { get; set; }
    public DateTime WakeAt { get; set; }
    public int SleepMinutes { get; set; }
    public int DeepMinutes { get; set; }
    public int RemMinutes { get; set; }
    public int LightMinutes { get; set; }
    public int AwakeMinutes { get; set; }
    /// <summary>Minutes asleep without stage differentiation (e.g. manual entries, some Health Connect sources).</summary>
    public int UnspecifiedMinutes { get; set; }
    public int? SleepScore { get; set; }
    public SleepScoreSource? ScoreSource { get; set; }
    /// <summary>Overnight time-in-range percentage (70–180 mg/dL). Null if no CGM data.</summary>
    public double? OvernightTirPct { get; set; }
    public int HypoCount { get; set; }
    /// <summary>Lowest glucose reading during sleep. Null if no CGM data.</summary>
    public int? LowestBg { get; set; }
    /// <summary>Dawn rise delta (mg/dL). Null if insufficient pre-wake CGM data.</summary>
    public int? DawnRiseDeltaMg { get; set; }
    /// <summary>Mean HRV during sleep in milliseconds. Null if not reported by the source device.</summary>
    public double? HrvMeanMs { get; set; }
}
