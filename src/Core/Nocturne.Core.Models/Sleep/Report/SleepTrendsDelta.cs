namespace Nocturne.Core.Models.Sleep.Report;

/// <summary>Change in key metrics between the most recent 7 nights and the prior 7 nights.</summary>
public class SleepTrendsDelta
{
    public double? ScoreDelta { get; set; }
    public double? TirDelta { get; set; }
    public double? DeepMinutesDelta { get; set; }
    public double? DawnRiseDelta { get; set; }
}
