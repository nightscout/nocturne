using SharedTimeAgoHelper = Nocturne.Widget.Contracts.Helpers.TimeAgoHelper;

namespace Nocturne.Desktop.Tray.Helpers;

/// <summary>
/// Formats timestamps as relative "time ago" strings.
/// Delegates to the shared <see cref="SharedTimeAgoHelper"/> implementation.
/// </summary>
public static class TimeAgoHelper
{
    public static string Format(DateTimeOffset timestamp) =>
        SharedTimeAgoHelper.Format(timestamp);

    public static string Format(TimeSpan age) =>
        SharedTimeAgoHelper.Format(age);

    /// <summary>
    /// Returns true if the reading is considered stale (older than the specified threshold).
    /// </summary>
    public static bool IsStale(DateTimeOffset timestamp, int staleMinutes = 15) =>
        SharedTimeAgoHelper.IsStale(timestamp, staleMinutes);
}
