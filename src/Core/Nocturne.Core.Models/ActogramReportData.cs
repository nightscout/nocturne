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
