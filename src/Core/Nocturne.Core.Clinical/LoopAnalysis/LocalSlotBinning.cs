using System.Globalization;
using Nocturne.Core.Models;

namespace Nocturne.Core.Clinical.LoopAnalysis;

/// <summary>
/// Places instants and half-open intervals on a zone's local wall clock, in N-minute time-of-day
/// slots.
/// </summary>
public static class LocalSlotBinning
{
    public const long MinuteMs = 60_000L;
    public const long HourMs = 60 * MinuteMs;

    /// <summary>
    /// Walks <c>[startMills, endMills)</c> in slices that end on <paramref name="tz"/>'s local
    /// <paramref name="slotMinutes"/>-minute boundaries, handing each slice's local start and length
    /// to <paramref name="addSlice"/>. With hour slots, a 30-minute interval at local 13:55 yields
    /// 5 min in hour 13 and 25 min in hour 14. Slicing on local rather than UTC boundaries is what
    /// keeps a zone with a non-whole-hour offset (Adelaide +9:30, Kathmandu +5:45) from attributing
    /// part of every slice to the neighbouring slot.
    /// </summary>
    /// <remarks>
    /// The boundary is computed with the offset in force at the slice start, so a DST transition
    /// that falls on a slot boundary ends a slice exactly there: a spring-forward day yields no
    /// slice for the skipped slots, and a fall-back day yields two for each repeated one.
    /// </remarks>
    public static void Distribute(
        long startMills,
        long endMills,
        TimeZoneInfo tz,
        int slotMinutes,
        Action<DateTime, long> addSlice) =>
        Distribute(startMills, endMills, tz, slotMinutes, (_, local, sliceMs) => addSlice(local, sliceMs));

    /// <inheritdoc cref="Distribute(long, long, TimeZoneInfo, int, Action{DateTime, long})"/>
    /// <param name="addSlice">Takes the slice's start instant as well as its local start and length.</param>
    public static void Distribute(
        long startMills,
        long endMills,
        TimeZoneInfo tz,
        int slotMinutes,
        Action<long, DateTime, long> addSlice)
    {
        var slotMs = slotMinutes * MinuteMs;
        long cursor = startMills;
        while (cursor < endMills)
        {
            var utcDt = DateTimeOffset.FromUnixTimeMilliseconds(cursor);
            var offsetMs = (long)tz.GetUtcOffset(utcDt).TotalMilliseconds;
            long nextLocalBoundary = ((cursor + offsetMs) / slotMs + 1) * slotMs - offsetMs;
            long sliceEnd = Math.Min(nextLocalBoundary, endMills);
            addSlice(cursor, TimeZoneInfo.ConvertTimeFromUtc(utcDt.UtcDateTime, tz), sliceEnd - cursor);
            cursor = sliceEnd;
        }
    }

    public static DateTime LocalTime(long mills, TimeZoneInfo tz) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTimeOffset.FromUnixTimeMilliseconds(mills).UtcDateTime, tz);

    /// <summary>The local date (yyyy-MM-dd) and time (HH:mm) Day in Review is linked to an instant by.</summary>
    public static (string Date, string Time) DayInReview(long mills, TimeZoneInfo tz)
    {
        var local = LocalTime(mills, tz);
        return (local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), local.ToString("HH:mm", CultureInfo.InvariantCulture));
    }

    public static DateOnly LocalDay(long mills, TimeZoneInfo tz) => DateOnly.FromDateTime(LocalTime(mills, tz));

    /// <summary>
    /// The first and last local day in <paramref name="tz"/> of the instants <paramref name="from"/> and
    /// <paramref name="to"/>, which are read as UTC.
    /// </summary>
    public static (DateOnly First, DateOnly Last) LocalDaySpan(DateTime from, DateTime to, TimeZoneInfo tz) =>
        (LocalDay(from.ToMills(), tz), LocalDay(to.ToMills(), tz));

    /// <summary>
    /// Every local day in <paramref name="tz"/> from the day of <paramref name="from"/> through the day of
    /// <paramref name="to"/>, inclusive, oldest first. Empty when <paramref name="to"/> falls on an earlier day.
    /// </summary>
    public static IReadOnlyList<DateOnly> LocalDays(DateTime from, DateTime to, TimeZoneInfo tz)
    {
        var (first, last) = LocalDaySpan(from, to, tz);
        var days = new List<DateOnly>();
        for (var day = first; day <= last; day = day.AddDays(1))
            days.Add(day);
        return days;
    }

    /// <summary>The UTC day of an instant, for a report with no tenant zone that says so on its response.</summary>
    public static DateOnly UtcDay(long mills) => LocalDay(mills, TimeZoneInfo.Utc);

    /// <summary>The UTC days <paramref name="from"/> to <paramref name="to"/> touch, for a report with no tenant zone that says so on its response.</summary>
    public static IReadOnlyList<DateOnly> UtcDays(DateTime from, DateTime to) => LocalDays(from, to, TimeZoneInfo.Utc);

    /// <summary>
    /// The local days <paramref name="from"/> to <paramref name="to"/> (exclusive) touch, whole: day
    /// <c>d</c> runs from <c>Starts[d]</c> to <c>Starts[d + 1]</c>, so <c>Starts</c> holds one more
    /// entry than there are days. An empty range touches no day.
    /// </summary>
    public static (DateOnly FirstDay, long[] Starts) DayStarts(long from, long to, TimeZoneInfo tz)
    {
        var firstDay = LocalDay(from, tz);
        var dayCount = to > from ? LocalDay(to - 1, tz).DayNumber - firstDay.DayNumber + 1 : 0;
        var starts = new long[dayCount + 1];
        for (var d = 0; d <= dayCount; d++)
            starts[d] = DayStartMills(firstDay.AddDays(d), tz);
        return (firstDay, starts);
    }

    /// <summary>The slot of the day <paramref name="local"/> falls in, counted from local midnight.</summary>
    public static int SlotOf(DateTime local, int slotMinutes) => (local.Hour * 60 + local.Minute) / slotMinutes;

    /// <summary>
    /// The UTC instant local <paramref name="day"/> begins in <paramref name="tz"/>. A zone whose
    /// midnight is skipped by a DST transition begins the day at the first instant after it.
    /// </summary>
    public static long DayStartMills(DateOnly day, TimeZoneInfo tz) => LocalInstantMills(day, 0, tz);

    /// <summary>
    /// The UTC instant local <paramref name="hour"/> o'clock of <paramref name="day"/> falls at in
    /// <paramref name="tz"/>. A skipped local time resolves to the first instant after the gap, an
    /// ambiguous one to its first occurrence.
    /// </summary>
    public static long LocalInstantMills(DateOnly day, int hour, TimeZoneInfo tz)
    {
        return TimeZoneHelper.LocalToUtc(day.ToDateTime(new TimeOnly(hour, 0)), tz).ToMills();
    }
}
