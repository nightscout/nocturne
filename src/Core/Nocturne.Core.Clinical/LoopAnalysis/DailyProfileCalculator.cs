using System.Globalization;
using Nocturne.Core.Clinical.Statistics;
using Nocturne.Core.Clinical.Treatments;
using Nocturne.Core.Models;
using Nocturne.Core.Models.LoopAnalysis;

namespace Nocturne.Core.Clinical.LoopAnalysis;

public readonly record struct GlucoseSample(long Mills, double Mgdl);

public readonly record struct CarbEvent(long Mills, double Grams);

/// <summary>Everything the Daily profile is computed from, fetched once for every range it covers.</summary>
public sealed record DailyProfileInputs
{
    public required TimeZoneInfo Zone { get; init; }
    public IReadOnlyList<GlucoseSample> Glucose { get; init; } = [];
    public required NetInsulinAboveSchedule Insulin { get; init; }
    public IReadOnlyList<CarbEvent> Carbs { get; init; } = [];

    /// <summary>Each tick's values hold for <see cref="IobCobIntervalMs"/> after it.</summary>
    public IReadOnlyList<IobCobTick> IobCob { get; init; } = [];
    public long IobCobIntervalMs { get; init; } = DailyProfileCalculator.SlotMinutes * LocalSlotBinning.MinuteMs;

    /// <summary>
    /// Where a calculated IOB or COB is a value: time the pump's records cover or let the schedule
    /// be inferred for, and time a recorded dose or carb entry is still acting. Elsewhere a
    /// calculated zero only says nothing was recorded, so a tick there with no device-reported
    /// value is absent. See <see cref="CalculableIobCobTime"/>.
    /// </summary>
    public IntervalSet CalculatedIobCobTime { get; init; } = IntervalSet.Empty;

    /// <summary>
    /// <see cref="LoopAnalysisFilter.HistoryVisibleFrom"/>. A calculated IOB or COB is a value only
    /// <see cref="DailyProfileCalculator.ActingMs"/> after it, once no hidden dose can still act.
    /// </summary>
    public long? HistoryVisibleFrom { get; init; }

    /// <summary>
    /// Whether a tick's IOB and COB are values: both reported by the device, or calculated where
    /// <see cref="CalculatedIobCobTime"/> holds the tick and the history it reads is visible. A
    /// tick half device and half calculated is a calculated one.
    /// </summary>
    public bool Stands(IobCobTick tick) =>
        tick.IobSource == IobCobSource.Device && tick.CobSource == IobCobSource.Device
        || CalculatedIobCobTime.Contains(tick.Mills)
        && tick.Mills >= VisibleHistory.ReadableFrom(HistoryVisibleFrom, DailyProfileCalculator.ActingMs);

    /// <summary>The target range in force at an instant, mg/dL; null where none is.</summary>
    public Func<long, (double Low, double High)?>? TargetAt { get; init; }
}

/// <summary>
/// The Daily profile: every series in 288 local five-minute slots of the day.
/// </summary>
/// <remarks>
/// <para>
/// A rate (basal, correction, IOB, COB) is first a per-day slot value, duration-weighted over the
/// slot's analysed time, then summarised across days. A day counts in a slot only where the series
/// covers at least <see cref="SlotSummary.MinimumSlotCoverage"/> of the slot's wall-clock length that
/// day, so a fall-back day's repeated slots (ten minutes long) are averaged over their full length
/// and a spring-forward day's skipped slots (no length) are simply missing from its count.
/// </para>
/// <para>
/// Glucose pools every reading in the slot across days, the AGP convention, so its bands agree
/// with the AGP report. A reading on a slot boundary belongs to the slot it starts.
/// </para>
/// </remarks>
public static class DailyProfileCalculator
{
    public const int SlotMinutes = 5;
    public const int SlotsPerDay = 24 * 60 / SlotMinutes;
    private const int SlotsPerHour = 60 / SlotMinutes;
    private const long SlotMs = SlotMinutes * LocalSlotBinning.MinuteMs;

    /// <summary>
    /// The share of a day that must be analysed, and covered by the basal the pump's records
    /// account for, for the day to count in the insulin and net loop correction per day. As Trio's
    /// and AAPS's total daily dose, those figures add up whole days and scale none: a masked or
    /// half-recorded day would be a day of missing boluses read as none, and scaling it up would
    /// multiply the boluses it did record. A DST day is measured against its own 23 or 25 hours.
    /// </summary>
    public const double InsulinSummaryMinimumCoverage = 0.9;

    /// <summary>
    /// How long after a dose or carb entry it is taken to still act. Eight hours covers the
    /// longest DIA an AID profile commonly runs (Loop's and the oref systems' curves end by 6 to 8
    /// hours) and the six hours <see cref="IobCobSeries"/> lets a carb entry absorb over.
    /// </summary>
    public const long ActingMs = 8 * LocalSlotBinning.HourMs;

    public static IntervalSet CalculableIobCobTime(BasalEvidence evidence, IEnumerable<long> doseAndCarbMills) =>
        evidence.Recorded.Union(evidence.Inferable)
            .Union(IntervalSet.Of(doseAndCarbMills.Select(m => new MillsInterval(m, m + ActingMs))));

    public static DailyProfileRange Calculate(DailyProfileInputs inputs, LoopAnalysisMask mask, long from, long to)
    {
        var tz = inputs.Zone;
        var (firstDay, dayStarts) = LocalSlotBinning.DayStarts(from, to, tz);
        var dayCount = dayStarts.Length - 1;
        var acc = new Accumulators(dayCount * SlotsPerDay);

        int Index(DateTime local)
        {
            var day = DateOnly.FromDateTime(local).DayNumber - firstDay.DayNumber;
            return day < 0 || day >= dayCount ? -1 : day * SlotsPerDay + LocalSlotBinning.SlotOf(local, SlotMinutes);
        }

        for (var d = 0; d < dayCount; d++)
            LocalSlotBinning.Distribute(dayStarts[d], dayStarts[d + 1], tz, SlotMinutes, (local, ms) =>
            {
                if (Index(local) is var i and >= 0) acc.Wall[i] += ms;
            });

        foreach (var interval in mask.Analysed.Intervals)
            LocalSlotBinning.Distribute(interval.Start, interval.End, tz, SlotMinutes, (start, local, ms) =>
            {
                if (Index(local) is not (var i and >= 0)) return;
                if (acc.Analysed[i] == 0) acc.FirstAnalysed[i] = start;
                acc.Analysed[i] += ms;
            });

        AccumulateBasal(inputs.Insulin, mask, tz, acc, Index);
        AccumulateEvents(inputs, mask, tz, acc, Index);
        AccumulateIobCob(inputs, mask, tz, acc, Index);
        AccumulateGlucose(inputs, mask, tz, acc, Index);

        var slots = new List<DailyProfileSlot>(SlotsPerDay);
        for (var s = 0; s < SlotsPerDay; s++)
            slots.Add(SummariseSlot(s, dayCount, acc, inputs.TargetAt));

        var days = Days(firstDay, dayCount, dayStarts, from, to, acc, out var summary);

        return new DailyProfileRange
        {
            From = DateTimeOffset.FromUnixTimeMilliseconds(from).UtcDateTime,
            To = DateTimeOffset.FromUnixTimeMilliseconds(to).UtcDateTime,
            SlotMinutes = SlotMinutes,
            Slots = slots,
            HourlyNetLoopCorrection = Hourly(dayCount, acc),
            Days = days,
            Summary = summary,
        };
    }

    private static void AccumulateBasal(
        NetInsulinAboveSchedule insulin, LoopAnalysisMask mask, TimeZoneInfo tz, Accumulators acc, Func<DateTime, int> index)
    {
        foreach (var piece in insulin.Pieces)
        {
            mask.Analysed.ForEachOverlap(piece.Start, piece.End, (start, end) =>
                LocalSlotBinning.Distribute(start, end, tz, SlotMinutes, (local, ms) =>
                {
                    if (index(local) is not (var i and >= 0)) return;
                    if (piece.Scheduled is { } scheduled)
                    {
                        acc.ScheduledW[i] += scheduled * ms;
                        acc.ScheduledMs[i] += ms;
                    }
                    if (piece.Delivered is { } delivered)
                    {
                        acc.DeliveredW[i] += delivered * ms;
                        acc.DeliveredMs[i] += ms;
                    }
                    if (piece.HasDelta)
                    {
                        acc.BothMs[i] += ms;
                        acc.BothScheduledW[i] += piece.Scheduled!.Value * ms;
                        var delta = (piece.Delivered!.Value - piece.Scheduled.Value) * ms;
                        switch (piece.Origin)
                        {
                            case NetInsulinOrigin.AlgorithmTemp: acc.AlgorithmW[i] += delta; break;
                            case NetInsulinOrigin.ManualTemp: acc.ManualTempW[i] += delta; break;
                            case NetInsulinOrigin.Suspend: acc.SuspendW[i] += delta; break;
                            default: acc.InferredW[i] += delta; break;
                        }
                    }
                }));
        }
    }

    private static void AccumulateEvents(
        DailyProfileInputs inputs, LoopAnalysisMask mask, TimeZoneInfo tz, Accumulators acc, Func<DateTime, int> index)
    {
        foreach (var dose in inputs.Insulin.Doses)
        {
            if (!mask.Analysed.Contains(dose.Mills) || index(LocalSlotBinning.LocalTime(dose.Mills, tz)) is not (var i and >= 0))
                continue;
            if (dose.Automatic)
                acc.AutomaticBolusU[i] += dose.Units;
            else
            {
                acc.ManualBolusU[i] += dose.Units;
                acc.ManualBolusN[i]++;
            }
        }

        foreach (var carb in inputs.Carbs)
        {
            if (carb.Grams <= 0 || !mask.Analysed.Contains(carb.Mills)
                || index(LocalSlotBinning.LocalTime(carb.Mills, tz)) is not (var i and >= 0))
                continue;
            acc.CarbG[i] += carb.Grams;
            acc.CarbN[i]++;
        }
    }

    private static void AccumulateIobCob(
        DailyProfileInputs inputs, LoopAnalysisMask mask, TimeZoneInfo tz, Accumulators acc, Func<DateTime, int> index)
    {
        foreach (var tick in inputs.IobCob)
        {
            if (!inputs.Stands(tick))
                continue;
            mask.Analysed.ForEachOverlap(tick.Mills, tick.Mills + inputs.IobCobIntervalMs, (start, end) =>
                LocalSlotBinning.Distribute(start, end, tz, SlotMinutes, (local, ms) =>
                {
                    if (index(local) is not (var i and >= 0)) return;
                    var slot = i % SlotsPerDay;
                    acc.IobW[i] += tick.Iob * ms;
                    acc.CobW[i] += tick.Cob * ms;
                    acc.IobCobMs[i] += ms;
                    (tick.IobSource == IobCobSource.Device ? acc.IobDeviceMs : acc.IobCalculatedMs)[slot] += ms;
                    (tick.CobSource == IobCobSource.Device ? acc.CobDeviceMs : acc.CobCalculatedMs)[slot] += ms;
                }));
        }
    }

    private static void AccumulateGlucose(
        DailyProfileInputs inputs, LoopAnalysisMask mask, TimeZoneInfo tz, Accumulators acc, Func<DateTime, int> index)
    {
        foreach (var reading in inputs.Glucose)
        {
            if (!GlucoseStatistics.IsReading(reading.Mgdl) || !mask.Glucose.Contains(reading.Mills)
                || index(LocalSlotBinning.LocalTime(reading.Mills, tz)) is not (var i and >= 0))
                continue;
            acc.GlucoseValues[i % SlotsPerDay].Add(reading.Mgdl);
            acc.GlucoseN[i]++;
        }
    }

    private static DailyProfileSlot SummariseSlot(
        int slot, int dayCount, Accumulators acc, Func<long, (double Low, double High)?>? targetAt)
    {
        List<double> scheduled = [], delivered = [], algorithm = [], headline = [], automatic = [],
            manualTemp = [], suspend = [], inferred = [], iob = [], cob = [], targetLow = [], targetHigh = [];
        double manualU = 0, manualN = 0, carbG = 0, carbN = 0, exposureMs = 0;
        int analysedDays = 0, glucoseDays = 0;

        for (var d = 0; d < dayCount; d++)
        {
            var i = d * SlotsPerDay + slot;
            if (acc.GlucoseN[i] > 0)
                glucoseDays++;
            if (!acc.Counts(i, acc.Analysed[i]))
                continue;

            analysedDays++;
            exposureMs += acc.Analysed[i];
            manualU += acc.ManualBolusU[i];
            manualN += acc.ManualBolusN[i];
            carbG += acc.CarbG[i];
            carbN += acc.CarbN[i];

            if (acc.Counts(i, acc.ScheduledMs[i]))
                scheduled.Add(acc.ScheduledW[i] / acc.ScheduledMs[i]);
            if (acc.Counts(i, acc.DeliveredMs[i]))
                delivered.Add(acc.DeliveredW[i] / acc.DeliveredMs[i]);
            if (acc.Counts(i, acc.BothMs[i]))
            {
                var algorithmRate = acc.AlgorithmW[i] / acc.BothMs[i];
                var automaticRate = acc.AutomaticBolusU[i] / (acc.Analysed[i] / LocalSlotBinning.HourMs);
                algorithm.Add(algorithmRate);
                automatic.Add(automaticRate);
                headline.Add(algorithmRate + automaticRate);
                manualTemp.Add(acc.ManualTempW[i] / acc.BothMs[i]);
                suspend.Add(acc.SuspendW[i] / acc.BothMs[i]);
                inferred.Add(acc.InferredW[i] / acc.BothMs[i]);
            }
            if (acc.Counts(i, acc.IobCobMs[i]))
            {
                iob.Add(acc.IobW[i] / acc.IobCobMs[i]);
                cob.Add(acc.CobW[i] / acc.IobCobMs[i]);
            }
            if (targetAt?.Invoke(acc.FirstAnalysed[i]) is { } target)
            {
                targetLow.Add(target.Low);
                targetHigh.Add(target.High);
            }
        }

        var standardSlots = exposureMs / SlotMs;
        DailyProfileEventSeries? Events(double total, double count) => analysedDays == 0
            ? null
            : new DailyProfileEventSeries
            {
                MeanPerDay = SlotSummary.Round(total / standardSlots),
                EventsPerDay = SlotSummary.Round(count / standardSlots),
                N = analysedDays,
                Sparse = analysedDays < SlotSummary.SparseDayThreshold,
            };

        return new DailyProfileSlot
        {
            Slot = slot,
            MinuteOfDay = slot * SlotMinutes,
            Glucose = SlotSummary.Of(acc.GlucoseValues[slot], glucoseDays),
            ScheduledBasal = SlotSummary.Of(scheduled),
            DeliveredBasal = SlotSummary.Of(delivered),
            NetLoopCorrection = SlotSummary.Of(algorithm) is { } rate
                ? new NetLoopCorrectionAggregate
                {
                    Mean = SlotSummary.Round(headline.Average()),
                    Rate = rate,
                    AutomaticBolusMean = SlotSummary.Round(automatic.Average()),
                }
                : null,
            ManualTemp = SlotSummary.Of(manualTemp),
            Suspend = SlotSummary.Of(suspend),
            Inferred = SlotSummary.Of(inferred),
            Iob = SlotSummary.Of(iob),
            IobSource = Dominant(acc.IobDeviceMs[slot], acc.IobCalculatedMs[slot]),
            Cob = SlotSummary.Of(cob),
            CobSource = Dominant(acc.CobDeviceMs[slot], acc.CobCalculatedMs[slot]),
            ManualBoluses = Events(manualU, manualN),
            Carbs = Events(carbG, carbN),
            TargetLow = Median(targetLow),
            TargetHigh = Median(targetHigh),
        };
    }

    private static double? Median(List<double> values)
    {
        if (values.Count == 0)
            return null;
        values.Sort();
        return SlotSummary.Round(GlucoseStatistics.Median(values));
    }

    private static IobCobSource? Dominant(double deviceMs, double calculatedMs) =>
        deviceMs == 0 && calculatedMs == 0 ? null
        : deviceMs >= calculatedMs ? IobCobSource.Device
        : IobCobSource.Calculated;

    private static List<LoopAnalysisHourlyValue> Hourly(int dayCount, Accumulators acc)
    {
        var hours = new List<LoopAnalysisHourlyValue>(24);
        for (var hour = 0; hour < 24; hour++)
        {
            var values = new List<double>();
            for (var d = 0; d < dayCount; d++)
            {
                double wall = 0, analysed = 0, both = 0, algorithmW = 0, automaticU = 0;
                for (var s = hour * SlotsPerHour; s < (hour + 1) * SlotsPerHour; s++)
                {
                    var i = d * SlotsPerDay + s;
                    wall += acc.Wall[i];
                    analysed += acc.Analysed[i];
                    both += acc.BothMs[i];
                    algorithmW += acc.AlgorithmW[i];
                    automaticU += acc.AutomaticBolusU[i];
                }
                if (wall > 0 && both >= SlotSummary.MinimumSlotCoverage * wall)
                    values.Add(algorithmW / both + automaticU / (analysed / LocalSlotBinning.HourMs));
            }
            hours.Add(new LoopAnalysisHourlyValue
            {
                Hour = hour,
                Mean = values.Count > 0 ? SlotSummary.Round(values.Average()) : null,
                N = values.Count,
                Sparse = values.Count < SlotSummary.SparseDayThreshold,
            });
        }
        return hours;
    }

    private static List<DailyProfileDay> Days(
        DateOnly firstDay, int dayCount, long[] dayStarts, long from, long to, Accumulators acc, out DailyProfileSummary summary)
    {
        var days = new List<DailyProfileDay>(dayCount);
        double ratioScheduledW = 0, ratioDeltaW = 0, netPerDaySum = 0, netShareU = 0, tddShareU = 0;
        double basalPerDaySum = 0, automaticPerDaySum = 0, manualPerDaySum = 0;
        var totalPerDay = new List<double>();
        int daysWithData = 0, netDays = 0;

        for (var d = 0; d < dayCount; d++)
        {
            double wall = 0, analysed = 0, both = 0, deliveredMs = 0, deliveredW = 0, algorithmW = 0,
                automaticU = 0, manualU = 0, carbG = 0, bothScheduledW = 0, deltaW = 0;
            var glucose = false;
            var coverage = new CoverageBits();
            for (var s = 0; s < SlotsPerDay; s++)
            {
                var i = d * SlotsPerDay + s;
                wall += acc.Wall[i];
                analysed += acc.Analysed[i];
                both += acc.BothMs[i];
                deliveredMs += acc.DeliveredMs[i];
                deliveredW += acc.DeliveredW[i];
                algorithmW += acc.AlgorithmW[i];
                automaticU += acc.AutomaticBolusU[i];
                manualU += acc.ManualBolusU[i];
                carbG += acc.CarbG[i];
                bothScheduledW += acc.BothScheduledW[i];
                deltaW += acc.AlgorithmW[i] + acc.ManualTempW[i] + acc.SuspendW[i] + acc.InferredW[i];
                glucose |= acc.GlucoseN[i] > 0;

                if (!acc.Counts(i, acc.Analysed[i]))
                    continue;
                coverage.Set(CoverageBits.Analysed, s);
                if (acc.GlucoseN[i] > 0) coverage.Set(CoverageBits.Glucose, s);
                if (acc.Counts(i, acc.ScheduledMs[i])) coverage.Set(CoverageBits.Scheduled, s);
                if (acc.Counts(i, acc.DeliveredMs[i])) coverage.Set(CoverageBits.Delivered, s);
                if (acc.Counts(i, acc.BothMs[i])) coverage.Set(CoverageBits.NetLoopCorrection, s);
                if (acc.Counts(i, acc.IobCobMs[i])) coverage.Set(CoverageBits.IobCob, s);
            }

            if (analysed == 0)
                continue;

            ratioScheduledW += bothScheduledW;
            ratioDeltaW += deltaW;

            double? net = both > 0 ? algorithmW / LocalSlotBinning.HourMs + automaticU : null;
            var basal = deliveredW / LocalSlotBinning.HourMs;
            double? total = deliveredMs > 0 ? basal + automaticU + manualU : null;
            var hasTreatments = deliveredMs > 0 || automaticU > 0 || manualU > 0 || carbG > 0;
            if (glucose || hasTreatments)
                daysWithData++;

            var wholeDay = analysed >= InsulinSummaryMinimumCoverage * wall
                && deliveredMs >= InsulinSummaryMinimumCoverage * wall;
            if (wholeDay)
            {
                basalPerDaySum += basal;
                automaticPerDaySum += automaticU;
                manualPerDaySum += manualU;
                totalPerDay.Add(total!.Value);
            }
            if (wholeDay && both >= InsulinSummaryMinimumCoverage * wall)
            {
                netDays++;
                netPerDaySum += net!.Value;
                netShareU += net.Value;
                tddShareU += total!.Value;
            }

            var date = firstDay.AddDays(d);
            days.Add(new DailyProfileDay
            {
                Date = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                DayOfWeek = date.DayOfWeek,
                Start = DateTimeOffset.FromUnixTimeMilliseconds(dayStarts[d]).UtcDateTime,
                Complete = dayStarts[d] >= from && dayStarts[d + 1] <= to,
                Coverage = coverage.ToCoverage(),
                NetLoopCorrection = net is { } n ? SlotSummary.Round(n) : null,
                TotalInsulin = total is { } t ? SlotSummary.Round(t) : null,
                DeliveredBasal = deliveredMs > 0 ? SlotSummary.Round(deliveredW / LocalSlotBinning.HourMs) : null,
                AutomaticBolus = SlotSummary.Round(automaticU),
                ManualBolus = SlotSummary.Round(manualU),
                HasGlucose = glucose,
                HasTreatments = hasTreatments,
            });
        }

        summary = new DailyProfileSummary
        {
            DaysWithData = daysWithData,
            DeliveredToScheduledRatio = ratioScheduledW > 0
                ? SlotSummary.Round((ratioScheduledW + ratioDeltaW) / ratioScheduledW)
                : null,
            NetLoopCorrectionPerDay = netDays > 0 ? SlotSummary.Round(netPerDaySum / netDays) : null,
            NetLoopCorrectionTddPercent = tddShareU > 0 ? SlotSummary.Round(netShareU / tddShareU * 100) : null,
            NetLoopCorrectionDays = netDays,
            Insulin = InsulinSummary(totalPerDay, basalPerDaySum, automaticPerDaySum, manualPerDaySum),
        };
        return days;
    }

    private static DailyInsulinSummary? InsulinSummary(
        List<double> totalPerDay, double basalSum, double automaticSum, double manualSum)
    {
        if (SlotSummary.Of(totalPerDay) is not { } total)
            return null;
        var days = total.N;
        var all = basalSum + automaticSum + manualSum;
        double Percent(double part) => all > 0 ? SlotSummary.Round(part / all * 100) : 0;
        return new DailyInsulinSummary
        {
            Total = total,
            DeliveredBasal = SlotSummary.Round(basalSum / days),
            AutomaticBolus = SlotSummary.Round(automaticSum / days),
            ManualBolus = SlotSummary.Round(manualSum / days),
            Bolus = SlotSummary.Round((automaticSum + manualSum) / days),
            DeliveredBasalPercent = Percent(basalSum),
            BolusPercent = Percent(automaticSum + manualSum),
            AutomaticBolusPercent = Percent(automaticSum),
            ManualBolusPercent = Percent(manualSum),
        };
    }

    private sealed class Accumulators(int size)
    {
        public readonly double[] Wall = new double[size];
        public readonly double[] Analysed = new double[size];
        public readonly long[] FirstAnalysed = new long[size];
        public readonly double[] ScheduledW = new double[size];
        public readonly double[] ScheduledMs = new double[size];
        public readonly double[] DeliveredW = new double[size];
        public readonly double[] DeliveredMs = new double[size];
        public readonly double[] BothMs = new double[size];
        public readonly double[] BothScheduledW = new double[size];
        public readonly double[] AlgorithmW = new double[size];
        public readonly double[] ManualTempW = new double[size];
        public readonly double[] SuspendW = new double[size];
        public readonly double[] InferredW = new double[size];
        public readonly double[] AutomaticBolusU = new double[size];
        public readonly double[] ManualBolusU = new double[size];
        public readonly int[] ManualBolusN = new int[size];
        public readonly double[] CarbG = new double[size];
        public readonly int[] CarbN = new int[size];
        public readonly double[] IobW = new double[size];
        public readonly double[] CobW = new double[size];
        public readonly double[] IobCobMs = new double[size];
        public readonly int[] GlucoseN = new int[size];

        public readonly double[] IobDeviceMs = new double[SlotsPerDay];
        public readonly double[] IobCalculatedMs = new double[SlotsPerDay];
        public readonly double[] CobDeviceMs = new double[SlotsPerDay];
        public readonly double[] CobCalculatedMs = new double[SlotsPerDay];
        public readonly List<double>[] GlucoseValues = [.. Enumerable.Range(0, SlotsPerDay).Select(_ => new List<double>())];

        /// <summary>Whether <paramref name="coveredMs"/> is enough of slot-day <paramref name="i"/> to count.</summary>
        public bool Counts(int i, double coveredMs) =>
            Wall[i] > 0 && coveredMs > 0 && coveredMs >= SlotSummary.MinimumSlotCoverage * Wall[i];
    }

    private sealed class CoverageBits
    {
        public const int Analysed = 0, Glucose = 1, Scheduled = 2, Delivered = 3, NetLoopCorrection = 4, IobCob = 5;

        private readonly byte[][] _bits = [.. Enumerable.Range(0, 6).Select(_ => new byte[SlotsPerDay / 8])];

        public void Set(int series, int slot) => _bits[series][slot / 8] |= (byte)(1 << (slot % 8));

        public DailyProfileDayCoverage ToCoverage() => new()
        {
            Analysed = Convert.ToBase64String(_bits[Analysed]),
            Glucose = Convert.ToBase64String(_bits[Glucose]),
            ScheduledBasal = Convert.ToBase64String(_bits[Scheduled]),
            DeliveredBasal = Convert.ToBase64String(_bits[Delivered]),
            NetLoopCorrection = Convert.ToBase64String(_bits[NetLoopCorrection]),
            IobCob = Convert.ToBase64String(_bits[IobCob]),
        };
    }
}
