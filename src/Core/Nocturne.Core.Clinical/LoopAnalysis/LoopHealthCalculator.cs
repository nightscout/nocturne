using System.Globalization;
using Nocturne.Core.Clinical.Statistics;
using Nocturne.Core.Models;
using Nocturne.Core.Models.LoopAnalysis;
using Nocturne.Core.Models.V4;

namespace Nocturne.Core.Clinical.LoopAnalysis;

/// <summary>Everything Loop health is computed from, fetched once for every range it covers.</summary>
public sealed record LoopHealthInputs
{
    public required TimeZoneInfo Zone { get; init; }

    /// <summary>
    /// Every cycle in the range and the temp lookback before it, with the nearest cycle beyond each
    /// end, so a gap that straddles an end is bounded by the cycles either side of it.
    /// </summary>
    public IReadOnlyList<ApsLoopCycle> Cycles { get; init; } = [];

    public required NetInsulinAboveSchedule Insulin { get; init; }

    public required BasalEvidence Evidence { get; init; }

    /// <summary>
    /// The recorded temp basals as <see cref="NetInsulinAboveSchedule.ClipTempBasals"/> clips them,
    /// ascending by start: the temps the delivered pieces belong to.
    /// </summary>
    public IReadOnlyList<TempBasal> Temps { get; init; } = [];

    /// <summary>Canonical readings.</summary>
    public IReadOnlyList<SensorGlucose> Glucose { get; init; } = [];

    /// <summary>Loop's maximum basal rate setting, U/h, where the profile in force records one.</summary>
    public StepTimeline<double>? LoopMaximumBasalRate { get; init; }
}

/// <summary>
/// Loop health: how the loop ran, figure by figure, for a range and for each local hour of the day.
/// </summary>
/// <remarks>
/// A share is time (or, for suggestions, events) measured over the analysed time. Each day and
/// each day's hour counts in the spread only where at least
/// <see cref="SlotSummary.MinimumSlotCoverage"/> of its wall-clock length was analysed, so a DST
/// day's long or short hour is weighed by its length. A count per day is normalised by the
/// analysed share of the day or hour it fell in.
/// </remarks>
public static class LoopHealthCalculator
{
    /// <summary>Two loop cycles further apart than this leave a gap: at least two cycles were missed.</summary>
    public const long LoopGapMs = 15 * LocalSlotBinning.MinuteMs;

    /// <summary>
    /// The most time one cycle stands for in the activity shares, as <c>ApsSnapshotStrategy</c>
    /// counts cycles for <c>aid-system-metrics</c>; a sooner cycle cuts it short.
    /// </summary>
    public const long CycleCreditMs = 5 * LocalSlotBinning.MinuteMs;

    /// <summary>
    /// How long before a range a temp still running at its start can have been set: the longest
    /// temp an AID algorithm sets. The cycles are read from this far before a range.
    /// </summary>
    public const long TempLookbackMs = 2 * LocalSlotBinning.HourMs;

    /// <summary>A temp this close below the cap is at it: half the 0.05 U/h step pumps set rates in.</summary>
    public const double MaxBasalToleranceUph = 0.025;

    /// <summary>
    /// How far either side of a temp record's start the cycle that set it is looked for. The
    /// snapshot is stamped when the cycle ran and the record when the pump confirmed the temp, which
    /// can come minutes later, or the other way round where an uploader stamps both late.
    /// </summary>
    public const long SetterWindowMs = 5 * LocalSlotBinning.MinuteMs;

    /// <summary>IOB this close below a reported <c>max_iob</c> is at it.</summary>
    public const double MaxIobToleranceU = 0.1;

    private const int HoursPerDay = 24;

    public static bool IsOref(AidAlgorithm algorithm) =>
        algorithm is AidAlgorithm.OpenAps or AidAlgorithm.AndroidAps or AidAlgorithm.Trio or AidAlgorithm.IAPS;

    public static LoopHealthRange Calculate(LoopHealthInputs inputs, LoopAnalysisMask mask, long from, long to)
    {
        var grid = new HourGrid(inputs.Zone, from, to, mask.Analysed);
        var cycles = LoopCycles.Distinct(inputs.Cycles, c => c.Mills, c => c.Enacted);
        var inRange = cycles.Where(c => c.Mills >= from && c.Mills < to).ToList();
        var range = new LoopHealthRange
        {
            From = DateTimeOffset.FromUnixTimeMilliseconds(from).UtcDateTime,
            To = DateTimeOffset.FromUnixTimeMilliseconds(to).UtcDateTime,
            Days = grid.Days(),
            Activity = Activity(inputs, cycles, grid, from, to),
            Cadence = inRange.Count == 0 ? new LoopHealthCadence() : new LoopHealthCadence
            {
                LoopGaps = LoopGaps(inputs, cycles, grid, from, to),
                UnrecordedDelivery = grid.Gaps(
                    Looping(cycles).Subtract(inputs.Evidence.Recorded.Union(inputs.Evidence.Inferable)).Intervals, from, to),
            },
            ZeroDelivery = ZeroDelivery(inputs.Insulin, grid),
            MaxBasal = MaxBasal(inputs, cycles, grid, from, to, out var capUnknown),
            AtMaxIob = AtMaxIob(cycles, grid),
            AutomaticBoluses = AutomaticBoluses(inputs.Insulin, grid),
            SuggestedNotEnacted = SuggestedNotEnacted(cycles, grid),
            SensitivityRatio = grid.Pooled(cycles
                .Where(c => c.SensitivityRatio is not null && grid.IsAnalysed(c.Mills))
                .Select(c => (c.Mills, c.SensitivityRatio!.Value))),
            Limits = Limits(inputs, cycles, from, to),
        };
        range.Unknown = Unknown(range, inRange, capUnknown);
        return range;
    }

    /// <summary>
    /// Why each figure left null is unknown, read off the cycles recorded in the range: none at all,
    /// none of a system that reports it (iAPS is oref), none whose suggestion names it, or only in
    /// time the masks left out.
    /// </summary>
    private static LoopHealthUnknown Unknown(
        LoopHealthRange range, List<ApsLoopCycle> inRange, LoopHealthUnknownReason? capUnknown)
    {
        var none = inRange.Count == 0;
        var oref = inRange.Where(c => IsOref(c.AidAlgorithm)).ToList();
        var loop = inRange.Any(c => c.AidAlgorithm == AidAlgorithm.Loop);
        LoopHealthUnknownReason Oref(Func<ApsLoopCycle, bool> recorded) =>
            none ? LoopHealthUnknownReason.NoCyclesInRange
            : oref.Count == 0 ? LoopHealthUnknownReason.NotRecordedByThisSystem
            : !oref.Any(recorded) ? LoopHealthUnknownReason.NoReasonRecorded
            : LoopHealthUnknownReason.MaskedOut;

        return new LoopHealthUnknown
        {
            LoopGaps = none ? LoopHealthUnknownReason.NoCyclesInRange : null,
            UnrecordedDelivery = none ? LoopHealthUnknownReason.NoCyclesInRange : null,
            MaxBasal = capUnknown,
            AtMaxIob = range.AtMaxIob is null ? Oref(c => c.HasReason) : null,
            SuggestedNotEnacted = range.SuggestedNotEnacted is null ? Oref(c => c.SuggestedChange == true) : null,
            LoopMaximumBasalRate = range.Limits.LoopMaximumBasalRate is not null ? null
                : none ? LoopHealthUnknownReason.NoCyclesInRange
                : !loop ? LoopHealthUnknownReason.NotRecordedByThisSystem
                : LoopHealthUnknownReason.SettingNotUploaded,
            OrefMaxIob = range.Limits.OrefMaxIob is not null ? null
                : none ? LoopHealthUnknownReason.NoCyclesInRange
                : oref.Count == 0 ? LoopHealthUnknownReason.NotRecordedByThisSystem
                : LoopHealthUnknownReason.NoReasonRecorded,
        };
    }

    /// <summary>Time between two cycles no further apart than <see cref="LoopGapMs"/>.</summary>
    private static IntervalSet Looping(List<ApsLoopCycle> cycles) =>
        IntervalSet.Of(cycles.Zip(cycles.Skip(1))
            .Where(pair => pair.Second.Mills - pair.First.Mills <= LoopGapMs)
            .Select(pair => new MillsInterval(pair.First.Mills, pair.Second.Mills)));

    /// <summary>
    /// Each instant credited for up to <paramref name="creditMs"/>, cut short by the next instant
    /// in <paramref name="following"/> order, merged so overlapping credits count once.
    /// </summary>
    private static IntervalSet Credits(IReadOnlyList<long> instants, IReadOnlyList<long> following, long creditMs)
    {
        var credits = new List<MillsInterval>(instants.Count);
        foreach (var at in instants)
        {
            var next = SortedSearch.FirstEndingAfter(following, at, static m => m);
            var end = next < following.Count ? Math.Min(at + creditMs, following[next]) : at + creditMs;
            credits.Add(new MillsInterval(at, end));
        }
        return IntervalSet.Of(credits);
    }

    /// <summary>
    /// The cycles the loop was closed at. oref enacts only a change: holding a temp ("temp X &gt;~
    /// req Y") or needing none, it enacts nothing while still in control, so its state is the
    /// outcome of the newest cycle that asked for a change, closed if that was enacted and open if
    /// not. An enacted cycle closes it whether or not its suggestion was stored. Loop records nothing it decided apart from what it enacted, so only an enacted cycle
    /// shows it closed.
    /// </summary>
    private static List<long> ClosedLoopCycles(List<ApsLoopCycle> cycles)
    {
        var closed = new List<long>();
        var orefClosed = false;
        foreach (var cycle in cycles)
        {
            if (IsOref(cycle.AidAlgorithm))
            {
                if (cycle.Enacted)
                    orefClosed = true;
                else if (cycle.SuggestedChange == true)
                    orefClosed = false;
                if (orefClosed)
                    closed.Add(cycle.Mills);
            }
            else if (cycle.Enacted)
                closed.Add(cycle.Mills);
        }
        return closed;
    }

    private static LoopHealthActivity Activity(
        LoopHealthInputs inputs, List<ApsLoopCycle> cycles, HourGrid grid, long from, long to)
    {
        var closed = grid.New();
        var pump = grid.New();
        if (cycles.Any(c => c.Mills >= from && c.Mills < to))
        {
            var all = cycles.Select(c => c.Mills).ToList();
            grid.AddTime(pump, Credits(all, all, CycleCreditMs));
            grid.AddTime(closed, Credits(ClosedLoopCycles(cycles), all, CycleCreditMs));
        }
        else
        {
            foreach (var piece in inputs.Insulin.Pieces.Where(p => p.Delivered.HasValue))
            {
                if (piece.Origin is NetInsulinOrigin.AlgorithmTemp
                    && TempOf(inputs, piece.Start)?.OriginEvidence
                        is TempBasalOriginEvidence.Reported or TempBasalOriginEvidence.Snapshot or TempBasalOriginEvidence.OtherDevice)
                    grid.AddTime(closed, piece.Start, piece.End);
                if (piece.Origin is NetInsulinOrigin.AlgorithmTemp or NetInsulinOrigin.ManualTemp or NetInsulinOrigin.Suspend)
                    grid.AddTime(pump, piece.Start, piece.End);
            }
        }

        var cgm = IntervalSet.Empty;
        foreach (var stream in inputs.Glucose.Where(g => GlucoseStatistics.IsReading(g.Mgdl))
                     .GroupBy(CanonicalGlucoseStream.StreamKey, StringComparer.Ordinal))
        {
            var ordered = stream.OrderBy(g => g.Mills).ToList();
            var creditMs = (long)(GlucoseCadence.SeriesCadenceMinutes(GlucoseCadence.ReadingIntervals(ordered))
                * LocalSlotBinning.MinuteMs);
            var mills = ordered.Select(g => g.Mills).ToList();
            cgm = cgm.Union(Credits(mills, mills, creditMs));
        }
        var cgmTime = grid.New();
        grid.AddTime(cgmTime, cgm);

        return new LoopHealthActivity
        {
            ClosedLoop = grid.Share(closed, grid.Analysed),
            Pump = grid.Share(pump, grid.Analysed),
            Cgm = grid.Share(cgmTime, grid.Analysed),
        };
    }

    /// <summary>
    /// Gaps of more than <see cref="LoopGapMs"/> between cycles. A loop that stops for good before
    /// the range ends leaves a gap from its last cycle until the newest glucose reading or temp
    /// basal record, whichever is later, within the range: past that the data ends, not the loop.
    /// </summary>
    private static LoopHealthGaps LoopGaps(LoopHealthInputs inputs, List<ApsLoopCycle> cycles, HourGrid grid, long from, long to)
    {
        var gaps = cycles.Zip(cycles.Skip(1))
            .Where(pair => pair.Second.Mills - pair.First.Mills > LoopGapMs)
            .Select(pair => new MillsInterval(pair.First.Mills, pair.Second.Mills))
            .ToList();

        if (cycles.Count > 0 && cycles[^1].Mills < to)
        {
            var dataEnd = Math.Min(to, Math.Max(
                inputs.Glucose.Count > 0 ? inputs.Glucose.Max(g => g.Mills) : long.MinValue,
                inputs.Evidence.Recorded.IsEmpty ? long.MinValue : inputs.Evidence.Recorded.Intervals[^1].End));
            if (dataEnd - cycles[^1].Mills > LoopGapMs)
                gaps.Add(new MillsInterval(cycles[^1].Mills, dataEnd));
        }
        return grid.Gaps(gaps, from, to);
    }

    private static LoopHealthZeroDelivery ZeroDelivery(NetInsulinAboveSchedule insulin, HourGrid grid)
    {
        var known = grid.New();
        var algorithm = grid.New();
        var manual = grid.New();
        var suspended = grid.New();
        foreach (var piece in insulin.Pieces)
        {
            if (piece.Delivered is not { } delivered)
                continue;
            grid.AddTime(known, piece.Start, piece.End);
            if (piece.Origin == NetInsulinOrigin.Suspend)
                grid.AddTime(suspended, piece.Start, piece.End);
            else if (delivered == 0 && piece.Origin == NetInsulinOrigin.AlgorithmTemp)
                grid.AddTime(algorithm, piece.Start, piece.End);
            else if (delivered == 0 && piece.Origin == NetInsulinOrigin.ManualTemp)
                grid.AddTime(manual, piece.Start, piece.End);
        }

        return new LoopHealthZeroDelivery
        {
            AlgorithmZeroTemp = grid.Share(algorithm, known),
            ManualZeroTemp = grid.Share(manual, known),
            Suspended = grid.Share(suspended, known),
        };
    }

    /// <summary>
    /// Time the algorithm's temps were delivering at the effective cap, measured on the delivered
    /// basal: a temp held through cycles that enacted nothing counts for as long as it ran. Each
    /// piece belongs to the temp record it falls in, and the cycles judge that temp's cap: Loop's
    /// setting at the time, or for oref, whether the cycle that set the temp cut it to
    /// <c>maxSafeBasal</c>.
    /// </summary>
    /// <param name="unknown">
    /// Where no temp's cap is known, why: no algorithm temps, or only in masked time, else the reason
    /// that held for the most algorithm temp time, judged by each temp's own system.
    /// </param>
    private static LoopHealthMaxBasal MaxBasal(
        LoopHealthInputs inputs, List<ApsLoopCycle> cycles, HourGrid grid, long from, long to,
        out LoopHealthUnknownReason? unknown)
    {
        var unanalysed = false;
        var uncapped = new Dictionary<LoopHealthUnknownReason, double>();
        var enacted = cycles.Where(c => c.Enacted && c.EnactedRate is not null).ToList();
        var cyclesInRange = cycles.Any(c => c.Mills >= from && c.Mills < to);
        var byId = new Dictionary<Guid, ApsLoopCycle>(cycles.Count);
        foreach (var cycle in cycles)
            byId.TryAdd(cycle.Id, cycle);
        var algorithmTime = grid.New();
        var known = grid.New();
        var atCap = grid.New();

        foreach (var piece in inputs.Insulin.Pieces)
        {
            if (piece.Origin != NetInsulinOrigin.AlgorithmTemp || piece.Delivered is not { } rate)
                continue;
            var analysedMs = grid.AddTime(algorithmTime, piece.Start, piece.End);
            if (analysedMs == 0)
            {
                unanalysed |= piece.Start < to && piece.End > from;
                continue;
            }
            var temp = TempOf(inputs, piece.Start);
            var setter = temp?.ApsSnapshotId is { } id ? byId.GetValueOrDefault(id) : null;
            if (AtCap(temp?.RecordStartMills ?? piece.Start, setter, rate, cycles, cyclesInRange, enacted, inputs.LoopMaximumBasalRate, out var why)
                is not { } capped)
            {
                uncapped[why] = uncapped.GetValueOrDefault(why) + analysedMs;
                continue;
            }
            grid.AddTime(known, piece.Start, piece.End);
            if (capped)
                grid.AddTime(atCap, piece.Start, piece.End);
        }

        var result = new LoopHealthMaxBasal
        {
            AtCap = grid.Share(atCap, known),
            CapKnown = grid.Share(known, algorithmTime),
        };
        unknown = result.AtCap.Percent is not null ? null
            : uncapped.Count > 0 ? uncapped.MaxBy(u => u.Value).Key
            : unanalysed ? LoopHealthUnknownReason.MaskedOut
            : LoopHealthUnknownReason.NoAlgorithmTemps;
        return result;
    }

    /// <summary>
    /// Whether a temp of <paramref name="rate"/> that began at <paramref name="tempStart"/> was at
    /// its cap; null where the cap is unknown, with <paramref name="why"/>. The temp's system is that
    /// of the newest cycle by its start, no more than <see cref="TempLookbackMs"/> before it, the
    /// longest an algorithm's temp runs; with none, no cycle bears on it. An oref temp is judged by the cycle that set it:
    /// <paramref name="recordedSetter"/>, the snapshot the temp names, where it was read and kept, else, of
    /// the enacted cycles within <see cref="SetterWindowMs"/> of its start whose rate is the delivered
    /// one (<see cref="TempBasalOriginProvenance.SameRate"/>), the nearest. The rate decides before the
    /// time does: the record is often stamped after the next cycle has run and enacted another
    /// rate. With no such cycle the cap is unknown: an older cycle that set the same rate set a
    /// different temp, and its reason says nothing of this one.
    /// </summary>
    private static bool? AtCap(
        long tempStart,
        ApsLoopCycle? recordedSetter,
        double rate,
        List<ApsLoopCycle> cycles,
        bool cyclesInRange,
        List<ApsLoopCycle> enacted,
        StepTimeline<double>? loopMaximum,
        out LoopHealthUnknownReason why)
    {
        why = LoopHealthUnknownReason.NoCyclesInRange;
        var latest = SortedSearch.FirstEndingAfter(cycles, tempStart + SetterWindowMs, static c => c.Mills) - 1;
        if (latest < 0 || cycles[latest].Mills < tempStart - TempLookbackMs)
            return null;

        if (cycles[latest].AidAlgorithm == AidAlgorithm.Loop)
        {
            why = LoopHealthUnknownReason.SettingNotUploaded;
            return loopMaximum is not null && loopMaximum.TryAt(tempStart, out var maximum)
                ? rate >= maximum - MaxBasalToleranceUph
                : null;
        }

        why = LoopHealthUnknownReason.NotRecordedByThisSystem;
        if (!IsOref(cycles[latest].AidAlgorithm))
            return null;

        var setter = recordedSetter ?? NearestSetter(tempStart, rate, enacted);
        why = setter is not null ? LoopHealthUnknownReason.NoReasonRecorded
            : cyclesInRange ? LoopHealthUnknownReason.SetterNotFound
            : LoopHealthUnknownReason.NoCyclesInRange;
        return setter is { HasReason: true } ? OrefReason.Parse(setter.LimitReason).CappedAtMaxSafeBasal : null;
    }

    private static ApsLoopCycle? NearestSetter(long tempStart, double rate, List<ApsLoopCycle> enacted)
    {
        ApsLoopCycle? setter = null;
        for (var i = SortedSearch.FirstEndingAfter(enacted, tempStart - SetterWindowMs - 1, static c => c.Mills);
             i < enacted.Count && enacted[i].Mills <= tempStart + SetterWindowMs;
             i++)
        {
            if (TempBasalOriginProvenance.SameRate(enacted[i].EnactedRate!.Value, rate)
                && (setter is null || Math.Abs(enacted[i].Mills - tempStart) < Math.Abs(setter.Mills - tempStart)))
                setter = enacted[i];
        }
        return setter;
    }

    /// <summary>The temp a delivered piece starting at <paramref name="mills"/> belongs to.</summary>
    private static TempBasal? TempOf(LoopHealthInputs inputs, long mills) =>
        SortedSearch.FirstEndingAfter(inputs.Temps, mills, static t => t.StartMills) - 1 is var i and >= 0
            ? inputs.Temps[i]
            : null;

    /// <summary>
    /// Each oref cycle with a reason stands until the next, or for <see cref="LoopGapMs"/>.
    /// It is at <c>max_iob</c> where its reason says so, or where IOB is within
    /// <see cref="MaxIobToleranceU"/> of the newest <c>max_iob</c> a reason has reported so far.
    /// </summary>
    private static LoopHealthShare? AtMaxIob(List<ApsLoopCycle> cycles, HourGrid grid)
    {
        var oref = grid.New();
        var atMax = grid.New();
        double? maxIob = null;
        var any = false;
        for (var i = 0; i < cycles.Count; i++)
        {
            var cycle = cycles[i];
            if (!IsOref(cycle.AidAlgorithm) || !cycle.HasReason)
                continue;
            var reason = OrefReason.Parse(cycle.LimitReason);
            maxIob = reason.MaxIob ?? maxIob;
            var end = Math.Min(i + 1 < cycles.Count ? cycles[i + 1].Mills : long.MaxValue, cycle.Mills + LoopGapMs);
            any |= grid.AddTime(oref, cycle.Mills, end) > 0;
            if (reason.AtMaxIob || cycle.Iob >= maxIob - MaxIobToleranceU)
                grid.AddTime(atMax, cycle.Mills, end);
        }
        return any ? grid.Share(atMax, oref) : null;
    }

    private static LoopHealthAutomaticBoluses AutomaticBoluses(NetInsulinAboveSchedule insulin, HourGrid grid)
    {
        var count = grid.New();
        var sizes = new List<(long, double)>();
        foreach (var dose in insulin.Doses)
        {
            if (!dose.Automatic || !grid.IsAnalysed(dose.Mills) || grid.IndexAt(dose.Mills) is not (var i and >= 0))
                continue;
            count[i]++;
            sizes.Add((dose.Mills, dose.Units));
        }

        return new LoopHealthAutomaticBoluses
        {
            PerDay = grid.PerDay(count),
            Size = grid.Pooled(sizes),
        };
    }

    private static LoopHealthShare? SuggestedNotEnacted(List<ApsLoopCycle> cycles, HourGrid grid)
    {
        var suggested = grid.New();
        var notEnacted = grid.New();
        var any = false;
        foreach (var cycle in cycles)
        {
            if (!IsOref(cycle.AidAlgorithm) || cycle.SuggestedChange != true || !grid.IsAnalysed(cycle.Mills)
                || grid.IndexAt(cycle.Mills) is not (var i and >= 0))
                continue;
            any = true;
            suggested[i]++;
            if (!cycle.Enacted)
                notEnacted[i]++;
        }
        return any ? grid.Share(notEnacted, suggested) : null;
    }

    private static LoopHealthLimits Limits(LoopHealthInputs inputs, List<ApsLoopCycle> cycles, long from, long to)
    {
        var loop = inputs.LoopMaximumBasalRate?.Steps.LastOrDefault(s => s.Start < to && s.End > from);
        return new LoopHealthLimits
        {
            LoopMaximumBasalRate = loop is { } step ? step.Value : null,
            OrefMaxIob = cycles
                .Where(c => c.Mills >= from && c.Mills < to && IsOref(c.AidAlgorithm))
                .Select(c => OrefReason.Parse(c.LimitReason).MaxIob)
                .LastOrDefault(v => v is not null),
        };
    }

    /// <summary>The range's local days by hour of the day, and its analysed time in each.</summary>
    private sealed class HourGrid
    {
        private readonly TimeZoneInfo _zone;
        private readonly IntervalSet _analysed;
        private readonly DateOnly _firstDay;
        private readonly int _dayCount;
        private readonly long[] _dayStarts;

        public HourGrid(TimeZoneInfo zone, long from, long to, IntervalSet analysed)
        {
            _zone = zone;
            _analysed = analysed;
            (_firstDay, _dayStarts) = LocalSlotBinning.DayStarts(from, to, zone);
            _dayCount = _dayStarts.Length - 1;

            Wall = New();
            Analysed = New();
            for (var d = 0; d < _dayCount; d++)
                Distribute(_dayStarts[d], _dayStarts[d + 1], (i, ms) => Wall[i] += ms);
            foreach (var interval in analysed.Intervals)
                Distribute(interval.Start, interval.End, (i, ms) => Analysed[i] += ms);
        }

        public double[] Wall { get; }

        public double[] Analysed { get; }

        public double[] New() => new double[_dayCount * HoursPerDay];

        public bool IsAnalysed(long mills) => _analysed.Contains(mills);

        public int IndexAt(long mills) => Index(LocalSlotBinning.LocalTime(mills, _zone));

        public void AddTime(double[] target, IntervalSet set)
        {
            foreach (var interval in set.Intervals)
                AddTime(target, interval.Start, interval.End);
        }

        /// <summary>Adds the analysed part of <c>[start, end)</c> to <paramref name="target"/>.</summary>
        /// <returns>The milliseconds added.</returns>
        public long AddTime(double[] target, long start, long end)
        {
            long added = 0;
            _analysed.ForEachOverlap(start, end, (pieceStart, pieceEnd) =>
                Distribute(pieceStart, pieceEnd, (i, ms) =>
                {
                    target[i] += ms;
                    added += ms;
                }));
            return added;
        }

        public List<LoopHealthDay> Days()
        {
            var days = new List<LoopHealthDay>();
            for (var d = 0; d < _dayCount; d++)
            {
                var date = _firstDay.AddDays(d);
                days.Add(new LoopHealthDay
                {
                    Date = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    DayOfWeek = date.DayOfWeek,
                    Start = DateTimeOffset.FromUnixTimeMilliseconds(_dayStarts[d]).UtcDateTime,
                });
            }
            return days;
        }

        public LoopHealthShare Share(double[] numerator, double[] denominator)
        {
            var hours = new List<LoopHealthShareHour>(HoursPerDay);
            for (var h = 0; h < HoursPerDay; h++)
            {
                double num = 0, den = 0;
                var daily = new List<double>();
                for (var d = 0; d < _dayCount; d++)
                {
                    var i = d * HoursPerDay + h;
                    num += numerator[i];
                    den += denominator[i];
                    if (Counts(Analysed[i], Wall[i]) && denominator[i] > 0)
                        daily.Add(Percent(numerator[i], denominator[i]));
                }
                hours.Add(new LoopHealthShareHour
                {
                    Hour = h,
                    Percent = den > 0 ? SlotSummary.Round(Percent(num, den)) : null,
                    Daily = SlotSummary.Of(daily),
                });
            }

            double totalNum = 0, totalDen = 0;
            var days = new List<double>();
            for (var d = 0; d < _dayCount; d++)
            {
                var num = Sum(numerator, d);
                var den = Sum(denominator, d);
                totalNum += num;
                totalDen += den;
                if (Counts(Sum(Analysed, d), Sum(Wall, d)) && den > 0)
                    days.Add(Percent(num, den));
            }

            return new LoopHealthShare
            {
                Percent = totalDen > 0 ? SlotSummary.Round(Percent(totalNum, totalDen)) : null,
                Daily = SlotSummary.Of(days),
                Hours = hours,
            };
        }

        /// <summary>
        /// <paramref name="amounts"/> per day: by hour, per standard hour of the hour's analysed
        /// time; for the range, per day scaled up to the day's length from its analysed share.
        /// </summary>
        public LoopHealthDistribution PerDay(double[] amounts)
        {
            var hours = new List<LoopHealthHourValue>(HoursPerDay);
            for (var h = 0; h < HoursPerDay; h++)
            {
                var values = new List<double>();
                for (var d = 0; d < _dayCount; d++)
                {
                    var i = d * HoursPerDay + h;
                    if (Counts(Analysed[i], Wall[i]))
                        values.Add(amounts[i] * LocalSlotBinning.HourMs / Analysed[i]);
                }
                hours.Add(new LoopHealthHourValue { Hour = h, Value = SlotSummary.Of(values) });
            }

            var days = new List<double>();
            for (var d = 0; d < _dayCount; d++)
            {
                var analysed = Sum(Analysed, d);
                var wall = Sum(Wall, d);
                if (Counts(analysed, wall))
                    days.Add(Sum(amounts, d) * wall / analysed);
            }
            return new LoopHealthDistribution { Overall = SlotSummary.Of(days), Hours = hours };
        }

        /// <summary>Every value pooled, by the hour it fell in and for the range; sparse by the days behind it.</summary>
        public LoopHealthDistribution Pooled(IEnumerable<(long Mills, double Value)> values)
        {
            var byHour = Enumerable.Range(0, HoursPerDay).Select(_ => new List<double>()).ToArray();
            var daysByHour = Enumerable.Range(0, HoursPerDay).Select(_ => new HashSet<int>()).ToArray();
            var all = new List<double>();
            var allDays = new HashSet<int>();
            foreach (var (mills, value) in values)
            {
                if (IndexAt(mills) is not (var i and >= 0))
                    continue;
                byHour[i % HoursPerDay].Add(value);
                daysByHour[i % HoursPerDay].Add(i / HoursPerDay);
                all.Add(value);
                allDays.Add(i / HoursPerDay);
            }

            return new LoopHealthDistribution
            {
                Overall = SlotSummary.Of(all, allDays.Count),
                Hours = [.. Enumerable.Range(0, HoursPerDay).Select(h => new LoopHealthHourValue
                {
                    Hour = h,
                    Value = SlotSummary.Of(byHour[h], daysByHour[h].Count),
                })],
            };
        }

        /// <summary>
        /// Gaps clipped to <c>[from, to)</c>, each counted once where any of it was analysed; the
        /// time share counts only the analysed part.
        /// </summary>
        public LoopHealthGaps Gaps(IEnumerable<MillsInterval> gaps, long from, long to)
        {
            var inGap = New();
            var kept = new List<LoopHealthGap>();
            foreach (var gap in gaps.OrderBy(g => g.Start))
            {
                var start = Math.Max(gap.Start, from);
                var end = Math.Min(gap.End, to);
                if (end <= start || AddTime(inGap, start, end) == 0)
                    continue;
                var local = LocalSlotBinning.LocalTime(start, _zone);
                kept.Add(new LoopHealthGap
                {
                    Start = DateTimeOffset.FromUnixTimeMilliseconds(start).UtcDateTime,
                    End = DateTimeOffset.FromUnixTimeMilliseconds(end).UtcDateTime,
                    Minutes = SlotSummary.Round((double)(end - start) / LocalSlotBinning.MinuteMs),
                    Date = DateOnly.FromDateTime(local).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    StartMinuteOfDay = local.Hour * 60 + local.Minute,
                });
            }

            return new LoopHealthGaps
            {
                Count = kept.Count,
                TotalMinutes = SlotSummary.Round(kept.Sum(g => g.Minutes)),
                LongestMinutes = kept.Count > 0 ? kept.Max(g => g.Minutes) : null,
                Gaps = kept,
            };
        }

        private static bool Counts(double analysed, double wall) =>
            wall > 0 && analysed > 0 && analysed >= SlotSummary.MinimumSlotCoverage * wall;

        private static double Percent(double numerator, double denominator) =>
            Math.Min(100.0 * numerator / denominator, 100.0);

        private static double Sum(double[] values, int day)
        {
            double sum = 0;
            for (var h = 0; h < HoursPerDay; h++)
                sum += values[day * HoursPerDay + h];
            return sum;
        }

        private int Index(DateTime local)
        {
            var day = DateOnly.FromDateTime(local).DayNumber - _firstDay.DayNumber;
            return day < 0 || day >= _dayCount ? -1 : day * HoursPerDay + local.Hour;
        }

        private void Distribute(long start, long end, Action<int, long> add) =>
            LocalSlotBinning.Distribute(start, end, _zone, 60, (local, ms) =>
            {
                if (Index(local) is var i and >= 0)
                    add(i, ms);
            });
    }
}
