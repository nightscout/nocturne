using Nocturne.Core.Clinical.LoopAnalysis;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Timezones;

namespace Nocturne.Core.Clinical.Devices;

/// <summary>
/// Places a registered device's dates, which are the wearer's local calendar dates. The zone at an
/// instant is the timezone timeline's entry in effect then, else the tenant's profile zone, else UTC.
/// </summary>
/// <remarks>
/// The profile zone stands in where the timeline is silent because only some connectors seed one: a
/// tenant uploading from AAPS, Loop or Trio usually has none. An entry is looked up by local wall
/// clock, so <see cref="LocalDate"/> and <see cref="Window"/> share one set of day boundaries.
/// </remarks>
public sealed class DeviceDateClock(
    TimezoneTimeline timeline, TimeZoneInfo? profileZone, TimeZoneUnavailableReason? profileZoneReason)
{
    /// <summary>Why device dates read as UTC days: null when the timeline or the profile gives a zone.</summary>
    public TimeZoneUnavailableReason? Reason => timeline.HasEntries ? null : profileZoneReason;

    public DateOnly LocalDate(DateTime utc)
    {
        var instant = new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
        var day = DateOnly.FromDateTime(
            TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), ZoneAt(utc)));
        while (instant < DayStartMills(day))
            day = day.AddDays(-1);
        while (instant >= DayStartMills(day.AddDays(1)))
            day = day.AddDays(1);
        return day;
    }

    /// <summary>
    /// A device's window: from the start of its first day to the end of its last, open-ended on either
    /// side a date does not bound.
    /// </summary>
    public MillsInterval Window(DateOnly? start, DateOnly? end, long openEnd) => new(
        start is { } first ? DayStartMills(first) : long.MinValue,
        end is { } last ? DayStartMills(last.AddDays(1)) : openEnd);

    private long DayStartMills(DateOnly day) =>
        LocalSlotBinning.DayStartMills(day, ZoneAt(day.ToDateTime(TimeOnly.MinValue)));

    private TimeZoneInfo ZoneAt(DateTime wallClock) =>
        TimeZoneHelper.TryGetTimeZoneInfoFromId(timeline.ZoneAt(wallClock), out var zone)
            ? zone
            : profileZone ?? TimeZoneInfo.Utc;
}
