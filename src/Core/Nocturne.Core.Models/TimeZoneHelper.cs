namespace Nocturne.Core.Models;

/// <summary>
/// Shared timezone conversion utilities for handling IANA/Windows timezone ID resolution
/// </summary>
public static class TimeZoneHelper
{
    /// <summary>
    /// Resolves a timezone ID (IANA or Windows) to a <see cref="TimeZoneInfo"/>, with fallback to UTC.
    /// Uses .NET built-in IANA/Windows conversion APIs for comprehensive timezone support.
    /// On Windows, IANA IDs are converted to Windows IDs via <c>TryConvertIanaIdToWindowsId</c>;
    /// on Linux the reverse conversion is also attempted. As a last resort the ID is matched
    /// case-insensitively against the system zone table, so a mis-cased IANA ID (e.g. the
    /// <c>ETC/GMT-2</c> some connectors emit instead of <c>Etc/GMT-2</c>) still resolves to its
    /// intended offset rather than silently falling back to UTC.
    /// </summary>
    /// <param name="timezoneId">IANA timezone ID (e.g., "America/New_York") or Windows timezone ID (e.g., "Eastern Standard Time")</param>
    /// <returns>The resolved <see cref="TimeZoneInfo"/>, or <see cref="TimeZoneInfo.Utc"/> if the ID cannot be resolved</returns>
    public static TimeZoneInfo GetTimeZoneInfoFromId(string? timezoneId)
        => TryGetTimeZoneInfoFromId(timezoneId, out var tz) ? tz : TimeZoneInfo.Utc;

    /// <summary>
    /// Resolves a timezone ID (IANA or Windows) to a <see cref="TimeZoneInfo"/>, reporting whether
    /// resolution succeeded instead of falling back to UTC. Use this over
    /// <see cref="GetTimeZoneInfoFromId(string)"/> when callers must distinguish "no/invalid zone"
    /// from "UTC" (e.g. to skip a conversion or reject a malformed rule). Resolution order is exact
    /// lookup, IANA↔Windows conversion, then a case-insensitive match against the system zone table
    /// so mis-cased IANA IDs (e.g. <c>ETC/GMT-2</c> for <c>Etc/GMT-2</c>) still resolve.
    /// </summary>
    /// <param name="timezoneId">The IANA or Windows timezone ID; null/empty resolves to <see langword="false"/>.</param>
    /// <param name="timeZone">The resolved zone, or <see cref="TimeZoneInfo.Utc"/> when this returns <see langword="false"/>.</param>
    /// <returns><see langword="true"/> if the ID resolved to a real zone; otherwise <see langword="false"/>.</returns>
    public static bool TryGetTimeZoneInfoFromId(string? timezoneId, out TimeZoneInfo timeZone)
    {
        timeZone = TimeZoneInfo.Utc;
        if (string.IsNullOrEmpty(timezoneId))
            return false;

        if (TryFindSystemTimeZone(timezoneId, out timeZone))
            return true;

        // On Windows, IANA IDs may not be directly recognized; on Linux a Windows ID isn't.
        // Use the built-in .NET converters (available since .NET 6 on ICU-enabled runtimes).
        if (TimeZoneInfo.TryConvertIanaIdToWindowsId(timezoneId, out var windowsId)
            && TryFindSystemTimeZone(windowsId, out timeZone))
            return true;

        if (TimeZoneInfo.TryConvertWindowsIdToIanaId(timezoneId, out var ianaId)
            && TryFindSystemTimeZone(ianaId, out timeZone))
            return true;

        // Last resort: case-insensitive match against the system zone table. IANA IDs are
        // technically case-sensitive — and Linux's zoneinfo lookup is too — but connector data
        // frequently carries mis-cased IDs. Resolving them preserves the intended offset.
        foreach (var zone in TimeZoneInfo.GetSystemTimeZones())
        {
            if (string.Equals(zone.Id, timezoneId, StringComparison.OrdinalIgnoreCase))
            {
                timeZone = zone;
                return true;
            }
        }

        timeZone = TimeZoneInfo.Utc;
        return false;
    }

    /// <summary>
    /// Attempts an exact <see cref="TimeZoneInfo.FindSystemTimeZoneById(string)"/> lookup, returning
    /// <see langword="false"/> instead of throwing when the ID is unknown or the zone data is invalid.
    /// </summary>
    private static bool TryFindSystemTimeZone(string id, out TimeZoneInfo timeZone)
    {
        try
        {
            timeZone = TimeZoneInfo.FindSystemTimeZoneById(id);
            return true;
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            timeZone = TimeZoneInfo.Utc;
            return false;
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
