using Nocturne.Core.Clinical.Statistics;
using Nocturne.Core.Clinical.Treatments;
using Nocturne.Core.Constants;
using Nocturne.Core.Models.LoopAnalysis;

namespace Nocturne.Core.Clinical.LoopAnalysis;

/// <summary>
/// The window around an anchor, in slots of <see cref="EventResponseDefinition.SlotMinutes"/>
/// measured from the anchor's exact time rather than snapped to the wall clock.
/// </summary>
public readonly record struct EventWindow(long BeforeMs, long AfterMs)
{
    public const long SlotMs = EventResponseDefinition.SlotMinutes * LocalSlotBinning.MinuteMs;

    public static EventWindow Of(EventResponseDefinition definition) => new(
        definition.MinutesBefore * LocalSlotBinning.MinuteMs, definition.MinutesAfter * LocalSlotBinning.MinuteMs);

    public int SlotCount => (int)((BeforeMs + AfterMs) / SlotMs);

    /// <summary>Where slot <paramref name="slot"/> starts, relative to the anchor.</summary>
    public long SlotOffset(int slot) => slot * SlotMs - BeforeMs;
}

/// <summary>
/// The series an episode reads, indexed once for every anchor: readings outside the glucose
/// exclusions, insulin above schedule, and the IOB/COB ticks that stand as values.
/// </summary>
public sealed class EventSeries
{
    private readonly GlucoseSample[] _readings;
    private readonly DailyProfileInputs _inputs;

    public EventSeries(DailyProfileInputs inputs, IntervalSet glucoseExclusions)
    {
        _inputs = inputs;
        _readings = [.. inputs.Glucose
            .Where(g => GlucoseStatistics.IsReading(g.Mgdl) && !glucoseExclusions.Contains(g.Mills))
            .OrderBy(g => g.Mills)];
    }

    public IReadOnlyList<GlucoseSample> Readings => _readings;

    public NetInsulinAboveSchedule Insulin => _inputs.Insulin;

    /// <summary>
    /// The reading nearest <paramref name="mills"/>, no further than
    /// <see cref="EventEpisode.ReadingToleranceMs"/> from it.
    /// </summary>
    public GlucoseSample? ReadingAt(long mills)
    {
        const long tolerance = EventEpisode.ReadingToleranceMs;
        GlucoseSample? nearest = null;
        var best = long.MaxValue;
        for (var i = FirstReadingFrom(mills - tolerance); i < _readings.Length && _readings[i].Mills <= mills + tolerance; i++)
        {
            var distance = Math.Abs(_readings[i].Mills - mills);
            if (distance < best)
            {
                best = distance;
                nearest = _readings[i];
            }
        }
        return nearest;
    }

    /// <summary>The glucose of <see cref="ReadingAt"/>.</summary>
    public double? GlucoseAt(long mills) => ReadingAt(mills)?.Mgdl;

    /// <summary>The mean reading in <c>[start, end)</c>.</summary>
    public double? MeanGlucose(long start, long end)
    {
        double sum = 0;
        var count = 0;
        for (var i = FirstReadingFrom(start); i < _readings.Length && _readings[i].Mills < end; i++)
        {
            sum += _readings[i].Mgdl;
            count++;
        }
        return count > 0 ? sum / count : null;
    }

    /// <summary>The first reading in <c>[from, to]</c>.</summary>
    public double? FirstGlucose(long from, long to)
    {
        var i = FirstReadingFrom(from);
        return i < _readings.Length && _readings[i].Mills <= to ? _readings[i].Mgdl : null;
    }

    /// <summary>Index of the first reading at or after <paramref name="mills"/>.</summary>
    public int FirstReadingFrom(long mills) =>
        SortedSearch.FirstEndingAfter(_readings, mills - 1, static r => r.Mills);

    /// <summary>
    /// IOB and COB over <c>[start, end)</c>, each tick weighted by the time it holds inside it; null
    /// where standing ticks cover less than <see cref="SlotSummary.MinimumSlotCoverage"/> of it.
    /// </summary>
    public (double Iob, double Cob)? IobCobOver(long start, long end)
    {
        var ticks = _inputs.IobCob;
        var interval = _inputs.IobCobIntervalMs;
        double iob = 0, cob = 0, covered = 0;
        for (var i = SortedSearch.FirstEndingAfter(ticks, start, t => t.Mills + interval); i < ticks.Count && ticks[i].Mills < end; i++)
        {
            if (!_inputs.Stands(ticks[i]))
                continue;
            var ms = Math.Min(end, ticks[i].Mills + interval) - Math.Max(start, ticks[i].Mills);
            if (ms <= 0)
                continue;
            iob += ticks[i].Iob * ms;
            cob += ticks[i].Cob * ms;
            covered += ms;
        }
        return covered > 0 && covered >= SlotSummary.MinimumSlotCoverage * (end - start)
            ? (iob / covered, cob / covered)
            : null;
    }

    public double? IobAt(long mills) => TickAt(mills)?.Iob;

    public double? CobAt(long mills) => TickAt(mills)?.Cob;

    /// <summary>The standing tick whose values hold at <paramref name="mills"/>.</summary>
    public IobCobTick? TickAt(long mills)
    {
        var ticks = _inputs.IobCob;
        var i = SortedSearch.FirstEndingAfter(ticks, mills, t => t.Mills + _inputs.IobCobIntervalMs);
        return i < ticks.Count && ticks[i].Mills <= mills && _inputs.Stands(ticks[i]) ? ticks[i] : null;
    }

    /// <summary>
    /// Delivered basal and every bolus over <c>[start, end)</c>; null where the pump's records
    /// account for less than <paramref name="minimumCoverage"/> of its basal.
    /// </summary>
    public double? TotalInsulin(long start, long end, double minimumCoverage)
    {
        var pieces = Insulin.Pieces;
        double basal = 0;
        long covered = 0;
        for (var i = SortedSearch.FirstEndingAfter(pieces, start, static p => p.End); i < pieces.Count && pieces[i].Start < end; i++)
        {
            if (pieces[i].Delivered is not { } rate)
                continue;
            var ms = Math.Min(end, pieces[i].End) - Math.Max(start, pieces[i].Start);
            basal += rate * ms / LocalSlotBinning.HourMs;
            covered += ms;
        }
        if (covered < minimumCoverage * (end - start))
            return null;
        var boluses = Insulin.Over(start, end);
        return basal + boluses.AutomaticBolus + boluses.ManualBolus;
    }

    /// <summary>
    /// Net insulin above schedule over <c>[start, end)</c>; null where both delivery and the
    /// schedule are known for less than <paramref name="minimumCoverage"/> of it.
    /// </summary>
    public NetInsulin? NetInsulinOver(long start, long end, double minimumCoverage)
    {
        var net = Insulin.Over(start, end);
        return net.CoveredMs > 0 && net.CoveredMs >= minimumCoverage * (end - start) ? net : null;
    }
}

/// <summary>
/// How far glucose fell over a window: the reading nearest its start minus the reading nearest its
/// end, each within <see cref="EventEpisode.ReadingToleranceMs"/>. Start minus end, not start minus
/// nadir: the nadir is the lowest of many noisy readings, so it is biased low, and the drop with it high.
/// </summary>
public readonly record struct GlucoseDrop(double Start, double End)
{
    public double Mgdl => Start - End;

    /// <returns>Null where no reading stands for either end.</returns>
    public static GlucoseDrop? Over(EventSeries series, long start, long end) =>
        series.GlucoseAt(start) is { } first && series.GlucoseAt(end) is { } last
            ? new GlucoseDrop(first, last)
            : null;

    /// <summary>
    /// When the reading <see cref="Over"/> takes for <paramref name="end"/> came: the one nearest it,
    /// which can fall after it. <paramref name="end"/> itself where there is none.
    /// </summary>
    public static long EndReadingMills(EventSeries series, long end) =>
        Math.Max(end, series.ReadingAt(end)?.Mills ?? end);
}

/// <summary>One relative slot of one episode; each value null where its data is missing.</summary>
public readonly record struct EpisodeSlot(
    double? GlucoseChange,
    double? Glucose,
    double? NetLoopCorrection,
    double? NetInsulinSinceAnchor,
    double? NetLoopCorrectionSinceAnchor,
    double? Iob,
    double? Cob);

/// <summary>
/// What glucose and insulin did around one anchor: every relative slot of its window, and the
/// figures read off it.
/// </summary>
/// <remarks>
/// A slot's rates follow the Daily profile's: net loop correction is the algorithm temp delta
/// over the time both delivery and the schedule are known plus automatic boluses over the slot, and
/// counts only where that time is at least <see cref="SlotSummary.MinimumSlotCoverage"/> of the
/// slot. Insulin accrued over a longer stretch (since the anchor, the window, the first 30 to 120
/// minutes) needs <see cref="DailyProfileCalculator.InsulinSummaryMinimumCoverage"/> of it, since a
/// gap there would be read as insulin not given.
/// </remarks>
public sealed class EventEpisode
{
    /// <summary>
    /// How far from an instant a reading may be and still stand for glucose then: one five-minute
    /// CGM interval.
    /// </summary>
    public const long ReadingToleranceMs = 5 * LocalSlotBinning.MinuteMs;

    /// <summary>
    /// How long after a sensor start its first reading may come and still stand for its start
    /// glucose: a new sensor reads nothing through its warm-up, up to two hours, and the start is
    /// often recorded a little before the sensor goes in.
    /// </summary>
    public const long SensorWarmUpMs = 3 * LocalSlotBinning.HourMs;

    /// <summary>The band around the start glucose that counts as back to start.</summary>
    public const double BackToStartMgdl = 20;

    private EventEpisode(EventAnchor anchor, bool isolated, EpisodeSlot[] slots, EventMetrics metrics)
    {
        Anchor = anchor;
        Isolated = isolated;
        Slots = slots;
        Metrics = metrics;
    }

    public EventAnchor Anchor { get; }

    /// <summary>No other meal, correction or rise fell inside the window, so its insulin is this event's.</summary>
    public bool Isolated { get; }

    public IReadOnlyList<EpisodeSlot> Slots { get; }

    public EventMetrics Metrics { get; }

    /// <param name="isolated">See <see cref="Isolated"/>; a meal's grams per unit is read only then.</param>
    /// <returns>Null when no reading stands for the start glucose.</returns>
    public static EventEpisode? Build(EventAnchor anchor, EventSeries series, EventWindow window, bool isolated)
    {
        var startGlucose = anchor.Kind == EventAnchorKind.SensorStart
            ? series.FirstGlucose(anchor.Mills, anchor.Mills + SensorWarmUpMs)
            : series.GlucoseAt(anchor.Mills);
        if (startGlucose is not { } start)
            return null;

        var slots = new EpisodeSlot[window.SlotCount];
        var coverage = DailyProfileCalculator.InsulinSummaryMinimumCoverage;
        NetInsulin? accrued = null;
        for (var k = 0; k < slots.Length; k++)
        {
            var slotStart = anchor.Mills + window.SlotOffset(k);
            var slotEnd = slotStart + EventWindow.SlotMs;
            var net = series.Insulin.Over(slotStart, slotEnd);
            NetInsulin? sinceAnchor = null;
            if (slotStart >= anchor.Mills)
            {
                accrued = accrued is null ? net : Sum(accrued, net);
                if (accrued.CoveredMs > 0 && accrued.CoveredMs >= coverage * (slotEnd - anchor.Mills))
                    sinceAnchor = accrued;
            }
            slots[k] = Slot(slotStart, slotEnd, net, sinceAnchor, start, series);
        }

        return new EventEpisode(anchor, isolated, slots, Measure(anchor, isolated, start, series, window));
    }

    private static NetInsulin Sum(NetInsulin a, NetInsulin b) => new(
        a.AlgorithmTemp + b.AlgorithmTemp,
        a.AutomaticBolus + b.AutomaticBolus,
        a.ManualTemp + b.ManualTemp,
        a.Suspend + b.Suspend,
        a.Inferred + b.Inferred,
        a.ManualBolus + b.ManualBolus,
        a.CoveredMs + b.CoveredMs);

    private static EpisodeSlot Slot(
        long slotStart, long slotEnd, NetInsulin net, NetInsulin? sinceAnchor, double start, EventSeries series)
    {
        var glucose = series.MeanGlucose(slotStart, slotEnd);

        double? correction = null;
        if (net.CoveredMs > 0 && net.CoveredMs >= SlotSummary.MinimumSlotCoverage * EventWindow.SlotMs)
            correction = net.AlgorithmTemp * LocalSlotBinning.HourMs / net.CoveredMs
                + net.AutomaticBolus * LocalSlotBinning.HourMs / EventWindow.SlotMs;

        var iobCob = series.IobCobOver(slotStart, slotEnd);

        return new EpisodeSlot(
            glucose - start,
            glucose,
            correction,
            sinceAnchor?.Total,
            sinceAnchor?.NetLoopCorrection,
            iobCob?.Iob,
            iobCob?.Cob);
    }

    private static EventMetrics Measure(EventAnchor anchor, bool isolated, double start, EventSeries series, EventWindow window)
    {
        var a = anchor.Mills;
        var end = a + window.AfterMs;
        var coverage = DailyProfileCalculator.InsulinSummaryMinimumCoverage;
        var net = series.NetInsulinOver(a - window.BeforeMs, end, coverage)?.Total;
        double? First(long minutes) => minutes * LocalSlotBinning.MinuteMs <= window.AfterMs
            ? series.NetInsulinOver(a, a + minutes * LocalSlotBinning.MinuteMs, coverage)?.Total
            : null;
        double? GlucoseAfter(long hours) => hours * LocalSlotBinning.HourMs <= window.AfterMs
            ? series.GlucoseAt(a + hours * LocalSlotBinning.HourMs)
            : null;

        var metrics = new EventMetrics
        {
            StartGlucose = start,
            GlucoseAt2Hours = GlucoseAfter(2),
            GlucoseAt4Hours = GlucoseAfter(4),
            TotalInsulin = series.TotalInsulin(a - window.BeforeMs, end, coverage),
            NetInsulin = net,
            IobAtAnchor = series.IobAt(a),
            IobAtEnd = series.IobAt(end),
            GramsPerUnit = isolated && anchor.Grams is { } grams && net is > 0 ? grams / net : null,
            NetInsulinFirst30 = First(30),
            NetInsulinFirst60 = First(60),
            NetInsulinFirst120 = First(120),
        };

        if (EventAnchors.ResponseOf(anchor.Kind) == EventResponseKind.Rise)
        {
            var excursion = Excursion(a, end, start, series);
            metrics.PeakRise = excursion.Peak - start;
            metrics.MinutesToPeak = Minutes(a, excursion.PeakAt);
            metrics.MinutesBackToStart = Minutes(a, excursion.Back);
        }
        else
        {
            var excursion = Excursion(a, GlucoseDrop.EndReadingMills(series, end) + 1, start, series);
            var drop = GlucoseDrop.Over(series, a, end)?.Mgdl;
            metrics.Drop = drop;
            metrics.Nadir = excursion.Nadir;
            metrics.MinutesToNadir = Minutes(a, excursion.NadirAt);
            metrics.WentBelowRange = excursion.Nadir is { } nadir ? nadir < GlucoseConstants.TargetBottomMgdl : null;
            metrics.DropPerUnit = isolated
                && excursion.Nadir is not < ObservedIsf.LowMgdl
                && drop is { } d
                && net >= SettingsEvidenceCalculator.MinimumInsulinUnits
                    ? d / net
                    : null;
        }
        return metrics;
    }

    private readonly record struct ExcursionFigures(double? Peak, long? PeakAt, double? Nadir, long? NadirAt, long? Back);

    /// <summary>
    /// The highest and lowest readings after the anchor and when each first came, and when glucose
    /// was back within <see cref="BackToStartMgdl"/> of the start after its furthest excursion either
    /// way: the anchor itself if it never left, null if it had not come back by <paramref name="end"/>.
    /// </summary>
    private static ExcursionFigures Excursion(long anchor, long end, double start, EventSeries series)
    {
        var readings = series.Readings;
        double? peak = null, nadir = null;
        long? peakAt = null, nadirAt = null;
        var furthest = -1;
        double furthestDistance = 0;
        var first = series.FirstReadingFrom(anchor + 1);
        for (var i = first; i < readings.Count && readings[i].Mills < end; i++)
        {
            if (peak is null || readings[i].Mgdl > peak)
            {
                peak = readings[i].Mgdl;
                peakAt = readings[i].Mills;
            }
            if (nadir is null || readings[i].Mgdl < nadir)
            {
                nadir = readings[i].Mgdl;
                nadirAt = readings[i].Mills;
            }
            var distance = Math.Abs(readings[i].Mgdl - start);
            if (distance > furthestDistance)
            {
                furthestDistance = distance;
                furthest = i;
            }
        }

        if (peak is null)
            return default;
        if (furthestDistance <= BackToStartMgdl)
            return new(peak, peakAt, nadir, nadirAt, anchor);
        for (var i = furthest + 1; i < readings.Count && readings[i].Mills < end; i++)
        {
            if (Math.Abs(readings[i].Mgdl - start) <= BackToStartMgdl)
                return new(peak, peakAt, nadir, nadirAt, readings[i].Mills);
        }
        return new(peak, peakAt, nadir, nadirAt, null);
    }

    private static double? Minutes(long from, long? to) =>
        to is { } at ? SlotSummary.Round((at - from) / (double)LocalSlotBinning.MinuteMs) : null;
}
