using Moq;
using Nocturne.Core.Clinical.LoopAnalysis;
using Nocturne.Core.Clinical.Tests.Treatments;
using Nocturne.Core.Clinical.Treatments;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Basal;
using Nocturne.Core.Models.LoopAnalysis;
using Nocturne.Core.Models.V4;

namespace Nocturne.Core.Clinical.Tests.LoopAnalysis;

public class ScheduledBasalTests
{
    private const long Minute = 60_000;
    private const long Hour = 60 * Minute;
    private const long Day = 24 * Hour;
    private static readonly long From = new DateTimeOffset(2024, 6, 3, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
    private static readonly long To = From + Day;
    private static readonly long TempStart = From + 6 * Hour;
    private static readonly long TempEnd = TempStart + 2 * Hour;

    private static readonly IobCalculator Calculator = new(
        Mock.Of<ITherapySettingsResolver>(), Mock.Of<ISensitivityResolver>(), Mock.Of<IBasalSegmentService>(),
        Mock.Of<IApsSnapshotRepository>(), Mock.Of<IPumpSnapshotRepository>(), MockStateSpans.Empty().Object);

    private static TempBasal Temp(double rate, double? scheduledRate) => new()
    {
        StartTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(TempStart).UtcDateTime,
        EndTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(TempEnd).UtcDateTime,
        Rate = rate,
        ScheduledRate = scheduledRate,
        Origin = TempBasalOrigin.Algorithm,
        Device = "pump",
    };

    private static TherapyTimeline Therapy(double basal) =>
        new([new TherapySegment(From - Day, To + Day, new TherapySnapshot(
            3, 75, 30, TimeZoneInfo.Utc, null, 0,
            [new ScheduleEntry { TimeAsSeconds = 0, Value = 40 }],
            [new ScheduleEntry { TimeAsSeconds = 0, Value = 10 }],
            [new ScheduleEntry { TimeAsSeconds = 0, Value = basal }]))]);

    /// <summary>
    /// Insulin that acted over the temp's two hours, with net insulin above schedule and IOB both
    /// built as Loop Analysis builds them.
    /// </summary>
    private static ActedInsulin ActedOverTheTemp(double profile, ScaleFactorInterval[] overrides, TempBasal temp)
    {
        var therapy = Therapy(profile);
        var (clipped, evidence) = NetInsulinAboveSchedule.ClipTempBasals([temp], To);
        var delivered = NetInsulinAboveSchedule.DeliveredFromSeries(
            [new BasalPoint { Timestamp = TempStart, Rate = temp.Rate!.Value, Origin = BasalDeliveryOrigin.Algorithm }],
            TempEnd, evidence);
        var scheduled = new ScheduledBasal(therapy.BasalSegments(), overrides);
        var ticks = IobCobSeries.Build(
            Calculator, Mock.Of<ICobCalculator>(), [], [], [temp], therapy, scheduled, null,
            From, To, To, 5 * Minute);

        var inputs = new SettingsEvidenceInputs
        {
            Series = new DailyProfileInputs
            {
                Zone = TimeZoneInfo.Utc,
                Insulin = new NetInsulinAboveSchedule(delivered, scheduled.Segments(clipped), []),
                IobCob = ticks,
                IobCobIntervalMs = 5 * Minute,
                CalculatedIobCobTime = IntervalSet.Of(From, To),
            },
            Therapy = therapy,
        };
        var mask = LoopAnalysisMask.Build(new LoopAnalysisMasks(), From, To, TimeZoneInfo.Utc, inputs.Sources);
        var (acted, rejected) = new SettingsEvidenceContext(inputs, mask, From, To, listEpisodes: false)
            .InsulinActed(TempStart, TempEnd);
        rejected.Should().BeNull();
        return acted!.Value;
    }

    public static TheoryData<string, double, double, double?> Cases => new()
    {
        // The temp runs 1 U/h above the baseline that acted, 0.5 U/h, for two hours.
        { "an override and no device rate", 0.5, 1.5, null },
        // The device measured its temp against 0.8 U/h, not the profile's 1 U/h.
        { "a device rate unlike the profile", 1.0, 1.8, 0.8 },
        { "both, where the device rate wins", 0.5, 1.8, 0.8 },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void InsulinActed_IsTheDeliveredInsulinAboveTheBaselineThatActed(
        string because, double overrideFactor, double rate, double? deviceRate)
    {
        var acted = ActedOverTheTemp(1.0, [new ScaleFactorInterval(From, To, overrideFactor)], Temp(rate, deviceRate));

        // The same delivery above a profile that is itself the baseline that acted.
        var reference = ActedOverTheTemp(rate - 1, [], Temp(rate, scheduledRate: null));

        acted.Net.Total.Should().BeApproximately(2, 1e-6, because);
        reference.IobAtEnd.Should().BeInRange(0.5, 2, "some of the two units is still on board when the block ends");
        acted.IobAtEnd.Should().BeApproximately(reference.IobAtEnd, 1e-6, because);
        acted.Acted.Should().BeApproximately(reference.Acted, 1e-6, because);
    }

    [Fact]
    public void Segments_ATempsDeviceRateReplacesTheScaledProfileOverTheTemp()
    {
        var temp = Temp(1.8, 0.8);

        var segments = new ScheduledBasal([new BasalSegment(From, To, 1.0, 1.0, "Default")], [new ScaleFactorInterval(From, To, 0.5)])
            .Segments([temp]);

        segments.Should().Equal(
            new RateSegment(From, TempStart, 0.5),
            new RateSegment(TempStart, TempEnd, 0.8),
            new RateSegment(TempEnd, To, 0.5));
    }

    [Fact]
    public void Under_ATempWithNoDeviceRate_ReadsTheScaledProfile()
    {
        var scheduled = new ScheduledBasal(
            [new BasalSegment(From, To, 1.0, 1.0, "Default")], [new ScaleFactorInterval(TempStart, TempEnd, 0.5)]);

        scheduled.Under(Temp(1.5, null), TempStart).Should().Be(0.5);
        scheduled.Under(Temp(1.5, null), TempEnd).Should().Be(1.0);
        scheduled.Under(Temp(1.5, 0.8), TempStart).Should().Be(0.8);
    }

    [Fact]
    public void TheTimelinesSegments_HoldTheRateTheSnapshotReadsAtEveryInstant()
    {
        var sydney = TimeZoneInfo.FindSystemTimeZoneById("Australia/Sydney");
        var timeline = new TherapyTimeline(
        [
            new TherapySegment(From, From + Day, new TherapySnapshot(
                3, 75, 30, sydney, 120, 2 * Hour, null, null,
                [
                    new ScheduleEntry { TimeAsSeconds = 0, Value = 0.8 },
                    new ScheduleEntry { TimeAsSeconds = 6 * 3600, Value = 1.2 },
                    new ScheduleEntry { TimeAsSeconds = 21 * 3600 + 1800, Value = 0.9 },
                ])),
            new TherapySegment(From + Day, To + Day, new TherapySnapshot(3, 75, 30, null, null, 0, null, null, null)),
        ]);
        var scheduled = new ScheduledBasal(timeline.BasalSegments(), []);

        for (var t = From; t < To + Day; t += 7 * Minute)
            scheduled.At(t).Should().BeApproximately(timeline.SnapshotAt(t).BasalRateAt(t), 1e-9, $"at {t}");
    }
}
