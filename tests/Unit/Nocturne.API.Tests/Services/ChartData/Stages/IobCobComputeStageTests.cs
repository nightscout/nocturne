using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.ChartData;
using Nocturne.API.Services.ChartData.Stages;
using Nocturne.API.Services.Treatments;
using Nocturne.Core.Contracts.Analytics;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Tests.Shared.Mocks;
using Xunit;

namespace Nocturne.API.Tests.Services.ChartData.Stages;

public class IobCobComputeStageTests
{
    private readonly Mock<IIobCalculator> _mockIobCalculator = new();
    private readonly Mock<ICobCalculator> _mockCobCalculator = new();
    private readonly Mock<IBasalSeriesBuilder> _mockBasalSeriesBuilder = new();
    private readonly Mock<ITherapyTimelineResolver> _mockTherapyTimelineResolver = new();
    private readonly IMemoryCache _cache = new MemoryCache(new MemoryCacheOptions());
    private readonly IobCobComputeStage _stage;

    // Common test timestamp: 2023-11-15T00:00:00Z in millis
    private const long TestMills = 1700000000000L;

    public IobCobComputeStageTests()
    {
        _mockBasalSeriesBuilder
            .Setup(b => b.BuildAsync(
                It.IsAny<List<TempBasal>>(),
                It.IsAny<long>(),
                It.IsAny<long>(),
                It.IsAny<double>(),
                It.IsAny<TherapyTimeline>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((List<TempBasal> _, long start, long _, double rate, TherapyTimeline _, CancellationToken _) =>
                new List<BasalPoint>
                {
                    new()
                    {
                        Timestamp = start,
                        Rate = rate,
                        ScheduledRate = rate,
                        Origin = BasalDeliveryOrigin.Inferred,
                    },
                });

        var defaultSnapshot = new TherapySnapshot(
            dia: 3.0,
            peakMinutes: TherapySnapshot.DefaultPeakMinutes,
            carbsPerHour: TherapySnapshot.DefaultCarbsPerHour,
            timezone: null,
            ccpPercentage: null,
            ccpTimeshiftMs: 0,
            sensitivityEntries: null,
            carbRatioEntries: null,
            basalEntries: null);
        _mockTherapyTimelineResolver
            .Setup(r => r.BuildAsync(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((long from, long to, string? _, CancellationToken _) =>
                new TherapyTimeline(new[] { new TherapySegment(from, to, defaultSnapshot) }));

        _stage = new IobCobComputeStage(
            _mockIobCalculator.Object,
            _mockCobCalculator.Object,
            _mockBasalSeriesBuilder.Object,
            _mockTherapyTimelineResolver.Object,
            _cache,
            MockTenantAccessor.Create().Object,
            NullLogger<IobCobComputeStage>.Instance
        );
    }

    [Fact]
    public async Task ExecuteAsync_ComputesIobCobAndBasalSeries()
    {
        // Arrange
        var startTime = TestMills;
        var endTime = TestMills + 30 * 60 * 1000; // 30 minutes later
        const int intervalMinutes = 5;

        var bolus = new Bolus
        {
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(TestMills - 60 * 60 * 1000).UtcDateTime, // 1 hour before start
            Insulin = 3.0,
        };

        var carbIntake = new CarbIntake
        {
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(TestMills - 30 * 60 * 1000).UtcDateTime, // 30 minutes before start
            Carbs = 45.0,
        };

        var tempBasal = new TempBasal
        {
            StartTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(startTime).UtcDateTime,
            EndTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(startTime + 30 * 60 * 1000).UtcDateTime,
            Rate = 1.5,
            Origin = TempBasalOrigin.Algorithm,
        };

        _mockIobCalculator
            .Setup(s => s.FromBoluses(It.IsAny<List<Bolus>>(), It.IsAny<TherapySnapshot>(), It.IsAny<long>()))
            .Returns(new IobResult { Iob = 2.0 });

        _mockIobCalculator
            .Setup(s => s.FromTempBasals(It.IsAny<List<TempBasal>>(), It.IsAny<TherapySnapshot>(), It.IsAny<long>()))
            .Returns(new IobResult { BasalIob = 0.5 });

        _mockCobCalculator
            .Setup(s => s.FromCarbIntakes(It.IsAny<List<CarbIntake>>(), It.IsAny<List<Bolus>?>(), It.IsAny<List<TempBasal>?>(), It.IsAny<TherapySnapshot>(), It.IsAny<long>()))
            .Returns(new CobResult { Cob = 20.0 });

        var context = new ChartDataContext
        {
            StartTime = startTime,
            EndTime = endTime,
            IntervalMinutes = intervalMinutes,
            DefaultBasalRate = 1.0,
            BolusList = [bolus],
            CarbIntakeList = [carbIntake],
            TempBasalList = [tempBasal],
        };

        // Act
        var result = await _stage.ExecuteAsync(context, CancellationToken.None);

        // Assert
        result.IobSeries.Should().NotBeEmpty();
        result.CobSeries.Should().NotBeEmpty();
        result.BasalSeries.Should().NotBeEmpty();
        result.MaxIob.Should().BeGreaterThanOrEqualTo(3); // floored at 3
        result.MaxCob.Should().BeGreaterThanOrEqualTo(30); // floored at 30
        result.MaxBasalRate.Should().BeGreaterThan(0);

        // Verify series timestamps are within expected range
        result.IobSeries.Should().AllSatisfy(p => p.Timestamp.Should().BeInRange(startTime, endTime));
        result.CobSeries.Should().AllSatisfy(p => p.Timestamp.Should().BeInRange(startTime, endTime));
    }

    [Fact]
    public async Task ExecuteAsync_WithEmptyData_ReturnsEmptySeries()
    {
        // Arrange
        var startTime = TestMills;
        var endTime = TestMills; // zero-length window produces only a single point
        const int intervalMinutes = 5;
        const double defaultBasalRate = 1.0;

        var context = new ChartDataContext
        {
            StartTime = startTime,
            EndTime = endTime,
            IntervalMinutes = intervalMinutes,
            DefaultBasalRate = defaultBasalRate,
            BolusList = [],
            CarbIntakeList = [],
            TempBasalList = [],
        };

        // Act
        var result = await _stage.ExecuteAsync(context, CancellationToken.None);

        // Assert — IOB/COB calculators should never be called with no data
        _mockIobCalculator.Verify(
            s => s.FromBoluses(It.IsAny<List<Bolus>>(), It.IsAny<TherapySnapshot>(), It.IsAny<long>()),
            Times.Never
        );
        _mockCobCalculator.Verify(
            s => s.FromCarbIntakes(It.IsAny<List<CarbIntake>>(), It.IsAny<List<Bolus>?>(), It.IsAny<List<TempBasal>?>(), It.IsAny<TherapySnapshot>(), It.IsAny<long>()),
            Times.Never
        );

        // Floors still apply even with empty data
        result.MaxIob.Should().Be(3);
        result.MaxCob.Should().Be(30);

        // Basal series falls back to profile-based (produces at least one point)
        result.BasalSeries.Should().NotBeEmpty();
        result.BasalSeries.Should().AllSatisfy(b => b.Rate.Should().Be(defaultBasalRate));

        // IobSeries and CobSeries have exactly one point (start == end)
        result.IobSeries.Should().ContainSingle();
        result.CobSeries.Should().ContainSingle();
        result.IobSeries[0].Value.Should().Be(0);
        result.CobSeries[0].Value.Should().Be(0);
    }

    /// <summary>
    /// Scaling regression for the dashboard-chart hang. With the <em>real</em> IOB and COB
    /// calculators wired into the stage and a wide window full of treatments shaped like erik's
    /// tenant (temp basals with null ScheduledRate and null InsulinContext, plus boluses and
    /// carbs), evaluating every tick must issue <b>zero</b> profile-resolver or device-snapshot
    /// DB calls — every lookup is served from the per-tick in-memory TherapySnapshot.
    /// Before the fix this path made an unbounded number of sync-over-async DB calls
    /// (O(ticks × treatments)), which pinned a thread for minutes.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WideWindowWithNullProfileFields_IssuesNoPerTickDbCalls()
    {
        // 24h window at 5-min resolution → ~288 ticks; treatments every ~15 min with the
        // "connector-populated" shape: null ScheduledRate / null InsulinContext.
        var startTime = TestMills;
        var endTime = TestMills + 24L * 60 * 60 * 1000;
        const int intervalMinutes = 5;

        var tempBasals = new List<TempBasal>();
        var boluses = new List<Bolus>();
        var carbs = new List<CarbIntake>();
        for (long t = startTime - 8L * 60 * 60 * 1000; t < endTime; t += 15 * 60 * 1000)
        {
            tempBasals.Add(new TempBasal
            {
                StartTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(t).UtcDateTime,
                EndTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(t + 15 * 60 * 1000).UtcDateTime,
                Rate = 1.5,
                ScheduledRate = null,
                Origin = TempBasalOrigin.Algorithm,
            });
            boluses.Add(new Bolus
            {
                Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(t).UtcDateTime,
                Insulin = 0.8,
                InsulinContext = null,
            });
        }
        for (long t = startTime; t < endTime; t += 3L * 60 * 60 * 1000)
        {
            carbs.Add(new CarbIntake
            {
                Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(t).UtcDateTime,
                Carbs = 40,
            });
        }

        // Counting resolvers — every one of these must stay at zero invocations.
        var therapySettings = new Mock<ITherapySettingsResolver>(MockBehavior.Strict);
        var sensitivity = new Mock<ISensitivityResolver>(MockBehavior.Strict);
        var basalRate = new Mock<IBasalRateResolver>(MockBehavior.Strict);
        var carbRatio = new Mock<ICarbRatioResolver>(MockBehavior.Strict);
        var apsRepo = new Mock<IApsSnapshotRepository>(MockBehavior.Strict);
        var pumpRepo = new Mock<IPumpSnapshotRepository>(MockBehavior.Strict);

        var realIob = new IobCalculator(
            therapySettings.Object, sensitivity.Object, basalRate.Object, apsRepo.Object, pumpRepo.Object);
        var realCob = new CobCalculator(
            NullLogger<CobCalculator>.Instance, realIob, sensitivity.Object, carbRatio.Object,
            therapySettings.Object, apsRepo.Object);

        // A snapshot with real schedules so the calculators produce non-trivial output.
        var snapshot = new TherapySnapshot(
            dia: 3.0, peakMinutes: 75, carbsPerHour: 30.0, timezone: null, ccpPercentage: null, ccpTimeshiftMs: 0,
            sensitivityEntries: new[] { new ScheduleEntry { TimeAsSeconds = 0, Value = 50.0 } },
            carbRatioEntries: new[] { new ScheduleEntry { TimeAsSeconds = 0, Value = 10.0 } },
            basalEntries: new[] { new ScheduleEntry { TimeAsSeconds = 0, Value = 0.8 } });
        var timelineResolver = new Mock<ITherapyTimelineResolver>();
        timelineResolver
            .Setup(r => r.BuildAsync(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((long from, long to, string? _, CancellationToken _) =>
                new TherapyTimeline(new[] { new TherapySegment(from, to, snapshot) }));
        var basalSeriesBuilder = new Mock<IBasalSeriesBuilder>();
        basalSeriesBuilder
            .Setup(b => b.BuildAsync(It.IsAny<List<TempBasal>>(), It.IsAny<long>(), It.IsAny<long>(), It.IsAny<double>(), It.IsAny<TherapyTimeline>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<BasalPoint>());

        var stage = new IobCobComputeStage(
            realIob, realCob, basalSeriesBuilder.Object, timelineResolver.Object,
            _cache, MockTenantAccessor.Create().Object, NullLogger<IobCobComputeStage>.Instance);

        var context = new ChartDataContext
        {
            StartTime = startTime,
            EndTime = endTime,
            IntervalMinutes = intervalMinutes,
            BufferStartTime = startTime - 8L * 60 * 60 * 1000,
            DefaultBasalRate = 1.0,
            BolusList = boluses,
            CarbIntakeList = carbs,
            TempBasalList = tempBasals,
        };

        // MockBehavior.Strict means any unconfigured async resolver / repo call throws — so the
        // run completing at all proves the tick loop never touched them. The result is non-trivial.
        var result = await stage.ExecuteAsync(context, CancellationToken.None);

        result.IobSeries.Should().NotBeEmpty();
        result.CobSeries.Should().NotBeEmpty();
        result.IobSeries.Should().Contain(p => p.Value > 0, "boluses + temp basals should yield IOB");
        result.CobSeries.Should().Contain(p => p.Value > 0, "carbs should yield COB");
    }
}
