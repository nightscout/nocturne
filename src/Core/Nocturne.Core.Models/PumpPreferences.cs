namespace Nocturne.Core.Models;

/// <summary>
/// The thresholds of Nightscout's <c>pump.js</c> <c>getPrefs()</c>, at its defaults.
/// </summary>
public class PumpPreferences
{
    /// <summary>
    /// Minutes before pump clock is considered stale (warn level)
    /// </summary>
    public int WarnClock { get; set; } = 30;

    /// <summary>
    /// Minutes before pump clock is considered stale (urgent level)
    /// </summary>
    public int UrgentClock { get; set; } = 60;

    /// <summary>
    /// Reservoir units threshold for warning alert
    /// </summary>
    public double WarnRes { get; set; } = 10;

    /// <summary>
    /// Reservoir units threshold for urgent alert
    /// </summary>
    public double UrgentRes { get; set; } = 5;

    /// <summary>
    /// Battery voltage threshold for warning (for voltage-based pumps like Medtronic)
    /// </summary>
    public double WarnBattV { get; set; } = 1.35;

    /// <summary>
    /// Battery voltage threshold for urgent (for voltage-based pumps like Medtronic)
    /// </summary>
    public double UrgentBattV { get; set; } = 1.3;

    /// <summary>
    /// Battery percentage threshold for warning
    /// </summary>
    public int WarnBattP { get; set; } = 30;

    /// <summary>
    /// Battery percentage threshold for urgent
    /// </summary>
    public int UrgentBattP { get; set; } = 20;

    /// <summary>
    /// Whether to generate warning when pump is suspended
    /// </summary>
    public bool WarnOnSuspend { get; set; }
}
