using Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.Profiles.Resolvers;

/// <summary>
/// Resolves the active value from a time-of-day schedule at a given seconds-from-midnight.
/// Finds the most recent entry at-or-before the query time, defaulting to the first entry
/// when the query time precedes all entries.
/// </summary>
internal static class ScheduleResolution
{
    /// <summary>
    /// Finds the active value at a given time of day from a schedule of single-value entries.
    /// </summary>
    /// <returns>The active value, or null if the list is empty.</returns>
    public static double? FindValueAtTime(List<ScheduleEntry> entries, int secondsFromMidnight)
    {
        if (entries.Count == 0)
            return null;

        var sorted = entries.OrderBy(e => e.TimeAsSeconds ?? 0).ToList();
        var result = sorted[0].Value;

        foreach (var entry in sorted)
        {
            if (secondsFromMidnight >= (entry.TimeAsSeconds ?? 0))
                result = entry.Value;
            else
                break;
        }

        return result;
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
        if (entries.Count == 0)
            return null;

        var sorted = entries.OrderBy(e => e.TimeAsSeconds ?? 0).ToList();
        var result = (sorted[0].Low, sorted[0].High);

        foreach (var entry in sorted)
        {
            if (secondsFromMidnight >= (entry.TimeAsSeconds ?? 0))
                result = (entry.Low, entry.High);
            else
                break;
        }

        return result;
    }
}
