namespace Nocturne.Core.Models;

/// <summary>
/// Shared timezone conversion utilities for handling IANA/Windows timezone ID resolution
/// </summary>
public static class TimeZoneHelper
{
    /// <summary>
    /// Resolves a timezone ID (IANA or Windows) to a TimeZoneInfo, with fallback to UTC.
    /// Uses .NET built-in IANA/Windows conversion APIs for comprehensive timezone support.
    /// </summary>
    public static TimeZoneInfo GetTimeZoneInfoFromId(string timezoneId)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timezoneId);
        }
        catch (TimeZoneNotFoundException)
        {
            // On Windows, IANA IDs may not be directly recognized.
            // Use the built-in .NET converter (available since .NET 6 on ICU-enabled runtimes)
            if (TimeZoneInfo.TryConvertIanaIdToWindowsId(timezoneId, out var windowsId))
            {
                try
                {
                    return TimeZoneInfo.FindSystemTimeZoneById(windowsId);
                }
                catch (TimeZoneNotFoundException)
                {
                    // Converted ID also not found, fall through
                }
            }

            // Try the reverse direction in case we're on Linux with a Windows ID
            if (TimeZoneInfo.TryConvertWindowsIdToIanaId(timezoneId, out var ianaId))
            {
                try
                {
                    return TimeZoneInfo.FindSystemTimeZoneById(ianaId);
                }
                catch (TimeZoneNotFoundException)
                {
                    // Converted ID also not found, fall through
                }
            }

            return TimeZoneInfo.Utc;
        }
    }

    /// <summary>
    /// Calculate overnight window boundaries in Unix milliseconds for a given night and timezone
    /// </summary>
    /// <param name="nightOf">The night to analyze (date when sleep started)</param>
    /// <param name="userTimeZone">User's timezone</param>
    /// <param name="bedtimeHour">Hour when bedtime starts (0-23)</param>
    /// <param name="wakeTimeHour">Hour when wake time is (0-23)</param>
    /// <returns>Tuple of (windowStartMills, windowEndMills) in UTC</returns>
    public static (long windowStart, long windowEnd) GetOvernightWindow(
        DateOnly nightOf,
        TimeZoneInfo userTimeZone,
        int bedtimeHour = 23,
        int wakeTimeHour = 7)
    {
        // Night of 2026-02-01 means bedtime on Feb 1 to wake time on Feb 2 in user's local time
        var startLocalDateTime = nightOf.ToDateTime(new TimeOnly(bedtimeHour, 0));
        var endLocalDateTime = nightOf.AddDays(1).ToDateTime(new TimeOnly(wakeTimeHour, 0));

        // Convert local times to UTC for querying
        var startUtc = TimeZoneInfo.ConvertTimeToUtc(startLocalDateTime, userTimeZone);
        var endUtc = TimeZoneInfo.ConvertTimeToUtc(endLocalDateTime, userTimeZone);

        var windowStart = new DateTimeOffset(startUtc, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var windowEnd = new DateTimeOffset(endUtc, TimeSpan.Zero).ToUnixTimeMilliseconds();

        return (windowStart, windowEnd);
    }
}
