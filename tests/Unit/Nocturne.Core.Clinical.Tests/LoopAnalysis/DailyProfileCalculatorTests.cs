using Nocturne.Core.Clinical.LoopAnalysis;
using Nocturne.Core.Clinical.Treatments;
using Nocturne.Core.Models.LoopAnalysis;
using Nocturne.Core.Models.V4;

namespace Nocturne.Core.Clinical.Tests.LoopAnalysis;

public class DailyProfileCalculatorTests
{
    private static readonly TimeZoneInfo Sydney = TimeZoneInfo.FindSystemTimeZoneById("Australia/Sydney");
    private static readonly TimeZoneInfo Darwin = TimeZoneInfo.FindSystemTimeZoneById("Australia/Darwin");
    private const long Minute = 60_000;
    private const long Hour = 60 * Minute;

    private static long Utc(int y, int mo, int d, int h, int mi = 0) =>
        new DateTimeOffset(y, mo, d, h, mi, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

    private static long Local(TimeZoneInfo tz, int y, int mo, int d, int h, int mi = 0) =>
        new DateTimeOffset(new DateTime(y, mo, d, h, mi, 0), tz.GetUtcOffset(new DateTime(y, mo, d, h, mi, 0)))
            .ToUnixTimeMilliseconds();

    private static int SlotAt(int hour, int minute) => (hour * 60 + minute) / 5;

    private static DailyProfileRange Calculate(
        TimeZoneInfo tz,
        long from,
        long to,
        IEnumerable<DeliveredBasalSegment>? delivered = null,
        IEnumerable<RateSegment>? scheduled = null,
        IEnumerable<InsulinDose>? doses = null,
        IEnumerable<GlucoseSample>? glucose = null,
        IEnumerable<CarbEvent>? carbs = null,
        IEnumerable<IobCobTick>? iobCob = null,
        LoopAnalysisMasks? filter = null,
        LoopAnalysisMaskSources? sources = null,
        IntervalSet? calculatedIobCobTime = null,
        Func<long, (double Low, double High)?>? targetAt = null,
        long? historyVisibleFrom = null)
    {
        var inputs = new DailyProfileInputs
        {
            Zone = tz,
            Insulin = new NetInsulinAboveSchedule(delivered ?? [], scheduled ?? [], doses ?? []),
            Glucose = [.. glucose ?? []],
            Carbs = [.. carbs ?? []],
            IobCob = [.. iobCob ?? []],
            CalculatedIobCobTime = calculatedIobCobTime ?? IntervalSet.Empty,
            TargetAt = targetAt,
            HistoryVisibleFrom = historyVisibleFrom,
        };
        var mask = LoopAnalysisMask.Build(filter ?? new LoopAnalysisMasks(), from, to, tz, sources ?? new LoopAnalysisMaskSources());
        return DailyProfileCalculator.Calculate(inputs, mask, from, to);
    }

    private static IEnumerable<GlucoseSample> EveryFiveMinutes(long from, long to, double mgdl)
    {
        for (var t = from; t < to; t += 5 * Minute)
            yield return new GlucoseSample(t, mgdl);
    }

    [Fact]
    public void FallBackDay_RepeatedSlotsAreDurationNormalised()
    {
        // 2024-04-07 in Sydney is 25 hours long: 03:00 AEDT falls back to 02:00 AEST.
        var from = Utc(2024, 4, 6, 13);
        var to = Utc(2024, 4, 7, 14);
        var firstPass = Utc(2024, 4, 6, 15);
        var secondPass = Utc(2024, 4, 6, 16);

        var result = Calculate(Sydney, from, to,
            delivered: [new(from, to, 2.0, BasalDeliveryOrigin.Algorithm)],
            scheduled: [new(from, to, 1.0)],
            doses: [new(firstPass + Minute, 1, false), new(secondPass + Minute, 1, false)],
            glucose: EveryFiveMinutes(from, to, 120));

        var repeated = result.Slots[SlotAt(2, 0)];
        repeated.NetLoopCorrection!.Mean.Should().Be(1.0);
        repeated.NetLoopCorrection.Rate.N.Should().Be(1);
        repeated.DeliveredBasal!.P50.Should().Be(2.0);
        repeated.Glucose!.N.Should().Be(2, "both passes through 02:00-02:05 are pooled");
        repeated.ManualBoluses!.EventsPerDay.Should().Be(1.0, "two boluses over ten minutes is one per five-minute slot");
        repeated.ManualBoluses.MeanPerDay.Should().Be(1.0);

        result.Slots[SlotAt(1, 55)].Glucose!.N.Should().Be(1);

        var day = result.Days.Should().ContainSingle().Which;
        day.Date.Should().Be("2024-04-07");
        day.NetLoopCorrection.Should().Be(25.0, "the day is 25 hours of +1 U/h");
        result.Summary.NetLoopCorrectionPerDay.Should().Be(25.0);
        result.Summary.DeliveredToScheduledRatio.Should().Be(2.0);
    }

    [Fact]
    public void SpringForwardDay_SkippedSlotsContributeNothing()
    {
        // 2024-10-06 in Sydney is 23 hours long: 02:00 AEST springs to 03:00 AEDT.
        var from = Utc(2024, 10, 5, 14);
        var to = Utc(2024, 10, 6, 13);

        var result = Calculate(Sydney, from, to,
            delivered: [new(from, to, 1.5, BasalDeliveryOrigin.Algorithm)],
            scheduled: [new(from, to, 1.0)],
            glucose: EveryFiveMinutes(from, to, 110));

        for (var slot = SlotAt(2, 0); slot < SlotAt(3, 0); slot++)
        {
            result.Slots[slot].DeliveredBasal.Should().BeNull();
            result.Slots[slot].NetLoopCorrection.Should().BeNull();
            result.Slots[slot].Glucose.Should().BeNull();
        }
        result.Slots[SlotAt(1, 55)].DeliveredBasal!.N.Should().Be(1);
        result.Slots[SlotAt(3, 0)].DeliveredBasal!.N.Should().Be(1);

        var day = result.Days.Should().ContainSingle().Which;
        day.NetLoopCorrection.Should().Be(11.5, "the day is 23 hours of +0.5 U/h");
        Bit(day.Coverage.DeliveredBasal!, SlotAt(2, 30)).Should().BeFalse();
        Bit(day.Coverage.DeliveredBasal!, SlotAt(3, 0)).Should().BeTrue();
    }

    [Fact]
    public void HalfHourZone_SlotsFollowTheLocalClock()
    {
        // Darwin is UTC+9:30, so 00:00Z is 09:30 local.
        var from = Local(Darwin, 2024, 6, 3, 0);
        var to = Local(Darwin, 2024, 6, 4, 0);

        var result = Calculate(Darwin, from, to,
            delivered: [new(Utc(2024, 6, 3, 0), Utc(2024, 6, 3, 0, 30), 2.0, BasalDeliveryOrigin.Algorithm)],
            scheduled: [new(from, to, 1.0)]);

        result.Slots[SlotAt(9, 25)].DeliveredBasal.Should().BeNull();
        for (var slot = SlotAt(9, 30); slot < SlotAt(10, 0); slot++)
            result.Slots[slot].DeliveredBasal!.P50.Should().Be(2.0);
        result.Slots[SlotAt(10, 0)].DeliveredBasal.Should().BeNull();
        result.Slots[SlotAt(9, 30)].ScheduledBasal!.P50.Should().Be(1.0);
    }

    [Fact]
    public void ReadingOnASlotBoundary_BelongsToTheSlotItStarts()
    {
        var from = Local(Darwin, 2024, 6, 3, 0);
        var to = Local(Darwin, 2024, 6, 4, 0);

        var result = Calculate(Darwin, from, to,
            glucose: [new(Local(Darwin, 2024, 6, 3, 10, 5), 150)]);

        result.Slots[SlotAt(10, 0)].Glucose.Should().BeNull();
        result.Slots[SlotAt(10, 5)].Glucose!.P50.Should().Be(150);
        result.Slots[SlotAt(10, 5)].Glucose!.N.Should().Be(1);
    }

    [Fact]
    public void AllZeroSlot_IsAZeroNotAGap()
    {
        var from = Local(Darwin, 2024, 6, 3, 0);
        var to = Local(Darwin, 2024, 6, 8, 0);
        var suspends = Enumerable.Range(0, 5).Select(d =>
            new DeliveredBasalSegment(from + d * 24 * Hour + 10 * Hour, from + d * 24 * Hour + 11 * Hour, 0, BasalDeliveryOrigin.Suspended));

        var result = Calculate(Darwin, from, to, delivered: suspends, scheduled: [new(from, to, 1.0)]);

        var slot = result.Slots[SlotAt(10, 30)];
        slot.DeliveredBasal!.P50.Should().Be(0);
        slot.DeliveredBasal.Mean.Should().Be(0);
        slot.DeliveredBasal.N.Should().Be(5);
        slot.DeliveredBasal.Sparse.Should().BeFalse();
        slot.Suspend!.P50.Should().Be(-1.0);
        slot.NetLoopCorrection!.Mean.Should().Be(0, "a suspend is not the algorithm's correction");
    }

    [Fact]
    public void Gap_IsAbsentNotZero()
    {
        var from = Local(Darwin, 2024, 6, 3, 0);
        var to = Local(Darwin, 2024, 6, 4, 0);

        var result = Calculate(Darwin, from, to,
            delivered: [new(from, from + 10 * Hour, 1.0, BasalDeliveryOrigin.Inferred)],
            scheduled: [new(from, to, 1.0)],
            glucose: EveryFiveMinutes(from, from + 10 * Hour, 100));

        var gap = result.Slots[SlotAt(12, 0)];
        gap.DeliveredBasal.Should().BeNull();
        gap.NetLoopCorrection.Should().BeNull();
        gap.Glucose.Should().BeNull();
        gap.ScheduledBasal!.P50.Should().Be(1.0, "the schedule is known through the gap");

        result.Slots[SlotAt(9, 55)].NetLoopCorrection!.Mean.Should().Be(0);
        result.HourlyNetLoopCorrection[12].Mean.Should().BeNull();
        result.HourlyNetLoopCorrection[12].N.Should().Be(0);
    }

    [Fact]
    public void AutomaticBoluses_CountTowardNetLoopCorrectionAndNotManualBoluses()
    {
        var from = Local(Darwin, 2024, 6, 3, 0);
        var to = Local(Darwin, 2024, 6, 4, 0);
        var at = Local(Darwin, 2024, 6, 3, 10, 2);

        var result = Calculate(Darwin, from, to,
            delivered: [new(from, to, 1.0, BasalDeliveryOrigin.Algorithm)],
            scheduled: [new(from, to, 1.0)],
            doses: [new(at, 0.5, true)]);

        var slot = result.Slots[SlotAt(10, 0)];
        slot.NetLoopCorrection!.AutomaticBolusMean.Should().Be(6.0, "0.5 U over five minutes is 6 U/h");
        slot.NetLoopCorrection.Mean.Should().Be(6.0);
        slot.NetLoopCorrection.Rate.P50.Should().Be(0, "the percentiles are on the temp delta alone");
        slot.ManualBoluses!.MeanPerDay.Should().Be(0);
        result.HourlyNetLoopCorrection[10].Mean.Should().Be(0.5, "0.5 U in the hour");
        result.Days.Single().NetLoopCorrection.Should().Be(0.5);
    }

    [Fact]
    public void ManualTempAndSuspend_StayOutOfNetLoopCorrection()
    {
        var from = Local(Darwin, 2024, 6, 3, 0);
        var to = Local(Darwin, 2024, 6, 4, 0);
        var ten = Local(Darwin, 2024, 6, 3, 10);

        var result = Calculate(Darwin, from, to,
            delivered:
            [
                new(ten, ten + 30 * Minute, 3.0, BasalDeliveryOrigin.Manual),
                new(ten + 30 * Minute, ten + Hour, 0, BasalDeliveryOrigin.Suspended),
            ],
            scheduled: [new(from, to, 1.0)]);

        result.Slots[SlotAt(10, 0)].NetLoopCorrection!.Mean.Should().Be(0);
        result.Slots[SlotAt(10, 0)].ManualTemp!.P50.Should().Be(2.0);
        result.Slots[SlotAt(10, 30)].NetLoopCorrection!.Mean.Should().Be(0);
        result.Slots[SlotAt(10, 30)].Suspend!.P50.Should().Be(-1.0);
        result.Days.Single().NetLoopCorrection.Should().Be(0);
    }

    [Theory]
    [InlineData(2, true)]
    [InlineData(3, false)]
    public void PartiallyMaskedSlot_CountsOnlyWhenHalfIsAnalysed(int maskedMinutes, bool counts)
    {
        var from = Local(Darwin, 2024, 6, 3, 0);
        var to = Local(Darwin, 2024, 6, 4, 0);
        var ten = Local(Darwin, 2024, 6, 3, 10);

        var result = Calculate(Darwin, from, to,
            delivered:
            [
                new(ten, ten + maskedMinutes * Minute, 5.0, BasalDeliveryOrigin.Algorithm),
                new(ten + maskedMinutes * Minute, to, 2.0, BasalDeliveryOrigin.Algorithm),
            ],
            scheduled: [new(from, to, 1.0)],
            filter: new LoopAnalysisMasks { ExcludeOverrides = true },
            sources: new LoopAnalysisMaskSources { Overrides = [new(ten, ten + maskedMinutes * Minute)] });

        var slot = result.Slots[SlotAt(10, 0)];
        if (counts)
        {
            slot.DeliveredBasal!.P50.Should().Be(2.0, "the masked minutes and their 5 U/h are left out");
            slot.NetLoopCorrection!.Mean.Should().Be(1.0);
        }
        else
        {
            slot.DeliveredBasal.Should().BeNull();
            slot.NetLoopCorrection.Should().BeNull();
        }
        Bit(result.Days.Single().Coverage.Analysed, SlotAt(10, 0)).Should().Be(counts);
    }

    [Fact]
    public void DataExclusion_RemovesGlucoseButNotInsulin()
    {
        var from = Local(Darwin, 2024, 6, 3, 0);
        var to = Local(Darwin, 2024, 6, 4, 0);
        var three = Local(Darwin, 2024, 6, 3, 3);

        var result = Calculate(Darwin, from, to,
            delivered: [new(from, to, 1.0, BasalDeliveryOrigin.Algorithm)],
            scheduled: [new(from, to, 1.0)],
            glucose: EveryFiveMinutes(from, to, 60),
            filter: new LoopAnalysisMasks { ExcludeIllness = false, ExcludeOpenLoop = false },
            sources: new LoopAnalysisMaskSources { DataExclusions = [new(three, three + 30 * Minute)] });

        result.Slots[SlotAt(3, 0)].Glucose.Should().BeNull();
        result.Slots[SlotAt(3, 30)].Glucose!.N.Should().Be(1);
        result.Slots[SlotAt(3, 0)].DeliveredBasal!.P50.Should().Be(1.0);
    }

    [Fact]
    public void IobCob_TakesTheDominantSourcePerSlot()
    {
        var from = Local(Darwin, 2024, 6, 3, 0);
        var to = Local(Darwin, 2024, 6, 4, 0);
        var ticks = new List<IobCobTick>();
        for (var t = from; t < to; t += 5 * Minute)
            ticks.Add(new IobCobTick(t, 1.5, t < from + 12 * Hour ? IobCobSource.Device : IobCobSource.Calculated, 0, IobCobSource.Calculated));

        var result = Calculate(Darwin, from, to, iobCob: ticks, calculatedIobCobTime: IntervalSet.Of(from, to));

        result.Slots[SlotAt(6, 0)].Iob!.P50.Should().Be(1.5);
        result.Slots[SlotAt(6, 0)].IobSource.Should().Be(IobCobSource.Device);
        result.Slots[SlotAt(18, 0)].IobSource.Should().Be(IobCobSource.Calculated);
        result.Slots[SlotAt(18, 0)].Cob!.P50.Should().Be(0, "zero COB is a value");
    }

    [Fact]
    public void Glucose_IsSparseBelowTheDayThreshold()
    {
        var from = Local(Darwin, 2024, 6, 3, 0);
        var to = Local(Darwin, 2024, 6, 3 + SlotSummary.SparseDayThreshold, 0);

        var result = Calculate(Darwin, from, to, glucose: EveryFiveMinutes(from, from + 24 * Hour, 100)
            .Concat(EveryFiveMinutes(from + 24 * Hour, to, 200)));

        var slot = result.Slots[0].Glucose!;
        slot.N.Should().Be(SlotSummary.SparseDayThreshold);
        slot.Sparse.Should().BeFalse();
        slot.P10.Should().Be(140);
        slot.P50.Should().Be(200);
        result.Summary.DaysWithData.Should().Be(SlotSummary.SparseDayThreshold);
    }

    [Fact]
    public void WeekdayFilter_KeepsOnlyThoseLocalDays()
    {
        // 2024-06-03 is a Monday.
        var from = Local(Darwin, 2024, 6, 3, 0);
        var to = Local(Darwin, 2024, 6, 10, 0);

        var result = Calculate(Darwin, from, to,
            glucose: EveryFiveMinutes(from, to, 100),
            filter: new LoopAnalysisMasks { Days = [DayOfWeek.Monday, DayOfWeek.Wednesday] });

        result.Days.Select(d => d.Date).Should().Equal("2024-06-03", "2024-06-05");
        result.Slots[0].Glucose!.N.Should().Be(2);
    }

    [Fact]
    public void CalculatedIobCob_WithNothingRecorded_IsAbsentNotZero()
    {
        var from = Local(Darwin, 2024, 6, 3, 0);
        var to = Local(Darwin, 2024, 6, 4, 0);
        var ticks = new List<IobCobTick>();
        for (var t = from; t < to; t += 5 * Minute)
            ticks.Add(new IobCobTick(t, 0, IobCobSource.Calculated, 0, IobCobSource.Calculated));

        var result = Calculate(Darwin, from, to, iobCob: ticks, calculatedIobCobTime: IntervalSet.Of(from, from + 6 * Hour));

        result.Slots[SlotAt(3, 0)].Iob!.P50.Should().Be(0);
        result.Slots[SlotAt(12, 0)].Iob.Should().BeNull();
        result.Slots[SlotAt(12, 0)].Cob.Should().BeNull();
    }

    [Fact]
    public void ATickWithDeviceCobButCalculatedIob_StandsOnlyWhereACalculatedValueWould()
    {
        var from = Local(Darwin, 2024, 6, 3, 0);
        var to = Local(Darwin, 2024, 6, 4, 0);
        var ticks = new List<IobCobTick>();
        for (var t = from; t < to; t += 5 * Minute)
            ticks.Add(t < from + 12 * Hour
                ? new IobCobTick(t, 0, IobCobSource.Calculated, 4, IobCobSource.Device)
                : new IobCobTick(t, 0.5, IobCobSource.Device, 4, IobCobSource.Device));

        var result = Calculate(Darwin, from, to, iobCob: ticks, calculatedIobCobTime: IntervalSet.Of(from, from + 6 * Hour));

        result.Slots[SlotAt(3, 0)].Iob!.P50.Should().Be(0, "a calculated zero is a value where a dose is still acting");
        result.Slots[SlotAt(9, 0)].Iob.Should().BeNull("the device's COB does not make a calculated zero IOB a value");
        result.Slots[SlotAt(9, 0)].Cob.Should().BeNull();
        result.Slots[SlotAt(18, 0)].Iob!.P50.Should().Be(0.5);
    }

    [Fact]
    public void ForAHistoryClampedCaller_CalculatedIobCob_IsAbsentWhileHiddenDosesCanStillAct()
    {
        var from = Local(Darwin, 2024, 6, 3, 0);
        var to = Local(Darwin, 2024, 6, 4, 0);
        var ticks = new List<IobCobTick>();
        for (var t = from; t < to; t += 5 * Minute)
            ticks.Add(t < from + 2 * Hour
                ? new IobCobTick(t, 2, IobCobSource.Device, 10, IobCobSource.Device)
                : new IobCobTick(t, 1, IobCobSource.Calculated, 5, IobCobSource.Calculated));

        var result = Calculate(Darwin, from, to, iobCob: ticks, calculatedIobCobTime: IntervalSet.Of(from, to),
            historyVisibleFrom: from);

        result.Slots[SlotAt(1, 0)].Iob!.P50.Should().Be(2, "the device's own IOB counts the hidden doses");
        result.Slots[SlotAt(7, 55)].Iob.Should().BeNull("a dose hidden before the edge can still act until eight hours after it");
        result.Slots[SlotAt(7, 55)].Cob.Should().BeNull();
        result.Slots[SlotAt(8, 0)].Iob!.P50.Should().Be(1);
        result.Slots[SlotAt(8, 0)].Cob!.P50.Should().Be(5);
    }

    [Fact]
    public void DayDates_AreGregorian_WhateverTheProcessCulture()
    {
        var from = Local(Darwin, 2024, 6, 3, 0);
        var to = Local(Darwin, 2024, 6, 4, 0);
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("th-TH");

            Calculate(Darwin, from, to, glucose: EveryFiveMinutes(from, to, 100))
                .Days.Single().Date.Should().Be("2024-06-03");
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = culture;
        }
    }

    [Fact]
    public void DaysWithNoPumpRecords_AreNotFilledWithTheSchedule()
    {
        // The reviewer's probe: one day of +1 U/h, then nine days with no record at all, which the
        // series builder fills from the schedule.
        var from = Utc(2024, 6, 9, 14);
        var to = from + 10 * 24 * Hour;
        var at = DateTimeOffset.FromUnixTimeMilliseconds(from).UtcDateTime;
        var (_, evidence) = NetInsulinAboveSchedule.ClipTempBasals(
        [
            new TempBasal { StartTimestamp = at, EndTimestamp = at.AddDays(1), Rate = 2, Origin = TempBasalOrigin.Algorithm, Device = "p" },
        ], to);
        var delivered = NetInsulinAboveSchedule.DeliveredFromSeries(
        [
            new BasalPoint { Timestamp = from, Rate = 2, Origin = BasalDeliveryOrigin.Algorithm },
            new BasalPoint { Timestamp = from + 24 * Hour, Rate = 1, Origin = BasalDeliveryOrigin.Inferred },
        ], to, evidence);

        var result = Calculate(Sydney, from, to, delivered: delivered, scheduled: [new(from, to, 1.0)]);

        result.Summary.DaysWithData.Should().Be(1);
        result.Summary.NetLoopCorrectionDays.Should().Be(1);
        result.Summary.NetLoopCorrectionPerDay.Should().Be(24);
        result.Slots[120].NetLoopCorrection!.Rate.N.Should().Be(1);
        result.Slots[120].NetLoopCorrection!.Rate.P50.Should().Be(1);
        result.Slots[120].DeliveredBasal!.N.Should().Be(1);
    }

    [Fact]
    public void ALongStretchWithoutTempRecords_IsAbsentHoweverDenseTheApsSnapshots()
    {
        // A day of temps, 47 days of APS snapshots (the loop ran) with no temp record, then a day
        // of temps. The series fills the stretch from the schedule.
        var from = Local(Darwin, 2024, 6, 1, 0);
        var stretchStart = from + 24 * Hour;
        var stretchEnd = stretchStart + 47 * 24 * Hour;
        var to = stretchEnd + 24 * Hour;
        var temps = new List<TempBasal>();
        foreach (var dayStart in new[] { from, stretchEnd })
            for (var t = dayStart; t < dayStart + 24 * Hour; t += 5 * Minute)
            {
                var at = DateTimeOffset.FromUnixTimeMilliseconds(t).UtcDateTime;
                temps.Add(new TempBasal { StartTimestamp = at, EndTimestamp = at.AddMinutes(5), Rate = 1.5, Origin = TempBasalOrigin.Algorithm, Device = "p" });
            }
        var (clipped, evidence) = NetInsulinAboveSchedule.ClipTempBasals(temps, to);
        var series = clipped.Select(t => new BasalPoint { Timestamp = t.StartMills, Rate = 1.5, Origin = BasalDeliveryOrigin.Algorithm })
            .Append(new BasalPoint { Timestamp = stretchStart, Rate = 1, Origin = BasalDeliveryOrigin.Inferred });
        var snapshots = new List<IobCobTick>();
        for (var t = stretchStart; t < stretchEnd; t += 5 * Minute)
            snapshots.Add(new IobCobTick(t, 1.2, IobCobSource.Device, 10, IobCobSource.Device));

        var result = Calculate(Darwin, from, to,
            delivered: NetInsulinAboveSchedule.DeliveredFromSeries(series, to, evidence),
            scheduled: [new(from, to, 1.0)],
            iobCob: snapshots);

        result.Summary.NetLoopCorrectionDays.Should().Be(2);
        result.Summary.Insulin!.Total.N.Should().Be(2);
        result.Slots[SlotAt(10, 0)].DeliveredBasal!.N.Should().Be(2, "only the two days of temps carry delivery");
        result.Slots[SlotAt(10, 0)].NetLoopCorrection!.Rate.N.Should().Be(2);
        result.Slots[SlotAt(10, 0)].Iob!.N.Should().Be(47, "the snapshots still report IOB");
        result.Days.Where(d => d.Start >= DateTimeOffset.FromUnixTimeMilliseconds(stretchStart).UtcDateTime
                && d.Start < DateTimeOffset.FromUnixTimeMilliseconds(stretchEnd).UtcDateTime)
            .Should().HaveCount(47).And.OnlyContain(d => d.NetLoopCorrection == null && d.DeliveredBasal == null);
    }

    [Fact]
    public void PreAidTime_IsAbsent()
    {
        var from = Local(Darwin, 2024, 6, 3, 0);
        var to = Local(Darwin, 2024, 6, 13, 0);
        var aidStart = from + 5 * 24 * Hour;
        var temps = new List<TempBasal>();
        for (var t = aidStart; t < to; t += 5 * Minute)
        {
            var at = DateTimeOffset.FromUnixTimeMilliseconds(t).UtcDateTime;
            temps.Add(new TempBasal { StartTimestamp = at, EndTimestamp = at.AddMinutes(30), Rate = 1.5, Origin = TempBasalOrigin.Algorithm, Device = "p" });
        }
        var (clipped, evidence) = NetInsulinAboveSchedule.ClipTempBasals(temps, to);
        var series = new List<BasalPoint> { new() { Timestamp = from, Rate = 1, Origin = BasalDeliveryOrigin.Inferred } };
        series.AddRange(clipped.Select(t => new BasalPoint { Timestamp = t.StartMills, Rate = 1.5, Origin = BasalDeliveryOrigin.Algorithm }));

        var result = Calculate(Darwin, from, to,
            delivered: NetInsulinAboveSchedule.DeliveredFromSeries(series, to, evidence),
            scheduled: [new(from, to, 1.0)]);

        result.Summary.DaysWithData.Should().Be(5);
        result.Slots[SlotAt(10, 0)].NetLoopCorrection!.Rate.N.Should().Be(5);
        result.Slots[SlotAt(10, 0)].NetLoopCorrection!.Rate.P50.Should().Be(0.5);
        result.Summary.NetLoopCorrectionPerDay.Should().Be(12);
    }

    [Fact]
    public void ExactlyHalfAnalysedSlot_Counts()
    {
        var from = Local(Darwin, 2024, 6, 3, 0);
        var to = Local(Darwin, 2024, 6, 4, 0);
        var ten = Local(Darwin, 2024, 6, 3, 10);

        var result = Calculate(Darwin, from, to,
            delivered: [new(from, to, 2.0, BasalDeliveryOrigin.Algorithm)],
            scheduled: [new(from, to, 1.0)],
            filter: new LoopAnalysisMasks { ExcludeOverrides = true },
            sources: new LoopAnalysisMaskSources { Overrides = [new(ten, ten + 150_000)] });

        result.Slots[SlotAt(10, 0)].NetLoopCorrection!.Mean.Should().Be(1.0, "2.5 of 5 minutes is half the slot");
    }

    [Fact]
    public void Target_IsTheMedianAcrossDaysOfTheRangeInForce()
    {
        var from = Local(Darwin, 2024, 6, 3, 0);
        var to = Local(Darwin, 2024, 6, 6, 0);
        var temporaryTargetDay = from + 24 * Hour;

        var result = Calculate(Darwin, from, to,
            targetAt: t => t >= temporaryTargetDay && t < temporaryTargetDay + 24 * Hour ? (150, 150) : (90, 140));

        result.Slots[0].TargetLow.Should().Be(90);
        result.Slots[0].TargetHigh.Should().Be(140);
    }

    [Fact]
    public void InsulinSummary_CountsOnlyNearCompleteDaysAndScalesNothing()
    {
        var from = Local(Darwin, 2024, 6, 3, 0);
        var to = Local(Darwin, 2024, 6, 6, 0);
        var secondDay = from + 24 * Hour;
        var thirdDay = from + 48 * Hour;

        var result = Calculate(Darwin, from, to,
            delivered: [new(from, thirdDay + 12 * Hour, 1.0, BasalDeliveryOrigin.Algorithm)],
            scheduled: [new(from, to, 1.0)],
            doses:
            [
                new(from + 3 * Hour, 0.5, true), new(from + 8 * Hour, 3, false),
                new(secondDay + 8 * Hour, 3, false), new(thirdDay + 8 * Hour, 3, false),
            ],
            filter: new LoopAnalysisMasks { ExcludeIllness = true },
            sources: new LoopAnalysisMaskSources { Illness = [new(secondDay + 12 * Hour, secondDay + 24 * Hour)] });

        var insulin = result.Summary.Insulin!;
        insulin.Total.N.Should().Be(1, "the half-masked second day and the half-recorded third are left out, not scaled");
        insulin.Total.Mean.Should().Be(27.5);
        insulin.DeliveredBasal.Should().Be(24);
        insulin.AutomaticBolus.Should().Be(0.5);
        insulin.ManualBolus.Should().Be(3);
        insulin.Bolus.Should().Be(3.5);
        insulin.BolusPercent.Should().Be(12.727, "an automatic bolus is bolus in the split, as Trio and AAPS count it");
        insulin.DeliveredBasalPercent.Should().Be(87.273);
        result.Summary.NetLoopCorrectionDays.Should().Be(1, "net loop correction counts the same whole days");
        result.Summary.NetLoopCorrectionPerDay.Should().Be(0.5);
        result.Summary.NetLoopCorrectionTddPercent.Should().Be(1.818);
        result.Days[0].TotalInsulin.Should().Be(27.5);
        result.Days[1].ManualBolus.Should().Be(3);
        result.Days[2].DeliveredBasal.Should().Be(12);
    }

    private static bool Bit(string? base64, int slot) =>
        (Convert.FromBase64String(base64!)[slot / 8] & (1 << (slot % 8))) != 0;
}
