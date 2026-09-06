namespace Nocturne.Core.Models.Sleep.Report;

public class SleepTrendsSummary
{
    public int NightCount { get; set; }
    /// <summary>Calendar days in the requested range, inclusive of both endpoints.</summary>
    public int DaysInRange { get; set; }
    /// <summary>NightCount / DaysInRange as a percentage, clamped to 100. 0 when DaysInRange is 0.</summary>
    public double CoveragePct { get; set; }
    public double? MeanScore { get; set; }
    public double? MeanTirPct { get; set; }
    public double MeanAsleepMinutes { get; set; }
    public double MeanDeepPct { get; set; }
    public double MeanRemPct { get; set; }
    public double? MeanDawnRiseMg { get; set; }
    public double? MeanHrvMs { get; set; }
    public double NightsWithHypoPct { get; set; }
    public int TotalHypoCount { get; set; }
    public SleepTrendsDelta Last7dVsPrior7d { get; set; } = new();
    /// <summary>Normative stage ranges resolved for the patient's age and sex, included so the frontend never hardcodes thresholds. Defaults to adult-female norms until the service resolves the patient.</summary>
    public SleepStageReferenceRangeSet ReferenceRanges { get; set; } = SleepStageReferenceRangeSet.Default;
}
