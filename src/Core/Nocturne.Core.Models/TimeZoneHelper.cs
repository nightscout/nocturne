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

        // Case-insensitive match against the system zone table. IANA IDs are technically
        // case-sensitive — and Linux's zoneinfo lookup is too — but connector data frequently
        // carries mis-cased IDs. Resolving them preserves the intended offset.
        foreach (var zone in TimeZoneInfo.GetSystemTimeZones())
        {
            if (string.Equals(zone.Id, timezoneId, StringComparison.OrdinalIgnoreCase))
            {
                timeZone = zone;
                return true;
            }
        }

        // Last resort: canonicalize the casing of the fixed-offset Etc/GMT±N family and retry the
        // direct lookup. These zones are loadable by exact ID but are excluded from
        // GetSystemTimeZones() enumeration on some runtimes (so the scan above can't catch a
        // mis-cased one) — which is exactly the prod data: ~240 rows carry "ETC/GMT-2" etc.
        if (TryNormalizeEtcId(timezoneId, out var canonicalId)
            && TryFindSystemTimeZone(canonicalId, out timeZone))
            return true;

        timeZone = TimeZoneInfo.Utc;
        return false;
    }

    /// <summary>
    /// The IANA id of the zone a Windows id (e.g. <c>AUS Eastern Standard Time</c>) names, or
    /// <paramref name="timezoneId"/> unchanged when it names no Windows zone. Consumers that read
    /// the IANA database directly, such as the Rust alert engine, cannot resolve a Windows id.
    /// </summary>
    public static string ToIanaIdIfWindows(string timezoneId)
    {
        if (TimeZoneInfo.TryConvertWindowsIdToIanaId(timezoneId, out var ianaId))
            return ianaId;

        // A mis-cased Windows id resolves through the lookup's case-insensitive retry.
        return TryGetTimeZoneInfoFromId(timezoneId, out var zone)
            && !zone.HasIanaId
            && TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out ianaId)
                ? ianaId
                : timezoneId;
    }

    /// <summary>
    /// The IANA id of the zone a timezone id names, in whatever form it arrived (IANA, mis-cased
    /// <c>Etc/*</c>, or Windows), or null when it names no zone.
    /// </summary>
    public static string? ToIanaIdOrNull(string? timezoneId)
    {
        if (!TryGetTimeZoneInfoFromId(timezoneId?.Trim(), out var zone))
            return null;

        if (zone.HasIanaId)
            return zone.Id;

        return TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out var ianaId) ? ianaId : null;
    }

    /// <summary>
    /// Canonicalizes the casing of an <c>Etc/*</c> timezone ID (e.g. <c>ETC/GMT-2</c> → <c>Etc/GMT-2</c>).
    /// Etc zone names after the prefix are all-uppercase tokens (<c>GMT</c>, <c>GMT-2</c>, <c>UTC</c>,
    /// <c>UCT</c>, <c>GMT0</c>), so uppercasing the remainder yields the canonical IANA form for the
    /// offset family. Returns <see langword="false"/> for non-Etc IDs; a wrongly-cased result simply
    /// fails the subsequent lookup and degrades to UTC, so this never mis-resolves.
    /// </summary>
    private static bool TryNormalizeEtcId(string id, out string normalized)
    {
        normalized = id;
        var slash = id.IndexOf('/');
        if (slash <= 0 || !id.AsSpan(0, slash).Equals("Etc", StringComparison.OrdinalIgnoreCase))
            return false;

        normalized = string.Concat("Etc/", id.AsSpan(slash + 1).ToString().ToUpperInvariant());
        return true;
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
    /// Hours from <paramref name="bedtimeHour"/> to the next <paramref name="wakeTimeHour"/>, 1 to 24.
    /// Equal hours span a full day.
    /// </summary>
    public static int SleepSpanHours(int bedtimeHour, int wakeTimeHour)
    {
        var hours = ((wakeTimeHour - bedtimeHour) % 24 + 24) % 24;
        return hours == 0 ? 24 : hours;
    }

    /// <summary>
    /// Calculate overnight window boundaries in Unix milliseconds for a given night and timezone.
    /// The window runs from bedtime on <paramref name="nightOf"/> to the next wake time after it:
    /// the same day when the wake hour is later than the bedtime hour, otherwise the next day.
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
        var wakeDay = wakeTimeHour > bedtimeHour ? nightOf : nightOf.AddDays(1);
        var startLocalDateTime = nightOf.ToDateTime(new TimeOnly(bedtimeHour, 0));
        var endLocalDateTime = wakeDay.ToDateTime(new TimeOnly(wakeTimeHour, 0));

        var startUtc = LocalToUtc(startLocalDateTime, userTimeZone);
        var endUtc = LocalToUtc(endLocalDateTime, userTimeZone);

        return (startUtc.ToMills(), endUtc.ToMills());
    }

    /// <summary>
    /// The UTC instant a wall-clock time falls at in <paramref name="timeZone"/>. Only the clock reading
    /// of <paramref name="local"/> is used, whatever its <see cref="DateTimeKind"/>. A time the clock
    /// skipped resolves to the first instant after the gap, exact to the tick. An ambiguous time resolves
    /// to its first occurrence, the one at the larger offset.
    /// </summary>
    /// <returns>The instant, with <see cref="DateTimeKind.Utc"/>.</returns>
    public static DateTime LocalToUtc(DateTime local, TimeZoneInfo timeZone)
    {
        var wall = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);

        if (timeZone.IsInvalidTime(wall))
            return DateTime.SpecifyKind(GapEnd(wall, timeZone), DateTimeKind.Utc);

        var offset = timeZone.IsAmbiguousTime(wall)
            ? timeZone.GetAmbiguousTimeOffsets(wall).Max()
            : timeZone.GetUtcOffset(wall);

        return DateTime.SpecifyKind(wall - offset, DateTimeKind.Utc);
    }

    private static DateTime GapEnd(DateTime wall, TimeZoneInfo timeZone)
    {
        var before = timeZone.GetUtcOffset(wall.AddDays(-1));
        var after = timeZone.GetUtcOffset(wall.AddDays(1));

        var low = (wall - after).Ticks;
        var high = (wall - before).Ticks;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            var offset = timeZone.GetUtcOffset(new DateTimeOffset(middle, TimeSpan.Zero));
            if (offset == before)
                low = middle + 1;
            else
                high = middle;
        }

        return new DateTime(low, DateTimeKind.Utc);
    }
}
