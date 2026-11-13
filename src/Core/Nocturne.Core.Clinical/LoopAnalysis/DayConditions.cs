using Nocturne.Core.Clinical.Statistics;
using Nocturne.Core.Models;
using Nocturne.Core.Models.LoopAnalysis;
using Nocturne.Core.Models.V4;

namespace Nocturne.Core.Clinical.LoopAnalysis;

/// <summary>
/// Decides, for each local day of a range, whether it had what a <see cref="DayCondition"/> names,
/// from the series a report has already read: no read per day.
/// </summary>
/// <remarks>
/// A day the reader cannot see whole, because it starts before the history the reader may read, is
/// undecided: a hidden span, low or carb entry could change the answer, so the day is in neither
/// group. A low is an episode of <see cref="GlucoseEpisodeDetector"/> over the consensus limits, the
/// lows the glucose statistics count; it belongs to every day (or night) it overlaps.
/// </remarks>
public static class DayConditions
{
    /// <param name="dayStarts">Each local day's start, Unix ms, and the end of the last: one more than the days.</param>
    /// <param name="readings">Readings in time order, those under a data exclusion left out.</param>
    /// <param name="covered">The time the data was read over; a day it does not cover whole is undecided.</param>
    public static DaySelection Select(
        DayCondition condition,
        DateOnly firstDay,
        IReadOnlyList<long> dayStarts,
        TimeZoneInfo zone,
        IReadOnlyList<GlucoseSample> readings,
        IEnumerable<CarbEvent> carbs,
        IEnumerable<MillsInterval> exercise,
        IEnumerable<MillsInterval> illness,
        long? historyVisibleFrom,
        MillsInterval covered)
    {
        var days = dayStarts.Count - 1;
        var had = new bool[days];
        switch (condition.Kind)
        {
            case DayConditionKind.Exercise:
                Overlaps(exercise, had, dayStarts, (_, start, end) => (start, end));
                break;
            case DayConditionKind.Illness:
                Overlaps(illness, had, dayStarts, (_, start, end) => (start, end));
                break;
            case DayConditionKind.Low or DayConditionKind.LowOvernight:
                var night = condition.Kind == DayConditionKind.LowOvernight;
                Overlaps(Lows(readings), had, dayStarts, (d, start, end) =>
                    night ? (start, Math.Min(end, NightEnd(firstDay.AddDays(d), zone))) : (start, end));
                break;
            case DayConditionKind.Carbs:
                var totals = new double[days];
                foreach (var carb in carbs.Where(c => c.Grams > 0))
                {
                    var d = DayOf(carb.Mills, dayStarts);
                    if (d >= 0)
                        totals[d] += carb.Grams;
                }
                for (var d = 0; d < days; d++)
                    had[d] = !(totals[d] < condition.MinCarbs) && !(totals[d] > condition.MaxCarbs);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(condition), condition.Kind, null);
        }

        var kept = new List<DateOnly>();
        int matching = 0, notMatching = 0, undecided = 0, partial = 0;
        for (var d = 0; d < days; d++)
        {
            if (historyVisibleFrom is { } edge && dayStarts[d] < edge)
            {
                undecided++;
                continue;
            }
            if (dayStarts[d] < covered.Start || dayStarts[d + 1] > covered.End)
            {
                partial++;
                continue;
            }
            if (had[d]) matching++; else notMatching++;
            if (had[d] != condition.Others)
                kept.Add(firstDay.AddDays(d));
        }
        return new DaySelection(kept, matching, notMatching, undecided, partial);
    }

    /// <summary>The kept days of <paramref name="selection"/> as instants.</summary>
    public static IntervalSet Instants(DaySelection selection, DateOnly firstDay, IReadOnlyList<long> dayStarts) =>
        IntervalSet.Of(selection.Kept.Select(day => day.DayNumber - firstDay.DayNumber)
            .Where(d => d >= 0 && d < dayStarts.Count - 1)
            .Select(d => new MillsInterval(dayStarts[d], dayStarts[d + 1])));

    /// <summary>The low episodes in <paramref name="readings"/>, as spans.</summary>
    internal static List<MillsInterval> Lows(IReadOnlyList<GlucoseSample> readings)
    {
        var plausible = readings
            .Where(r => GlucoseStatistics.IsPlausibleReading(r.Mgdl))
            .Select(r => new SensorGlucose { Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(r.Mills).UtcDateTime, Mgdl = r.Mgdl })
            .ToList();
        return
        [
            .. GlucoseEpisodeDetector.Detect(plausible, new GlycemicThresholds())
                .Where(e => e.BelowRange)
                .Select(e => new MillsInterval(
                    new DateTimeOffset(e.StartAt, TimeSpan.Zero).ToUnixTimeMilliseconds(),
                    new DateTimeOffset(e.EndAt, TimeSpan.Zero).ToUnixTimeMilliseconds())),
        ];
    }

    private static long NightEnd(DateOnly day, TimeZoneInfo zone)
    {
        var local = day.ToDateTime(new TimeOnly(0, 0)).AddMinutes(DayCondition.NightEndMinute);
        return new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUnixTimeMilliseconds();
    }

    /// <summary>Marks each day whose window (by <paramref name="window"/>) a span overlaps.</summary>
    private static void Overlaps(
        IEnumerable<MillsInterval> spans, bool[] had, IReadOnlyList<long> dayStarts, Func<int, long, long, (long Start, long End)> window)
    {
        var list = spans.ToList();
        for (var d = 0; d < had.Length; d++)
        {
            var (start, end) = window(d, dayStarts[d], dayStarts[d + 1]);
            had[d] = list.Any(s => s.Start < end && s.End > start);
        }
    }

    private static int DayOf(long mills, IReadOnlyList<long> dayStarts)
    {
        for (var d = 0; d < dayStarts.Count - 1; d++)
            if (mills >= dayStarts[d] && mills < dayStarts[d + 1])
                return d;
        return -1;
    }
}
