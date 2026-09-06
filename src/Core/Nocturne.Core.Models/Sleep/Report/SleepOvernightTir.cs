namespace Nocturne.Core.Models.Sleep.Report;

/// <summary>
/// Time-in-range breakdown for the overnight sleep window,
/// computed using GlycemicThresholds (default 54/70/180/250 mg/dL).
/// </summary>
public class SleepOvernightTir
{
    public double VeryLowPct { get; set; }
    public double LowPct { get; set; }
    public double InRangePct { get; set; }
    public double HighPct { get; set; }
    public double VeryHighPct { get; set; }
    /// <summary>Mean glucose (mg/dL, rounded) during the sleep window.</summary>
    public int MeanBg { get; set; }
}
