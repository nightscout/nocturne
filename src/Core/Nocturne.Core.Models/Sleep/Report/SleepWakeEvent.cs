namespace Nocturne.Core.Models.Sleep.Report;

public class SleepWakeEvent
{
    public DateTime StartAt { get; set; }
    public DateTime EndAt { get; set; }
    public int DurationMinutes { get; set; }
    /// <summary>Glucose at wake start. Null if no CGM reading falls within 15 minutes.</summary>
    public int? BgAtStart { get; set; }
    /// <summary>True if this awake interval occurs before sleep onset (settling-in period).</summary>
    public bool IsPreSleep { get; set; }
    /// <summary>True if this awake interval occurs after the main sleep period ends.</summary>
    public bool IsPostSleep { get; set; }
}
