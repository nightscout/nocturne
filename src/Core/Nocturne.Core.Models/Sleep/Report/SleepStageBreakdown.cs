namespace Nocturne.Core.Models.Sleep.Report;

public class SleepStageBreakdown
{
    public int DeepMinutes { get; set; }
    public int RemMinutes { get; set; }
    public int LightMinutes { get; set; }
    public int AwakeMinutes { get; set; }
    /// <summary>Minutes asleep without stage differentiation (e.g. manual entries, some Health Connect sources).</summary>
    public int UnspecifiedMinutes { get; set; }
    public int TotalMinutes { get; set; }

    public double DeepPct { get; set; }
    public double RemPct { get; set; }
    public double LightPct { get; set; }
    public double AwakePct { get; set; }
    public double UnspecifiedPct { get; set; }

    /// <summary>Normative stage ranges resolved for the patient's age and sex, included so the frontend never hardcodes thresholds. Defaults to adult-female norms until the service resolves the patient.</summary>
    public SleepStageReferenceRangeSet ReferenceRanges { get; set; } = SleepStageReferenceRangeSet.Default;
}
