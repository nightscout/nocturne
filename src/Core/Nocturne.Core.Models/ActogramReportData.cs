namespace Nocturne.Core.Models;

/// <summary>
/// Lean dataset for the actogram report. Returned by the
/// <c>/api/v4/Actogram</c> endpoint to back the sleep, steps, and heart-rate
/// reports without invoking the dashboard chart-data pipeline.
/// </summary>
/// <seealso cref="GlucosePointDto"/>
/// <seealso cref="ChartThresholdsDto"/>
/// <seealso cref="HeartRatePointDto"/>
/// <seealso cref="StepBubbleDto"/>
public class ActogramReportData
{
    /// <summary>Glucose readings within the requested window.</summary>
    public List<GlucosePointDto> Glucose { get; set; } = new();

    /// <summary>Glucose threshold configuration derived from the active profile.</summary>
    public ChartThresholdsDto Thresholds { get; set; } = new();

    /// <summary>Heart rate samples within the requested window.</summary>
    public List<HeartRatePointDto> HeartRates { get; set; } = new();

    /// <summary>Step count samples within the requested window.</summary>
    public List<StepBubbleDto> StepCounts { get; set; } = new();

    /// <summary>
    /// Every day the half-open window touches, oldest first, as the rows the report draws. The days
    /// are the tenant's, or UTC days when <see cref="TimeZoneUnavailableReason"/> is set.
    /// </summary>
    public List<ActogramDay> Days { get; set; } = new();

    /// <summary>
    /// Total steps per entry of <see cref="Days"/>, keyed by <see cref="ActogramDay.Date"/>. Days
    /// without samples are 0.
    /// </summary>
    public Dictionary<string, int> StepDayTotals { get; set; } = new();

    /// <summary>
    /// Why <see cref="Days"/> are UTC days: the tenant time zone did not resolve.
    /// Null when the days use that zone.
    /// </summary>
    public TimeZoneUnavailableReason? TimeZoneUnavailableReason { get; set; }

    /// <summary>Sleep spans within the requested window.</summary>
    public List<ActogramSleepSpan> SleepSpans { get; set; } = new();
}

/// <summary>
/// Sleep span for the actogram. Color is resolved on the frontend from
/// <see cref="State"/> because sleep stage colors (deep / REM / light) are a
/// presentation concern.
/// </summary>
public class ActogramSleepSpan
{
    public long StartMills { get; set; }
    public long EndMills { get; set; }
    public string State { get; set; } = string.Empty;
}

/// <summary>One local day of the actogram, from its first instant to the first instant of the next day.</summary>
public class ActogramDay
{
    /// <summary>The <c>yyyy-MM-dd</c> date of the day in the report's zone.</summary>
    public string Date { get; set; } = string.Empty;
    public long StartMills { get; set; }
    /// <summary>Exclusive. A day that crosses a DST change is 23 or 25 hours long.</summary>
    public long EndMills { get; set; }

    /// <summary>
    /// The instants of local 6h, 12h and 18h of this day, then of the next day, for the row's double
    /// plot. The row's own 0h marks are <see cref="StartMills"/> and <see cref="EndMills"/>. A DST
    /// change moves these off a fixed spacing from <see cref="StartMills"/>.
    /// </summary>
    public List<long> HourMarks { get; set; } = new();
}
