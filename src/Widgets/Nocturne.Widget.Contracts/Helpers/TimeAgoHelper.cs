namespace Nocturne.Widget.Contracts.Helpers;

/// <summary>
/// Formats timestamps as relative "time ago" strings and evaluates staleness.
/// </summary>
public static class TimeAgoHelper
{
    /// <summary>
    /// Formats a <see cref="DateTimeOffset"/> as a relative time string (e.g. "3m ago").
    /// </summary>
    public static string Format(DateTimeOffset timestamp)
    {
        var age = DateTimeOffset.UtcNow - timestamp;
        return Format(age);
    }

    /// <summary>
    /// Formats a <see cref="TimeSpan"/> age as a relative time string.
    /// </summary>
    public static string Format(TimeSpan age)
    {
        return age.TotalMinutes switch
        {
            < 1 => "just now",
            < 2 => "1m ago",
            < 60 => $"{(int)age.TotalMinutes}m ago",
            < 120 => "1h ago",
            < 1440 => $"{(int)age.TotalHours}h ago",
            _ => $"{(int)age.TotalDays}d ago",
        };
    }

    /// <summary>
    /// Formats milliseconds as a relative time string.
    /// Useful when working with Unix-millisecond age values directly.
    /// </summary>
    public static string FormatMilliseconds(long milliseconds)
    {
        if (milliseconds < 0) return "just now";
        var age = TimeSpan.FromMilliseconds(milliseconds);
        return Format(age);
    }

    /// <summary>
    /// Returns true if the reading is considered stale (older than the specified threshold).
    /// </summary>
    /// <param name="timestamp">The timestamp of the reading.</param>
    /// <param name="staleMinutes">Number of minutes after which a reading is considered stale. Defaults to 15.</param>
    public static bool IsStale(DateTimeOffset timestamp, int staleMinutes = 15)
    {
        return (DateTimeOffset.UtcNow - timestamp).TotalMinutes > staleMinutes;
    }

    /// <summary>
    /// Returns true if the age in milliseconds exceeds the staleness threshold.
    /// </summary>
    /// <param name="ageMilliseconds">Age of the reading in milliseconds.</param>
    /// <param name="staleMinutes">Number of minutes after which a reading is considered stale. Defaults to 15.</param>
    public static bool IsStaleMilliseconds(long ageMilliseconds, int staleMinutes = 15)
    {
        return ageMilliseconds > staleMinutes * 60 * 1000L;
    }
}
