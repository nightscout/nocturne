using Nocturne.Core.Models.V4;

namespace Nocturne.Core.Clinical.Schedules;

/// <summary>Resolves the active value of a time-of-day schedule by <see cref="ScheduleEntryTime.TryEntryAt{T}"/>.</summary>
public static class ScheduleResolution
{
    /// <summary>
    /// Finds the active value at a given time of day from a schedule of single-value entries.
    /// </summary>
    /// <returns>The active value, or null if the list is empty.</returns>
    public static double? FindValueAtTime(List<ScheduleEntry> entries, int secondsFromMidnight)
    {
        return ScheduleEntryTime.TryEntryAt(entries, secondsFromMidnight, SecondsOf, out var entry)
            ? entry.Value
            : null;
    }

    /// <summary>
    /// Finds the active target range at a given time of day from a schedule of range entries.
    /// </summary>
    /// <returns>The active (Low, High) range, or null if the list is empty.</returns>
    public static (double Low, double High)? FindRangeAtTime(
        List<TargetRangeEntry> entries,
        int secondsFromMidnight
    )
    {
        return ScheduleEntryTime.TryEntryAt(entries, secondsFromMidnight, SecondsOf, out var entry)
            ? (entry.Low, entry.High)
            : null;
    }

    private static int SecondsOf(ScheduleEntry entry) => ScheduleEntryTime.SecondsOf(entry.TimeAsSeconds, entry.Time);

    private static int SecondsOf(TargetRangeEntry entry) => ScheduleEntryTime.SecondsOf(entry.TimeAsSeconds, entry.Time);
}
