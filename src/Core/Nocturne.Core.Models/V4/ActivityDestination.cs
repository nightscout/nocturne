namespace Nocturne.Core.Models.V4;

/// <summary>
/// The storage a legacy <see cref="Activity"/> is written to.
/// </summary>
public enum ActivityDestination
{
    /// <summary>Regular activities (exercise, illness, travel), stored as state spans.</summary>
    StateSpan,

    /// <summary>The <c>heart_rates</c> table.</summary>
    HeartRate,

    /// <summary>The <c>step_counts</c> table.</summary>
    StepCount,

    /// <summary>The <c>sleep_sessions</c> table.</summary>
    SleepSession,
}
