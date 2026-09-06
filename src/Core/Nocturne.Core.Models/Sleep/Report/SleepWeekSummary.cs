namespace Nocturne.Core.Models.Sleep.Report;

/// <summary>Per-week aggregate for the trends report. Weeks start on Monday.</summary>
public class SleepWeekSummary
{
    /// <summary>Monday of the week (date only).</summary>
    public DateTime WeekStart { get; set; }

    /// <summary>Sunday of the week (date only).</summary>
    public DateTime WeekEnd { get; set; }

    /// <summary>Week span formatted "MMM d – MMM d" (e.g. "May 12 – May 18").</summary>
    public string Label { get; set; } = string.Empty;

    public int NightCount { get; set; }

    /// <summary>Days of this week that fall inside the requested range, so partial edge weeks aren't scored against 7 days.</summary>
    public int DaysInRange { get; set; }

    public double MeanAsleepMinutes { get; set; }

    /// <summary>Mean sleep score across scored nights. Null when no night in the week has a score.</summary>
    public double? MeanScore { get; set; }

    /// <summary>Mean overnight TIR across CGM nights. Null when no night in the week has CGM data.</summary>
    public double? MeanTirPct { get; set; }

    public int TotalHypoCount { get; set; }

    /// <summary>Session ids of this week's nights, oldest first, for linking to the single-night report.</summary>
    public IReadOnlyList<Guid> SessionIds { get; set; } = [];
}
