using Nocturne.Core.Models;
using Nocturne.Core.Models.LoopAnalysis;

namespace Nocturne.Core.Clinical.LoopAnalysis;

/// <summary>
/// Resolves Loop Analysis ranges of tenant-local calendar dates to the instants they span. A day is
/// a local day, so a DST day stays whole: 23 or 25 hours. No range runs past now: time that has not
/// happened is not analysed, so the current day is a partial day.
/// </summary>
public static class LoopAnalysisRanges
{
    /// <summary>
    /// <paramref name="startDate"/> to <paramref name="endDate"/> inclusive, ending at
    /// <paramref name="nowUtc"/> when that is sooner, and keeping only the most recent
    /// <paramref name="maxDays"/> local days when it spans more. Dates after today are read as
    /// today: the viewer's today can be ahead of the tenant's.
    /// </summary>
    public static LoopAnalysisResolvedRange Resolve(
        DateOnly startDate, DateOnly endDate, TimeZoneInfo tz, int maxDays, DateTime nowUtc)
    {
        var nowMills = nowUtc.ToMills();
        var today = LocalSlotBinning.LocalDay(nowMills - 1, tz);
        var firstDate = startDate < today ? startDate : today;
        var lastDate = endDate < today ? endDate : today;
        var shortened = lastDate.DayNumber - firstDate.DayNumber + 1 > maxDays;
        var range = Of(shortened ? lastDate.AddDays(1 - maxDays) : firstDate, lastDate, tz, shortened);
        if (range.To > nowUtc)
            range.To = nowUtc;
        return range;
    }

    /// <summary>
    /// The same number of days as <paramref name="primary"/>, ending the day before it starts. When
    /// <paramref name="primary"/>'s last day is partial, so is this range's, cut the same time into it.
    /// </summary>
    public static LoopAnalysisResolvedRange Previous(LoopAnalysisResolvedRange primary, TimeZoneInfo tz)
    {
        var days = primary.EndDate.DayNumber - primary.StartDate.DayNumber + 1;
        var previous = Of(primary.StartDate.AddDays(-days), primary.StartDate.AddDays(-1), tz, shortened: false);
        var primaryLastDay = LocalSlotBinning.DayStartMills(primary.EndDate, tz);
        var primaryTo = primary.To.ToMills();
        if (primaryTo < LocalSlotBinning.DayStartMills(primary.EndDate.AddDays(1), tz))
        {
            var partialTo = LocalSlotBinning.DayStartMills(previous.EndDate, tz) + (primaryTo - primaryLastDay);
            if (partialTo < previous.To.ToMills())
                previous.To = DateTimeOffset.FromUnixTimeMilliseconds(partialTo).UtcDateTime;
        }
        return previous;
    }

    /// <summary>The last <paramref name="window"/> before <paramref name="nowUtc"/>, for a history-clamped caller.</summary>
    public static LoopAnalysisResolvedRange Clamped(DateTime nowUtc, TimeSpan window, TimeZoneInfo tz)
    {
        var from = nowUtc - window;
        var toMills = nowUtc.ToMills();
        return new LoopAnalysisResolvedRange
        {
            StartDate = LocalSlotBinning.LocalDay(from.ToMills(), tz),
            EndDate = LocalSlotBinning.LocalDay(toMills - 1, tz),
            From = from,
            To = nowUtc,
        };
    }

    private static LoopAnalysisResolvedRange Of(DateOnly startDate, DateOnly endDate, TimeZoneInfo tz, bool shortened) => new()
    {
        StartDate = startDate,
        EndDate = endDate,
        From = DateTimeOffset.FromUnixTimeMilliseconds(LocalSlotBinning.DayStartMills(startDate, tz)).UtcDateTime,
        To = DateTimeOffset.FromUnixTimeMilliseconds(LocalSlotBinning.DayStartMills(endDate.AddDays(1), tz)).UtcDateTime,
        Shortened = shortened,
    };
}
