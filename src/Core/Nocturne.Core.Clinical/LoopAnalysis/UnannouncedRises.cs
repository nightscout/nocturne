using Nocturne.Core.Clinical.Statistics;
using Nocturne.Core.Models.LoopAnalysis;

namespace Nocturne.Core.Clinical.LoopAnalysis;

/// <summary>What a search for unannounced rises found.</summary>
/// <param name="Anchors">The rises that qualify as <see cref="EventAnchorKind.UnannouncedRise"/>, in time order.</param>
/// <param name="Climbs">
/// The start of every meal-like climb that is an intake of its own, whether it qualified or not: one
/// no carb entry announced, answered by the loop or not, and one a carb entry announced but did not
/// cause (<see cref="UnannouncedRises.CarbAttributionMs"/>). A climb a carb entry caused is that
/// meal's, which the entry already stands for, and a climb merged into an earlier one is part of it
/// (<see cref="UnannouncedRises.MergeMs"/>); both are left out. An episode with any of these in its
/// window is not isolated.
/// </param>
public sealed record RiseSearch(List<EventAnchor> Anchors, List<long> Climbs);

/// <summary>
/// Finds <see cref="EventAnchorKind.UnannouncedRise"/> anchors: the start of a meal-like glucose
/// rise that no carb entry announced, for a loop run fully closed.
/// </summary>
/// <remarks>
/// <para>
/// A rise starts from a floor: the <see cref="FloorQuantile"/> of a stable stretch of readings
/// (<see cref="StableMs"/> long, spanning at most <see cref="StableRangeMgdl"/>), at least
/// <see cref="MinimumFloorMgdl"/>. The floor, not any one reading, is what a rise is measured from,
/// so a single noisy reading neither sets the anchor nor hides the meal. The anchor is the last
/// reading within <see cref="FloorToleranceMgdl"/> of the floor before glucose climbs
/// <see cref="RiseMgdl"/> above it within <see cref="RiseWindowMs"/>, without falling back to the
/// floor on the way.
/// </para>
/// <para>
/// A climb is announced, and so not a rise, by a carb entry from <see cref="CarbsBeforeMs"/> before
/// its anchor to <see cref="CarbsAfterMs"/> after it, or by carbs on board at the anchor
/// where those are known; the entry caused it only when the climb starts within
/// <see cref="CarbAttributionMs"/> of it. A climb within <see cref="MergeMs"/> of an earlier one is
/// part of it.
/// </para>
/// <para>
/// Readings under a <c>DataExclusion</c> span are dropped before the search, and a stretch or climb
/// is not read across a gap of more than <see cref="MaxGapMs"/>. A climb faster than
/// <see cref="MaxRiseRateMgdlPerMinute"/> over <see cref="RateSpanMs"/>, or with one reading
/// <see cref="StepMgdl"/> above the one before (<see cref="StepMs"/>), is not physiological. A
/// compression low is told by its shape, a sudden drop from a level the climb then returns to
/// (<see cref="IsCompressionRecovery"/>), so one that is not excluded still qualifies only where
/// glucose came down to it gradually.
/// </para>
/// </remarks>
public static class UnannouncedRises
{
    /// <summary>
    /// How far above the floor glucose must climb. A meal without insulin raises glucose by well
    /// over 50 mg/dL; 40 keeps a meal the loop partly caught early while staying above the 20 to 30
    /// mg/dL a CGM's noise and an ordinary drift reach in the same time.
    /// </summary>
    public const double RiseMgdl = 40;

    /// <summary>
    /// How quickly, from the anchor: <see cref="RiseMgdl"/> above the floor in 45 minutes. The
    /// anchor may already sit <see cref="FloorToleranceMgdl"/> above the floor, so the slowest climb
    /// that qualifies is 35 mg/dL in 45 minutes, about 0.78 mg/dL a minute: below the 1 to 3 a
    /// meal's carbs drive. The dawn phenomenon usually climbs at 0.2 to 0.5 and takes hours to rise
    /// as far; a steep one that passes this is what corroboration
    /// (<see cref="CorroborationBasalHours"/>) tells from a meal.
    /// </summary>
    public const long RiseWindowMs = 45 * LocalSlotBinning.MinuteMs;

    /// <summary>
    /// How long glucose must have been stable before the rise: half an hour, so a rise is told from
    /// the continuation of one already under way, or the rebound from a fall.
    /// </summary>
    public const long StableMs = 30 * LocalSlotBinning.MinuteMs;

    /// <summary>
    /// The most the readings of the stable stretch may span. Two CGM readings five minutes apart
    /// differ by up to 10 mg/dL from noise alone, so 20 tolerates that while rejecting a trend.
    /// </summary>
    public const double StableRangeMgdl = 20;

    /// <summary>
    /// The quantile of the stable stretch taken as its floor: low enough to sit at the level glucose
    /// rested at, high enough that one reading a few mg/dL low does not set it.
    /// </summary>
    public const double FloorQuantile = 25;

    /// <summary>
    /// How near the floor a reading is still at it: half the noise <see cref="StableRangeMgdl"/>
    /// allows between neighbouring readings.
    /// </summary>
    public const double FloorToleranceMgdl = 5;

    /// <summary>
    /// The lowest floor a rise is read from. Below 70 mg/dL a climb is far likelier the recovery from
    /// a hypo, treated or rebounding, than a meal, and a hypo treatment is often not logged as carbs.
    /// </summary>
    public const double MinimumFloorMgdl = 70;

    /// <summary>
    /// The longest gap between readings the search reads across: one missed five-minute reading.
    /// Longer, and what glucose did in between is unknown.
    /// </summary>
    public const long MaxGapMs = 10 * LocalSlotBinning.MinuteMs;

    /// <summary>
    /// The fastest climb read as glucose, over <see cref="RateSpanMs"/>. Carbohydrate absorption
    /// peaks at about 3 to 4 mg/dL a minute; faster is a sensor artefact, typically a compression
    /// low ending.
    /// </summary>
    public const double MaxRiseRateMgdlPerMinute = 5;

    /// <summary>
    /// The span the climb's rate is measured over. Between neighbouring readings a CGM's noise alone
    /// passes <see cref="MaxRiseRateMgdlPerMinute"/>: ±4 mg/dL a minute apart, or one reading 16 high
    /// five minutes on. Over a quarter of an hour that noise is diluted below it while a sensor's
    /// jump, which holds, is not.
    /// </summary>
    public const long RateSpanMs = 15 * LocalSlotBinning.MinuteMs;

    /// <summary>
    /// How long before a climb a carb entry still announces it. A mixed meal absorbs over two to
    /// four hours, and its glucose often waits on the bolus, or climbs in two steps; a meal logged up
    /// to three hours earlier is the likelier cause of a climb than a second, unlogged one.
    /// </summary>
    public const long CarbsBeforeMs = 3 * LocalSlotBinning.HourMs;

    /// <summary>
    /// How long after a climb starts a carb entry still announces it: one is often logged once the
    /// plate is cleared.
    /// </summary>
    public const long CarbsAfterMs = 30 * LocalSlotBinning.MinuteMs;

    /// <summary>
    /// How long after a carb entry a climb may start and still be that meal's own. A meal's glucose
    /// leaves its floor within the hour, however slowly it absorbs; a climb that starts later is a
    /// further intake, a snack or a second course, even while the meal's carbs are still on board, so
    /// the meal's insulin no longer belongs to it alone.
    /// </summary>
    public const long CarbAttributionMs = LocalSlotBinning.HourMs;

    /// <summary>
    /// The most glucose may climb between two readings at most <see cref="StepMs"/> apart. Carbs at
    /// their fastest, about 4.5 mg/dL a minute, add 22 in five minutes, and a CGM's noise a further 10
    /// or so; 30 is a sensor stepping back from a false low.
    /// </summary>
    public const double StepMgdl = 30;

    /// <summary>The longest interval <see cref="StepMgdl"/> is read over: one five-minute reading.</summary>
    public const long StepMs = 5 * LocalSlotBinning.MinuteMs;

    /// <summary>
    /// How far above the floor the level before a sudden drop must be for the floor to read as a
    /// compression low rather than glucose: well past the <see cref="StableRangeMgdl"/> a resting
    /// level moves within, and as far as a pressed sensor commonly reads low.
    /// </summary>
    public const double CompressionDropMgdl = 30;

    /// <summary>
    /// The fastest glucose falls in fact, mg/dL a minute: a correction or exercise brings it down at
    /// 1 to 2. A sensor pressed on reads low within one or two readings, far faster.
    /// </summary>
    public const double CompressionDropRateMgdlPerMinute = 3;

    /// <summary>
    /// How long before the floor's stretch began the level it dropped from is looked for: three
    /// readings, enough to see a drop over one or two.
    /// </summary>
    public const long CompressionLookbackMs = 15 * LocalSlotBinning.MinuteMs;

    /// <summary>
    /// How near the level it dropped from a climb must come back to read as the sensor recovering
    /// rather than a meal: within the <see cref="StableRangeMgdl"/> glucose rests within.
    /// </summary>
    public const double CompressionReturnMgdl = StableRangeMgdl;

    /// <summary>
    /// How long after one climb's anchor another is taken to be the same intake: a meal that stalls
    /// and climbs again within two hours is one meal, not two.
    /// </summary>
    public const long MergeMs = 2 * LocalSlotBinning.HourMs;

    /// <summary>
    /// How long after the anchor the loop's answer is read for corroboration: AID systems act on
    /// readings five minutes apart and their correction builds over half an hour or more, so 45
    /// minutes, the same as <see cref="RiseWindowMs"/>, covers their response latency.
    /// </summary>
    public const long CorroborationMs = RiseWindowMs;

    /// <summary>
    /// The net loop correction that corroborates a rise, in hours of the user's mean scheduled basal
    /// rate over the day before the anchor: half an hour's schedule delivered above it within
    /// <see cref="CorroborationMs"/>. Scaled to the schedule so a low-dose user corroborates as easily
    /// as a high-dose one, averaged over a whole day so the bar does not swing with the hour a rise
    /// starts in (a zero-basal hour would otherwise corroborate anything), and read off net loop
    /// correction (algorithm temp delta plus automatic boluses) so a loop that corrects with temp
    /// basals alone does as well as one that uses boluses. A slow dawn rise draws a trickle short of it.
    /// </summary>
    public const double CorroborationBasalHours = 0.5;

    /// <summary>
    /// The least net loop correction that corroborates a rise whatever the schedule, U: the smallest
    /// automatic bolus most pumps deliver is 0.05 to 0.1 U, so a few of them, not one, answer a meal.
    /// </summary>
    public const double MinimumCorroborationUnits = 0.2;

    /// <summary>How far before the anchor the mean scheduled rate of <see cref="CorroborationBasalHours"/> is read.</summary>
    public const long CorroborationScheduleMs = 24 * LocalSlotBinning.HourMs;

    /// <param name="glucose">Readings in time order.</param>
    /// <param name="exclusions">Time whose readings never qualify.</param>
    /// <param name="insulin">Read only with <paramref name="requireCorroboration"/>.</param>
    /// <param name="carbsOnBoard">Carbs on board at an instant, g, where known.</param>
    public static RiseSearch Find(
        IReadOnlyList<GlucoseSample> glucose,
        IntervalSet exclusions,
        IReadOnlyList<CarbEvent> carbs,
        NetInsulinAboveSchedule insulin,
        bool requireCorroboration,
        Func<long, double?>? carbsOnBoard = null)
    {
        var readings = glucose
            .Where(g => GlucoseStatistics.IsReading(g.Mgdl) && !exclusions.Contains(g.Mills))
            .ToArray();
        var carbMills = EventAnchors.CarbMills(carbs);
        var anchors = new List<EventAnchor>();
        var climbs = new List<long>();
        var last = long.MinValue;

        var stableStart = 0;
        for (var i = 0; i < readings.Length; i++)
        {
            while (readings[stableStart].Mills < readings[i].Mills - StableMs)
                stableStart++;
            if (StableFloor(readings, stableStart, i) is not { } floor
                || Math.Abs(readings[i].Mgdl - floor) > FloorToleranceMgdl
                || ClimbFrom(readings, i, floor) is not { } anchor
                || IsCompressionRecovery(readings, stableStart, anchor, floor))
                continue;

            var at = readings[anchor].Mills;
            if (at <= last)
                continue;
            var merged = last != long.MinValue && at < last + MergeMs;
            last = at;
            if (merged)
                continue;
            if (AnnouncingCarbs(carbMills, at) is { } entry)
            {
                if (at - entry >= CarbAttributionMs)
                    climbs.Add(at);
                continue;
            }
            climbs.Add(at);
            if (carbsOnBoard?.Invoke(at) is > 0)
                continue;
            if (requireCorroboration && !Corroborated(insulin, at))
                continue;
            anchors.Add(new EventAnchor(at, EventAnchorKind.UnannouncedRise));
        }
        return new RiseSearch(anchors, climbs);
    }

    /// <summary>
    /// The latest carb entry from <see cref="CarbsBeforeMs"/> before <paramref name="at"/> to
    /// <see cref="CarbsAfterMs"/> after it, or null.
    /// </summary>
    private static long? AnnouncingCarbs(long[] carbMills, long at)
    {
        var i = SortedSearch.FirstEndingAfter(carbMills, at + CarbsAfterMs, static m => m) - 1;
        return i >= 0 && carbMills[i] >= at - CarbsBeforeMs ? carbMills[i] : null;
    }

    /// <summary>
    /// The floor of the stretch from <paramref name="first"/> to <paramref name="last"/>, when it
    /// reaches back to <see cref="StableMs"/> without a long gap, spans no more than
    /// <see cref="StableRangeMgdl"/> and sits at or above <see cref="MinimumFloorMgdl"/>.
    /// </summary>
    private static double? StableFloor(GlucoseSample[] readings, int first, int last)
    {
        if (readings[first].Mills > readings[last].Mills - StableMs + MaxGapMs)
            return null;
        var values = new List<double>(last - first + 1);
        for (var k = first; k <= last; k++)
        {
            if (k > first && readings[k].Mills - readings[k - 1].Mills > MaxGapMs)
                return null;
            values.Add(readings[k].Mgdl);
        }
        values.Sort();
        if (values[^1] - values[0] > StableRangeMgdl)
            return null;
        var floor = GlucoseStatistics.Percentile(values, FloorQuantile);
        return floor >= MinimumFloorMgdl ? floor : null;
    }

    /// <summary>
    /// Follows the readings after <paramref name="start"/>, which is at <paramref name="floor"/>:
    /// the anchor moves to each later reading still at the floor, and the climb qualifies once a
    /// reading is <see cref="RiseMgdl"/> above the floor within <see cref="RiseWindowMs"/> of it. A
    /// floor holds only for <see cref="StableMs"/> past the stretch it was read from; glucose resting
    /// longer is read against the floor of a later stretch, so a floor set as glucose came down from
    /// an earlier peak does not stand for hours of a lower one.
    /// </summary>
    /// <returns>The anchor's index, or null when no qualifying climb follows.</returns>
    private static int? ClimbFrom(GlucoseSample[] readings, int start, double floor)
    {
        var anchor = start;
        for (var j = start + 1; j < readings.Length; j++)
        {
            if (readings[j].Mills - readings[j - 1].Mills > MaxGapMs
                || TooFast(readings, j)
                || (readings[j].Mills - readings[j - 1].Mills <= StepMs && readings[j].Mgdl - readings[j - 1].Mgdl >= StepMgdl)
                || readings[j].Mgdl < floor - StableRangeMgdl)
                return null;
            if (readings[j].Mgdl <= floor + FloorToleranceMgdl)
            {
                if (readings[j].Mills - readings[start].Mills > StableMs)
                    return null;
                anchor = j;
                continue;
            }
            if (readings[j].Mills - readings[anchor].Mills > RiseWindowMs)
                return null;
            if (readings[j].Mgdl - floor >= RiseMgdl)
                return anchor;
        }
        return null;
    }

    /// <summary>
    /// Whether the floor is a compression low and the climb from <paramref name="anchor"/> its
    /// recovery: glucose dropped onto the floor's plateau at least <see cref="CompressionDropMgdl"/>
    /// at <see cref="CompressionDropRateMgdlPerMinute"/> or faster, from a level seen within
    /// <see cref="CompressionLookbackMs"/> before the plateau began, and the climb comes back within
    /// <see cref="CompressionReturnMgdl"/> of that level within <see cref="RiseWindowMs"/>.
    /// </summary>
    /// <param name="stableStart">The first reading of the stretch the floor was read from.</param>
    private static bool IsCompressionRecovery(GlucoseSample[] readings, int stableStart, int anchor, double floor)
    {
        var plateau = stableStart;
        while (plateau > 0
            && readings[plateau].Mills - readings[plateau - 1].Mills <= MaxGapMs
            && Math.Abs(readings[plateau - 1].Mgdl - floor) <= StableRangeMgdl)
            plateau--;

        GlucoseSample? before = null;
        for (var k = plateau - 1; k >= 0 && readings[k].Mills >= readings[plateau].Mills - CompressionLookbackMs; k--)
        {
            if (before is null || readings[k].Mgdl > before.Value.Mgdl)
                before = readings[k];
        }
        if (before is not { } level || level.Mgdl - floor < CompressionDropMgdl)
            return false;

        var minutes = (readings[plateau].Mills - level.Mills) / (double)LocalSlotBinning.MinuteMs;
        if ((level.Mgdl - readings[plateau].Mgdl) / minutes < CompressionDropRateMgdlPerMinute)
            return false;

        for (var j = anchor + 1; j < readings.Length && readings[j].Mills <= readings[anchor].Mills + RiseWindowMs; j++)
        {
            if (readings[j].Mgdl >= level.Mgdl - CompressionReturnMgdl)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Whether glucose reached reading <paramref name="j"/> faster than
    /// <see cref="MaxRiseRateMgdlPerMinute"/> from the last reading at least <see cref="RateSpanMs"/>
    /// before it.
    /// </summary>
    private static bool TooFast(GlucoseSample[] readings, int j)
    {
        var k = SortedSearch.FirstEndingAfter(readings, readings[j].Mills - RateSpanMs, static r => r.Mills) - 1;
        if (k < 0)
            return false;
        var minutes = (readings[j].Mills - readings[k].Mills) / (double)LocalSlotBinning.MinuteMs;
        return (readings[j].Mgdl - readings[k].Mgdl) / minutes > MaxRiseRateMgdlPerMinute;
    }

    /// <summary>
    /// Whether the loop answered the rise: net loop correction over <see cref="CorroborationMs"/> from
    /// the anchor of at least <see cref="CorroborationBasalHours"/> of the mean scheduled rate over the
    /// day before it, and never less than <see cref="MinimumCorroborationUnits"/>.
    /// </summary>
    internal static bool Corroborated(NetInsulinAboveSchedule insulin, long anchor) =>
        insulin.Over(anchor, anchor + CorroborationMs).NetLoopCorrection
            >= Math.Max(CorroborationBasalHours * MeanScheduledRate(insulin, anchor - CorroborationScheduleMs, anchor), MinimumCorroborationUnits);

    /// <summary>The mean scheduled rate over the time in <c>[from, to)</c> a schedule is known for, U/h; 0 where none is.</summary>
    private static double MeanScheduledRate(NetInsulinAboveSchedule insulin, long from, long to)
    {
        var pieces = insulin.Pieces;
        double units = 0;
        long known = 0;
        for (var i = SortedSearch.FirstEndingAfter(pieces, from, static p => p.End); i < pieces.Count && pieces[i].Start < to; i++)
        {
            if (pieces[i].Scheduled is not { } rate)
                continue;
            var ms = Math.Min(to, pieces[i].End) - Math.Max(from, pieces[i].Start);
            units += rate * ms;
            known += ms;
        }
        return known > 0 ? units / known : 0;
    }
}
