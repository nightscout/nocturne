namespace Nocturne.Core.Models.Sleep.Report;

public class SleepTrendsReport
{
    /// <summary>Per-night summaries, oldest first.</summary>
    public IReadOnlyList<SleepNightSummary> Nights { get; set; } = [];
    /// <summary>Per-week aggregates covering the requested range, oldest first. Weeks with no recorded night are included.</summary>
    public IReadOnlyList<SleepWeekSummary> Weeks { get; set; } = [];
    public SleepTrendsSummary Summary { get; set; } = new();
}
