using Nocturne.Core.Models;

namespace Nocturne.Core.Models.Sleep.Report;

public class SleepHypoEvent
{
    public DateTime StartAt { get; set; }
    public DateTime EndAt { get; set; }
    public int DurationMinutes { get; set; }
    /// <summary>Lowest glucose reading (mg/dL) during this event.</summary>
    public int LowestBg { get; set; }
    /// <summary>Sleep stage at the nadir reading. Unknown if no stage interval covers the nadir timestamp.</summary>
    public SleepStageType Stage { get; set; }
    public SleepHypoSeverity Severity { get; set; }
}
