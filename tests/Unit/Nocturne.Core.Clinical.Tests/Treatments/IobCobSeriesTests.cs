using Moq;
using Nocturne.Core.Clinical.Treatments;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.Core.Clinical.Tests.Treatments;

/// <summary>
/// <see cref="IobCobSeries"/> over the real <see cref="IobCalculator"/>. Its resolvers and the COB
/// calculator are strict mocks with nothing set up, so a tick that reaches past the timeline throws.
/// </summary>
public class IobCobSeriesTests
{
    private const long Minute = 60 * 1000;
    private const long Now = 1_750_000_200_000;

    private static readonly IobCalculator Calculator = new(
        new Mock<ITherapySettingsResolver>(MockBehavior.Strict).Object,
        new Mock<ISensitivityResolver>(MockBehavior.Strict).Object,
        new Mock<IBasalSegmentService>(MockBehavior.Strict).Object,
        new Mock<IApsSnapshotRepository>(MockBehavior.Strict).Object,
        new Mock<IPumpSnapshotRepository>(MockBehavior.Strict).Object,
        new Mock<IStateSpanRepository>(MockBehavior.Strict).Object);

    private static TherapySnapshot Snapshot(double basalRate) => new(
        dia: 3.0,
        peakMinutes: 75,
        carbsPerHour: 30.0,
        timezone: null,
        ccpPercentage: null,
        ccpTimeshiftMs: 0,
        sensitivityEntries: [new ScheduleEntry { TimeAsSeconds = 0, Value = 95.0 }],
        carbRatioEntries: null,
        basalEntries: [new ScheduleEntry { TimeAsSeconds = 0, Value = basalRate }]);

    private static TherapyTimeline Flat(double basalRate) =>
        new([new TherapySegment(Now - 24 * 60 * Minute, Now + 1, Snapshot(basalRate))]);

    private static TempBasal Temp(long start, long? end, double rate, TempBasalOrigin origin = TempBasalOrigin.Algorithm) => new()
    {
        StartTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(start).UtcDateTime,
        EndTimestamp = end is { } e ? DateTimeOffset.FromUnixTimeMilliseconds(e).UtcDateTime : null,
        Rate = rate,
        Origin = origin,
    };

    private static IobCobTick TickAtNow(IEnumerable<Bolus> boluses, IEnumerable<TempBasal> temps, TherapyTimeline timeline) =>
        IobCobSeries.Build(
                Calculator, new Mock<ICobCalculator>(MockBehavior.Strict).Object, boluses, [], temps, timeline, new ScheduledBasal(timeline.BasalSegments(), []), null,
                Now, Now, Now, 5 * Minute)
            .Single();

    [Fact]
    public void SuspendStartedBeforeDiaButStillRunning_CountsIt()
    {
        // Suspended 4 h ago on a 1 U/h schedule and still running at the tick. With DIA 3 h the 35 chunks
        // from 5 to 175 minutes old are on the curve, each -1/12 U: before the 75 min peak
        // sum (1 - 0.001852 k(k+1)) for k = 1..14 = 11.92576, after it
        // sum (0.001323 x^2 - 0.054233 x + 0.55556) for x = 0..20 = 4.07484, so -16.0006 / 12 = -1.333.
        var tick = TickAtNow([], [Temp(Now - 240 * Minute, end: null, rate: 0, TempBasalOrigin.Suspended)], Flat(1.0));

        Assert.Equal(-1.333, tick.Iob, 9);
        Assert.Equal(-1.333, tick.BasalIob);
    }

    [Fact]
    public void ScheduleChangeBetweenChunks_ReadsEachChunksSegment()
    {
        // 1 U/h for 30 minutes. The schedule is 2 U/h in the segment until 20 minutes ago and 1 U/h in the
        // one after, so only the chunks 30 and 25 minutes old withhold 1/12 U:
        // -(0.922216 + 0.94444) / 12 = -0.155555. Read from the tick's segment, every chunk is at the
        // schedule and the tick is 0.
        var switchAt = Now - 20 * Minute;
        var timeline = new TherapyTimeline(
        [
            new TherapySegment(Now - 24 * 60 * Minute, switchAt, Snapshot(2.0)),
            new TherapySegment(switchAt, Now + 1, Snapshot(1.0)),
        ]);

        var tick = TickAtNow([], [Temp(Now - 30 * Minute, Now, rate: 1.0)], timeline);

        Assert.Equal(-0.156, tick.Iob, 9);
    }

    [Fact]
    public void TickCanBeNegative_WhenASuspendOutweighsBoluses()
    {
        // A 0.5 U bolus 30 minutes ago keeps 0.5 * 0.922216 = 0.461108. A suspend over the same 30
        // minutes on a 1 U/h schedule withholds -5.792576 / 12 = -0.482715. The tick is
        // 0.461 - 0.483 = -0.022.
        var bolus = new Bolus { Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(Now - 30 * Minute).UtcDateTime, Insulin = 0.5 };

        var tick = TickAtNow([bolus], [Temp(Now - 30 * Minute, Now, rate: 0, TempBasalOrigin.Suspended)], Flat(1.0));

        Assert.Equal(0.461, tick.BolusIob);
        Assert.Equal(-0.483, tick.BasalIob);
        Assert.Equal(-0.022, tick.Iob, 9);
        Assert.Equal(IobCobSource.Calculated, tick.IobSource);
    }

    private static List<IobCobTick> TwoTicks(DeviceReadings device, long nowMills) =>
        IobCobSeries.Build(
            Calculator, new Mock<ICobCalculator>(MockBehavior.Strict).Object, [], [], null, Flat(1.0), new ScheduledBasal(Flat(1.0).BasalSegments(), []), device,
            Now - 5 * Minute, Now, nowMills, 5 * Minute);

    private static DateTime At(long minutesFromNow) =>
        DateTimeOffset.FromUnixTimeMilliseconds(Now + minutesFromNow * Minute).UtcDateTime;

    [Fact]
    public void OnlyTheTickHoldingNow_TakesASnapshotStampedAfterIt()
    {
        // Now is a minute after the last tick. A snapshot stamped three minutes after it is that
        // tick's device clock running ahead; for the tick five minutes earlier it is later data.
        var device = DeviceReadings.Of([new ApsIobCobPoint(At(3), 2.5, 20)], null);

        var ticks = TwoTicks(device, nowMills: Now + Minute);

        Assert.Equal((0.0, IobCobSource.Calculated), (ticks[0].Iob, ticks[0].IobSource));
        Assert.Equal((2.5, IobCobSource.Device), (ticks[1].Iob, ticks[1].IobSource));
        Assert.Equal((20.0, IobCobSource.Device), (ticks[1].Cob, ticks[1].CobSource));
    }

    [Fact]
    public void APastTick_TakesNoSnapshotStampedAfterIt()
    {
        var device = DeviceReadings.Of([new ApsIobCobPoint(At(3), 2.5, 20)], null);

        var ticks = TwoTicks(device, nowMills: Now + 60 * Minute);

        Assert.All(ticks, tick => Assert.Equal(IobCobSource.Calculated, tick.IobSource));
    }

    [Fact]
    public void ATick_TakesIobFromThePumpAndCobFromTheAps_WhenTheApsReportsNoIob()
    {
        var device = DeviceReadings.Of([new ApsIobCobPoint(At(-2), null, 15)], [new PumpIobPoint(At(-1), 0.9, null)]);

        var tick = TwoTicks(device, nowMills: Now + 60 * Minute)[1];

        Assert.Equal((0.9, IobCobSource.Device), (tick.Iob, tick.IobSource));
        Assert.Equal((15.0, IobCobSource.Device), (tick.Cob, tick.CobSource));
        Assert.Null(tick.BolusIob);
    }

    [Fact]
    public void ABolusWithItsOwnLongerDia_IsInTheSeriesForAsLongAsTheSingleTickCountsIt()
    {
        var bolus = new Bolus
        {
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(Now - 6 * 60 * Minute).UtcDateTime,
            Insulin = 4,
            InsulinContext = new TreatmentInsulinContext { Dia = 7, Peak = 75 },
        };
        var timeline = Flat(1.0);

        var tick = TickAtNow([bolus], [], timeline);

        var single = Calculator.CalcBolus(bolus, timeline.SnapshotAt(Now), Now).IobContrib;
        Assert.True(single > 0);
        Assert.Equal(single, tick.BolusIob!.Value, 3);
    }

    [Fact]
    public void ATempBasalWithItsOwnLongerDia_IsInTheSeriesWhileItsInsulinIsOnBoard()
    {
        var temp = Temp(Now - 380 * Minute, Now - 350 * Minute, rate: 3);
        temp.InsulinContext = new TreatmentInsulinContext { Dia = 7, Peak = 75 };
        var timeline = Flat(1.0);

        var tick = TickAtNow([], [temp], timeline);

        Assert.True(tick.BasalIob > 0);
    }
}
