namespace Nocturne.Core.Models.Sleep.Report;

/// <summary>
/// Pre-wake glucose trend in the final 2 hours before the session ended.
/// Null on the report when fewer than 4 CGM readings exist in the window.
/// Negative DeltaBg indicates a declining pre-wake trend (no dawn rise).
/// </summary>
public class SleepDawnPhenomenon
{
    public DateTime WindowStart { get; set; }
    public DateTime WindowEnd { get; set; }
    public int TroughBg { get; set; }
    public int PeakBg { get; set; }
    /// <summary>Peak minus trough. Negative means glucose declined in the pre-wake window.</summary>
    public int DeltaBg { get; set; }
    /// <summary>Rate of change from trough to peak in mg/dL per hour. Negative = declining.</summary>
    public double RateOfClimbPerHour { get; set; }
}
