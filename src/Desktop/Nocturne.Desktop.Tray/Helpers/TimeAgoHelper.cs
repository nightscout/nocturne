namespace Nocturne.Desktop.Tray.Helpers;

/// <summary>
/// Formats timestamps as relative "time ago" strings.
/// </summary>
public static class TimeAgoHelper
{
    public static string Format(DateTimeOffset timestamp)
    {
        var age = DateTimeOffset.UtcNow - timestamp;
        return Format(age);
    }

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
    /// Returns true if the reading is considered stale (older than 15 minutes).
    /// </summary>
    public static bool IsStale(DateTimeOffset timestamp, int staleMinutes = 15)
    {
        return (DateTimeOffset.UtcNow - timestamp).TotalMinutes > staleMinutes;
    }
}
