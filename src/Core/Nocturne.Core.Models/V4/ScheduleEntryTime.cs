using System.Globalization;

namespace Nocturne.Core.Models.V4;

/// <summary>When in the day a <see cref="ScheduleEntry"/> or <see cref="TargetRangeEntry"/> starts.</summary>
public static class ScheduleEntryTime
{
    private static readonly string[] Formats = ["H:mm", "H:mm:ss"];

    /// <summary>Seconds from midnight: <paramref name="timeAsSeconds"/>, else <paramref name="time"/> parsed, else 0.</summary>
    public static int SecondsOf(int? timeAsSeconds, string time) =>
        timeAsSeconds ?? (TryParseSeconds(time, out var parsed) ? parsed : 0);

    /// <summary>
    /// Parses a time of day written <c>H:mm</c> or <c>H:mm:ss</c>, in the invariant culture. A bare
    /// number such as "6" is not a time of day.
    /// </summary>
    public static bool TryParseSeconds(string? time, out int seconds)
    {
        if (TimeOnly.TryParseExact(time, Formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            seconds = (int)parsed.ToTimeSpan().TotalSeconds;
            return true;
        }

        seconds = 0;
        return false;
    }

    /// <summary>Seconds since local midnight at <paramref name="mills"/> in <paramref name="zone"/>; a null zone reads as UTC.</summary>
    public static int SecondsOfDay(long mills, TimeZoneInfo? zone)
    {
        var instant = DateTimeOffset.FromUnixTimeMilliseconds(mills);
        if (zone is not null)
            instant = TimeZoneInfo.ConvertTime(instant, zone);
        return (int)instant.TimeOfDay.TotalSeconds;
    }

    /// <summary>
    /// The entry in force at <paramref name="secondsFromMidnight"/>: the last entry starting at or before it,
    /// or the earliest entry when it precedes them all. Entries need no order. Of entries that start
    /// together, the last one listed wins when they are in force and the first one listed wins when they
    /// are the earliest.
    /// </summary>
    /// <returns>False when <paramref name="entries"/> is empty.</returns>
    public static bool TryEntryAt<T>(
        IEnumerable<T> entries, int secondsFromMidnight, Func<T, int> secondsOf, out T entry)
    {
        var earliest = default(T);
        var earliestSeconds = 0;
        var inForce = default(T);
        var inForceSeconds = 0;
        var any = false;
        var anyInForce = false;

        foreach (var candidate in entries)
        {
            var seconds = secondsOf(candidate);
            if (!any || seconds < earliestSeconds)
            {
                earliest = candidate;
                earliestSeconds = seconds;
            }

            if (seconds <= secondsFromMidnight && (!anyInForce || seconds >= inForceSeconds))
            {
                inForce = candidate;
                inForceSeconds = seconds;
                anyInForce = true;
            }

            any = true;
        }

        entry = (anyInForce ? inForce : earliest)!;
        return any;
    }
}
