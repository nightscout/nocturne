using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Caching;
using Nocturne.API.Services.ChartData;
using Nocturne.API.Services.ChartData.Stages;
using Nocturne.API.Tests.TestDoubles;
using Nocturne.Core.Clinical.Treatments;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Contracts.Analytics;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Tests.Shared.Mocks;
using Xunit;
using Nocturne.API.Tests.Infrastructure;

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
                It.IsAny<IReadOnlyList<ScaleFactorInterval>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((List<TempBasal> _, long start, long _, double rate, IReadOnlyList<ScaleFactorInterval> _, CancellationToken _) =>
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

        _stage = CreateStage(TestTenantCache.Create(_cache));
    }

    private IobCobComputeStage CreateStage(ITenantCache cache) => new(
        _mockIobCalculator.Object,
        _mockCobCalculator.Object,
        _mockBasalSeriesBuilder.Object,
        _mockTherapyTimelineResolver.Object,
        cache,
        NullLogger<IobCobComputeStage>.Instance
    );

    [Fact]
    public async Task ExecuteAsync_ARunningTempInAWindowEndingAfterNow_DeliversOnlyUntilNow()
    {
        var now = TestMills + 10 * 60 * 1000;
        var running = new TempBasal
        {
            StartTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(TestMills).UtcDateTime,
            Rate = 3.0,
            Origin = TempBasalOrigin.Algorithm,
        };
        var pieceEnds = new List<long>();
        _mockIobCalculator
            .Setup(s => s.FromBoluses(It.IsAny<List<Bolus>>(), It.IsAny<TherapySnapshot>(), It.IsAny<long>()))
            .Returns(new IobResult { Iob = 0 });
        _mockIobCalculator
            .Setup(s => s.FromTempBasals(It.IsAny<List<TempBasal>>(), It.IsAny<TherapySnapshot>(), It.IsAny<Func<TempBasal, long, double>>(), It.IsAny<long>(), It.IsAny<long>()))
            .Callback((List<TempBasal> pieces, TherapySnapshot _, Func<TempBasal, long, double> _, long _, long _) => pieceEnds.AddRange(pieces.Select(p => p.EndMills!.Value)))
            .Returns(new IobResult { BasalIob = 0.1 });

        await _stage.ExecuteAsync(new ChartDataContext
        {
            StartTime = TestMills,
            EndTime = TestMills + 30 * 60 * 1000,
            NowMills = now,
            IntervalMinutes = 5,
            DefaultBasalRate = 1.0,
            TempBasalList = [running],
        }, CancellationToken.None);

        pieceEnds.Should().NotBeEmpty().And.OnlyContain(end => end == now,
            "the chart's basal stops a running temp at now, and so must its IOB");
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
            .Setup(s => s.FromTempBasals(It.IsAny<List<TempBasal>>(), It.IsAny<TherapySnapshot>(), It.IsAny<Func<TempBasal, long, double>>(), It.IsAny<long>(), It.IsAny<long>()))
            .Returns(new IobResult { BasalIob = 0.5 });

        _mockCobCalculator
            .Setup(s => s.FromCarbIntakes(It.IsAny<List<CarbIntake>>(), It.IsAny<List<Bolus>?>(), It.IsAny<List<TempBasal>?>(), It.IsAny<TherapySnapshot>(), It.IsAny<Func<TempBasal, long, double>>(), It.IsAny<long>()))
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
            s => s.FromCarbIntakes(It.IsAny<List<CarbIntake>>(), It.IsAny<List<Bolus>?>(), It.IsAny<List<TempBasal>?>(), It.IsAny<TherapySnapshot>(), It.IsAny<Func<TempBasal, long, double>>(), It.IsAny<long>()),
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
        var basalRate = new Mock<IBasalSegmentService>(MockBehavior.Strict);
        var carbRatio = new Mock<ICarbRatioResolver>(MockBehavior.Strict);
        var apsRepo = new Mock<IApsSnapshotRepository>(MockBehavior.Strict);
        var pumpRepo = new Mock<IPumpSnapshotRepository>(MockBehavior.Strict);

        var realIob = new IobCalculator(
            therapySettings.Object, sensitivity.Object, basalRate.Object, apsRepo.Object, pumpRepo.Object,
            MockStateSpans.Empty().Object);
        var realCob = new CobCalculator(
            NullLogger<CobCalculator>.Instance, realIob, sensitivity.Object, carbRatio.Object,
            therapySettings.Object, apsRepo.Object);

        // A snapshot with real schedules so the calculators produce non-trivial output.
        var snapshot = new TherapySnapshot(
            dia: 3.0, carbsPerHour: 30.0, timezone: null, ccpPercentage: null, ccpTimeshiftMs: 0,
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
            .Setup(b => b.BuildAsync(It.IsAny<List<TempBasal>>(), It.IsAny<long>(), It.IsAny<long>(), It.IsAny<double>(), It.IsAny<IReadOnlyList<ScaleFactorInterval>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<BasalPoint>());

        var stage = new IobCobComputeStage(
            realIob, realCob, basalSeriesBuilder.Object, timelineResolver.Object,
            TestTenantCache.Create(_cache), NullLogger<IobCobComputeStage>.Instance);

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

    [Fact]
    public async Task ExecuteAsync_WithRecentApsSnapshots_PrefersDeviceReportedValues()
    {
        // 30-minute window, one snapshot every 5 minutes covering every tick — the whole series
        // must be the device-reported values, and the local calculators must never run.
        var startTime = TestMills;
        var endTime = TestMills + 30 * 60 * 1000;
        const int intervalMinutes = 5;

        var snapshots = new List<ApsIobCobPoint>();
        for (var t = startTime; t <= endTime; t += 5 * 60 * 1000)
        {
            snapshots.Add(new ApsIobCobPoint(
                DateTimeOffset.FromUnixTimeMilliseconds(t).UtcDateTime, 1.25, 18.0, null));
        }

        var context = new ChartDataContext
        {
            StartTime = startTime,
            EndTime = endTime,
            IntervalMinutes = intervalMinutes,
            DefaultBasalRate = 1.0,
            BolusList = [new Bolus
            {
                Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(startTime).UtcDateTime,
                Insulin = 3.0,
            }],
            CarbIntakeList = [new CarbIntake
            {
                Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(startTime).UtcDateTime,
                Carbs = 45.0,
            }],
            TempBasalList = [],
            ApsSnapshotList = snapshots,
        };

        var result = await _stage.ExecuteAsync(context, CancellationToken.None);

        result.IobSeries.Should().AllSatisfy(p => p.Value.Should().Be(1.25));
        result.CobSeries.Should().AllSatisfy(p => p.Value.Should().Be(18.0));
        _mockIobCalculator.Verify(
            s => s.FromBoluses(It.IsAny<List<Bolus>>(), It.IsAny<TherapySnapshot>(), It.IsAny<long>()),
            Times.Never
        );
        _mockCobCalculator.Verify(
            s => s.FromCarbIntakes(It.IsAny<List<CarbIntake>>(), It.IsAny<List<Bolus>?>(), It.IsAny<List<TempBasal>?>(), It.IsAny<TherapySnapshot>(), It.IsAny<Func<TempBasal, long, double>>(), It.IsAny<long>()),
            Times.Never
        );
    }

    [Fact]
    public async Task ExecuteAsync_SnapshotOlderThanRecencyWindow_FallsBackToComputed()
    {
        // A single snapshot 45 minutes before the window start is stale for every tick, so the
        // series must come from the local calculators.
        var startTime = TestMills;
        var endTime = TestMills + 10 * 60 * 1000;
        const int intervalMinutes = 5;

        _mockIobCalculator
            .Setup(s => s.FromBoluses(It.IsAny<List<Bolus>>(), It.IsAny<TherapySnapshot>(), It.IsAny<long>()))
            .Returns(new IobResult { Iob = 2.0 });
        _mockCobCalculator
            .Setup(s => s.FromCarbIntakes(It.IsAny<List<CarbIntake>>(), It.IsAny<List<Bolus>?>(), It.IsAny<List<TempBasal>?>(), It.IsAny<TherapySnapshot>(), It.IsAny<Func<TempBasal, long, double>>(), It.IsAny<long>()))
            .Returns(new CobResult { Cob = 20.0 });

        var context = new ChartDataContext
        {
            StartTime = startTime,
            EndTime = endTime,
            IntervalMinutes = intervalMinutes,
            DefaultBasalRate = 1.0,
            BolusList = [new Bolus
            {
                Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(startTime - 5 * 60 * 1000).UtcDateTime,
                Insulin = 3.0,
            }],
            CarbIntakeList = [new CarbIntake
            {
                Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(startTime - 5 * 60 * 1000).UtcDateTime,
                Carbs = 45.0,
            }],
            TempBasalList = [],
            ApsSnapshotList = [new ApsIobCobPoint(
                DateTimeOffset.FromUnixTimeMilliseconds(startTime - 45 * 60 * 1000).UtcDateTime, 9.99, 99.0, null)],
        };

        var result = await _stage.ExecuteAsync(context, CancellationToken.None);

        result.IobSeries.Should().AllSatisfy(p => p.Value.Should().Be(2.0));
        result.CobSeries.Should().AllSatisfy(p => p.Value.Should().Be(20.0));
    }

    [Fact]
    public async Task ExecuteAsync_SnapshotWithNullValues_FallsBackPerComponent()
    {
        // A recent snapshot carrying only IOB: the IOB series takes the device value while the
        // COB series still comes from the local calculator.
        var startTime = TestMills;
        var endTime = TestMills;
        const int intervalMinutes = 5;

        _mockCobCalculator
            .Setup(s => s.FromCarbIntakes(It.IsAny<List<CarbIntake>>(), It.IsAny<List<Bolus>?>(), It.IsAny<List<TempBasal>?>(), It.IsAny<TherapySnapshot>(), It.IsAny<Func<TempBasal, long, double>>(), It.IsAny<long>()))
            .Returns(new CobResult { Cob = 20.0 });

        var context = new ChartDataContext
        {
            StartTime = startTime,
            EndTime = endTime,
            IntervalMinutes = intervalMinutes,
            DefaultBasalRate = 1.0,
            BolusList = [],
            CarbIntakeList = [new CarbIntake
            {
                Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(startTime - 5 * 60 * 1000).UtcDateTime,
                Carbs = 45.0,
            }],
            TempBasalList = [],
            ApsSnapshotList = [new ApsIobCobPoint(
                DateTimeOffset.FromUnixTimeMilliseconds(startTime - 60 * 1000).UtcDateTime, 1.5, null, null)],
        };

        var result = await _stage.ExecuteAsync(context, CancellationToken.None);

        result.IobSeries.Should().ContainSingle().Which.Value.Should().Be(1.5);
        result.CobSeries.Should().ContainSingle().Which.Value.Should().Be(20.0);
    }

    [Fact]
    public async Task ExecuteAsync_NewSnapshot_InvalidatesCachedSeries()
    {
        // Same treatments, same window — a snapshot arriving between two requests must change
        // the result rather than replaying the cached series.
        var startTime = TestMills;
        var endTime = TestMills;
        const int intervalMinutes = 5;

        _mockIobCalculator
            .Setup(s => s.FromBoluses(It.IsAny<List<Bolus>>(), It.IsAny<TherapySnapshot>(), It.IsAny<long>()))
            .Returns(new IobResult { Iob = 2.0 });

        var baseContext = new ChartDataContext
        {
            StartTime = startTime,
            EndTime = endTime,
            IntervalMinutes = intervalMinutes,
            DefaultBasalRate = 1.0,
            BolusList = [new Bolus
            {
                Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(startTime - 5 * 60 * 1000).UtcDateTime,
                Insulin = 3.0,
            }],
            CarbIntakeList = [],
            TempBasalList = [],
        };

        var first = await _stage.ExecuteAsync(baseContext, CancellationToken.None);
        first.IobSeries.Should().ContainSingle().Which.Value.Should().Be(2.0);

        var second = await _stage.ExecuteAsync(baseContext with
        {
            ApsSnapshotList = [new ApsIobCobPoint(
                DateTimeOffset.FromUnixTimeMilliseconds(startTime - 60 * 1000).UtcDateTime, 1.5, null, null)],
        }, CancellationToken.None);

        second.IobSeries.Should().ContainSingle().Which.Value.Should().Be(1.5);
    }

    [Fact]
    public void BuildIobCobSeries_NowInADifferentTick_MissesTheCache_ButNotInTheSameTick()
    {
        // Ticks at start and start + 5 min; start is not on the epoch's five-minute grid, so
        // start + 3 and start + 6 share an epoch slot while falling in different ticks.
        const long minute = 60 * 1000;
        var start = TestMills;
        var timeline = new TherapyTimeline([new TherapySegment(start - 600 * minute, start + 600 * minute,
            new TherapySnapshot(3.0, TherapySnapshot.DefaultCarbsPerHour,
                null, null, 0, null, null, null))]);
        List<Bolus> boluses = [new Bolus
        {
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(start - 5 * minute).UtcDateTime,
            Insulin = 3.0,
        }];
        // Stamped two minutes after the second tick, so only that tick, while live, takes it.
        IReadOnlyList<ApsIobCobPoint> aps = [new ApsIobCobPoint(
            DateTimeOffset.FromUnixTimeMilliseconds(start + 7 * minute).UtcDateTime, 1.5, null, null)];
        var calculated = 2.0;
        _mockIobCalculator
            .Setup(s => s.FromBoluses(It.IsAny<List<Bolus>>(), It.IsAny<TherapySnapshot>(), It.IsAny<long>()))
            .Returns(() => new IobResult { Iob = calculated });

        List<double> At(long now) => _stage
            .BuildIobCobSeries(boluses, [], start, start + 5 * minute, now, 5, [], timeline, [], aps)
            .iobSeries.Select(p => p.Value).ToList();

        var firstTickLive = At(start + 3 * minute);
        var secondTickLive = At(start + 6 * minute);
        calculated = 9.0;
        var secondTickLater = At(start + 9 * minute);

        firstTickLive.Should().Equal(2.0, 2.0);
        secondTickLive.Should().Equal(2.0, 1.5);
        secondTickLater.Should().Equal(secondTickLive);
    }

    [Fact]
    public void BuildIobCobSeries_StartsWithinOneInterval_AreDifferentSeries()
    {
        // Two windows whose starts share an epoch five-minute slot tick at different instants.
        const long minute = 60 * 1000;
        var timeline = new TherapyTimeline([new TherapySegment(TestMills - 600 * minute, TestMills + 600 * minute,
            new TherapySnapshot(3.0, TherapySnapshot.DefaultCarbsPerHour,
                null, null, 0, null, null, null))]);
        List<Bolus> boluses = [new Bolus
        {
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(TestMills - 5 * minute).UtcDateTime,
            Insulin = 3.0,
        }];
        _mockIobCalculator
            .Setup(s => s.FromBoluses(It.IsAny<List<Bolus>>(), It.IsAny<TherapySnapshot>(), It.IsAny<long>()))
            .Returns(new IobResult { Iob = 2.0 });

        List<long> TicksFrom(long start) => _stage
            .BuildIobCobSeries(boluses, [], start, start + 5 * minute, start + 60 * minute, 5, [], timeline, [])
            .iobSeries.Select(p => p.Timestamp).ToList();

        var first = TicksFrom(TestMills);
        var second = TicksFrom(TestMills + minute);

        first.Should().Equal(TestMills, TestMills + 5 * minute);
        second.Should().Equal(TestMills + minute, TestMills + 6 * minute);
    }

    [Fact]
    public async Task ExecuteAsync_NewPumpSnapshot_InvalidatesCachedSeries()
    {
        _mockIobCalculator
            .Setup(s => s.FromBoluses(It.IsAny<List<Bolus>>(), It.IsAny<TherapySnapshot>(), It.IsAny<long>()))
            .Returns(new IobResult { Iob = 2.0 });
        var baseContext = new ChartDataContext
        {
            StartTime = TestMills,
            EndTime = TestMills,
            IntervalMinutes = 5,
            DefaultBasalRate = 1.0,
            BolusList = [new Bolus
            {
                Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(TestMills - 5 * 60 * 1000).UtcDateTime,
                Insulin = 3.0,
            }],
        };

        var first = await _stage.ExecuteAsync(baseContext, CancellationToken.None);
        var second = await _stage.ExecuteAsync(baseContext with
        {
            PumpIobPointList = [new PumpIobPoint(
                DateTimeOffset.FromUnixTimeMilliseconds(TestMills - 60 * 1000).UtcDateTime, 1.1, null)],
        }, CancellationToken.None);

        first.IobSeries.Should().ContainSingle().Which.Value.Should().Be(2.0);
        second.IobSeries.Should().ContainSingle().Which.Value.Should().Be(1.1);
    }

    /// <summary>
    /// A share computes with the profile tables hidden from it, so its series must never meet the
    /// owner's.
    /// </summary>
    [Theory]
    [MemberData(nameof(NarrowedReaders.All), MemberType = typeof(NarrowedReaders))]
    public async Task ExecuteAsync_NarrowedReader_NeverSharesTheOwnersCachedSeries(string reader)
    {
        var context = new ChartDataContext
        {
            StartTime = TestMills,
            EndTime = TestMills,
            IntervalMinutes = 5,
            DefaultBasalRate = 1.0,
            BolusList = [new Bolus
            {
                Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(TestMills - 5 * 60 * 1000).UtcDateTime,
                Insulin = 3.0,
            }],
            CarbIntakeList = [],
            TempBasalList = [],
        };
        void IobIs(double iob) => _mockIobCalculator
            .Setup(s => s.FromBoluses(It.IsAny<List<Bolus>>(), It.IsAny<TherapySnapshot>(), It.IsAny<long>()))
            .Returns(new IobResult { Iob = iob });
        var narrowed = CreateStage(NarrowedReaders.Cache(reader, _cache));

        IobIs(2.0);
        await _stage.ExecuteAsync(context, CancellationToken.None);

        IobIs(7.0);
        var narrowedResult = await narrowed.ExecuteAsync(context, CancellationToken.None);

        IobIs(9.0);
        var owner = await _stage.ExecuteAsync(context, CancellationToken.None);

        narrowedResult.IobSeries.Should().ContainSingle().Which.Value.Should().Be(7.0, "the owner's cached series is not served");
        owner.IobSeries.Should().ContainSingle().Which.Value.Should().Be(2.0, "the narrowed series is not written over it");
    }

    [Fact]
    public async Task ExecuteAsync_ProfileChange_InvalidatesCachedSeries()
    {
        var dia = 3.0;
        _mockTherapyTimelineResolver
            .Setup(r => r.BuildAsync(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((long from, long to, string? _, CancellationToken _) =>
                new TherapyTimeline([new TherapySegment(from, to, new TherapySnapshot(
                    dia, TherapySnapshot.DefaultCarbsPerHour,
                    null, null, 0, null, null, null))]));
        _mockIobCalculator
            .Setup(s => s.FromBoluses(It.IsAny<List<Bolus>>(), It.IsAny<TherapySnapshot>(), It.IsAny<long>()))
            .Returns((List<Bolus> _, TherapySnapshot snapshot, long _) => new IobResult { Iob = snapshot.Dia });
        var context = new ChartDataContext
        {
            StartTime = TestMills,
            EndTime = TestMills,
            IntervalMinutes = 5,
            DefaultBasalRate = 1.0,
            BolusList = [new Bolus
            {
                Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(TestMills - 5 * 60 * 1000).UtcDateTime,
                Insulin = 3.0,
            }],
            CarbIntakeList = [],
            TempBasalList = [],
        };

        var first = await _stage.ExecuteAsync(context, CancellationToken.None);
        dia = 5.0;
        var second = await _stage.ExecuteAsync(context, CancellationToken.None);

        first.IobSeries.Should().ContainSingle().Which.Value.Should().Be(3.0);
        second.IobSeries.Should().ContainSingle().Which.Value.Should().Be(5.0);
    }

    [Fact]
    public async Task ExecuteAsync_BasalScheduleEditBetweenTicks_InvalidatesCachedSeries()
    {
        // TestMills is 22:13:20 UTC and the temp basal starts at 22:11:20. The edit adds a 2 U/h
        // block from 22:10 to 22:13, which changes the rate under the temp basal's first chunk but
        // not at the only tick.
        var basal = new List<ScheduleEntry> { new() { TimeAsSeconds = 0, Value = 1.0 } };
        _mockTherapyTimelineResolver
            .Setup(r => r.BuildAsync(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((long from, long to, string? _, CancellationToken _) =>
                new TherapyTimeline([new TherapySegment(from, to, new TherapySnapshot(
                    3.0, TherapySnapshot.DefaultCarbsPerHour,
                    null, null, 0, null, null, basal.ToList()))]));
        _mockIobCalculator
            .Setup(s => s.FromTempBasals(It.IsAny<List<TempBasal>>(), It.IsAny<TherapySnapshot>(), It.IsAny<Func<TempBasal, long, double>>(), It.IsAny<long>(), It.IsAny<long>()))
            .Returns((List<TempBasal> tempBasals, TherapySnapshot _, Func<TempBasal, long, double> scheduledUnder, long _, long _) =>
                new IobResult { BasalIob = scheduledUnder(tempBasals[0], tempBasals[0].StartMills) });
        var context = new ChartDataContext
        {
            StartTime = TestMills,
            EndTime = TestMills,
            IntervalMinutes = 5,
            DefaultBasalRate = 1.0,
            BolusList = [],
            CarbIntakeList = [],
            TempBasalList = [new TempBasal
            {
                StartTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(TestMills - 2 * 60 * 1000).UtcDateTime,
                Rate = 0.5,
                Origin = TempBasalOrigin.Algorithm,
            }],
        };

        var first = await _stage.ExecuteAsync(context, CancellationToken.None);
        basal =
        [
            new() { TimeAsSeconds = 0, Value = 1.0 },
            new() { TimeAsSeconds = 22 * 3600 + 10 * 60, Value = 2.0 },
            new() { TimeAsSeconds = 22 * 3600 + 13 * 60, Value = 1.0 },
        ];
        var second = await _stage.ExecuteAsync(context, CancellationToken.None);

        first.IobSeries.Should().ContainSingle().Which.Value.Should().Be(1.0);
        second.IobSeries.Should().ContainSingle().Which.Value.Should().Be(2.0);
    }

    [Fact]
    public async Task ExecuteAsync_BasalSeriesGetsOnlyDisplayTempBasals_WhileIobSeesPreWindowTempBasal()
    {
        var startTime = TestMills;
        var endTime = TestMills + 30 * 60 * 1000;
        var preWindow = new TempBasal
        {
            StartTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(startTime - 60 * 60 * 1000).UtcDateTime,
            EndTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(startTime - 30 * 60 * 1000).UtcDateTime,
            Rate = 3.0,
            Origin = TempBasalOrigin.Algorithm,
        };

        List<TempBasal>? firstTickBasals = null;
        _mockIobCalculator
            .Setup(s => s.FromTempBasals(It.IsAny<List<TempBasal>>(), It.IsAny<TherapySnapshot>(), It.IsAny<Func<TempBasal, long, double>>(), It.IsAny<long>(), It.IsAny<long>()))
            .Callback<List<TempBasal>, TherapySnapshot, Func<TempBasal, long, double>, long, long>((tbs, _, _, t, _) =>
            {
                if (t == startTime)
                    firstTickBasals = tbs;
            })
            .Returns(new IobResult { BasalIob = 0.4 });

        var context = new ChartDataContext
        {
            StartTime = startTime,
            EndTime = endTime,
            IntervalMinutes = 5,
            DefaultBasalRate = 1.0,
            TempBasalList = [preWindow],
            DisplayTempBasals = [],
        };

        var result = await _stage.ExecuteAsync(context, CancellationToken.None);

        firstTickBasals.Should().ContainSingle().Which.Should().BeEquivalentTo(preWindow);
        result.IobSeries[0].Value.Should().BeApproximately(0.4, 1e-9);
        _mockBasalSeriesBuilder.Verify(b => b.BuildAsync(
            It.Is<List<TempBasal>>(l => l.Count == 0),
            startTime, endTime, It.IsAny<double>(), It.IsAny<IReadOnlyList<ScaleFactorInterval>>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_BasalSeriesGetsTheOverridesTheContextHolds()
    {
        var startTime = TestMills;
        var endTime = TestMills + 30 * 60 * 1000;
        ScaleFactorInterval[] overrides = [new(startTime - 60 * 60 * 1000, endTime, 0.5)];
        var context = new ChartDataContext
        {
            StartTime = startTime,
            EndTime = endTime,
            IntervalMinutes = 5,
            DefaultBasalRate = 1.0,
            Overrides = overrides,
        };

        await _stage.ExecuteAsync(context, CancellationToken.None);

        _mockBasalSeriesBuilder.Verify(b => b.BuildAsync(
            It.IsAny<List<TempBasal>>(), startTime, endTime, It.IsAny<double>(),
            It.Is<IReadOnlyList<ScaleFactorInterval>>(o => o.SequenceEqual(overrides)), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    private const long Minute = 60 * 1000;

    private static TherapySnapshot FlatSchedule(double basalRate) => new(
        3.0, TherapySnapshot.DefaultCarbsPerHour, null, null, 0,
        [new ScheduleEntry { TimeAsSeconds = 0, Value = 95.0 }], null,
        [new ScheduleEntry { TimeAsSeconds = 0, Value = basalRate }]);

    /// <summary>
    /// The stage with the real IOB calculator over the mocked timeline and a COB calculator that is
    /// never reached, since these cases carry no carbs.
    /// </summary>
    private IobCobComputeStage RealIobStage() => new(
        new IobCalculator(
            Mock.Of<ITherapySettingsResolver>(MockBehavior.Strict),
            Mock.Of<ISensitivityResolver>(MockBehavior.Strict),
            Mock.Of<IBasalSegmentService>(MockBehavior.Strict),
            Mock.Of<IApsSnapshotRepository>(MockBehavior.Strict),
            Mock.Of<IPumpSnapshotRepository>(MockBehavior.Strict),
            Mock.Of<IStateSpanRepository>(MockBehavior.Strict)),
        new Mock<ICobCalculator>(MockBehavior.Strict).Object,
        _mockBasalSeriesBuilder.Object,
        _mockTherapyTimelineResolver.Object,
        TestTenantCache.Create(new MemoryCache(new MemoryCacheOptions())),
        NullLogger<IobCobComputeStage>.Instance);

    private static TempBasal Temp(long start, long? end, double rate, TempBasalOrigin origin = TempBasalOrigin.Algorithm) => new()
    {
        StartTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(start).UtcDateTime,
        EndTimestamp = end is { } e ? DateTimeOffset.FromUnixTimeMilliseconds(e).UtcDateTime : null,
        Rate = rate,
        Origin = origin,
    };

    [Fact]
    public async Task ExecuteAsync_ScheduleChangeBeforeTheWindow_SetsTheBaselineOfEarlierChunks()
    {
        // The profile in force until 20 minutes before the window had a 2 U/h schedule. A 1 U/h temp
        // over the 30 minutes before the window withheld 1/12 U in each of its chunks 30 and 25
        // minutes old and nothing after: -(0.922216 + 0.94444) / 12 = -0.155555 on the DIA 3 h curve,
        // where a dose m minutes old keeps 1 - 0.001852 (m/5)(m/5 + 1). Built from the window start,
        // the timeline would put every chunk under the later 1 U/h profile.
        var bufferStart = TestMills - 8L * 60 * Minute;
        var switchAt = TestMills - 20 * Minute;
        _mockTherapyTimelineResolver
            .Setup(r => r.BuildAsync(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((long from, long to, string? _, CancellationToken _) => from < switchAt
                ? new TherapyTimeline(
                [
                    new TherapySegment(from, switchAt, FlatSchedule(2.0)),
                    new TherapySegment(switchAt, to, FlatSchedule(1.0)),
                ])
                : new TherapyTimeline([new TherapySegment(from, to, FlatSchedule(1.0))]));

        var result = await RealIobStage().ExecuteAsync(new ChartDataContext
        {
            StartTime = TestMills,
            EndTime = TestMills,
            BufferStartTime = bufferStart,
            IntervalMinutes = 5,
            DefaultBasalRate = 1.0,
            TempBasalList = [Temp(TestMills - 30 * Minute, TestMills, rate: 1.0)],
        }, CancellationToken.None);

        result.IobSeries.Should().ContainSingle().Which.Value.Should().Be(-0.156);
        result.MinIob.Should().Be(-0.156);
    }

    [Fact]
    public async Task ExecuteAsync_SuspendOutweighingBoluses_GivesNegativeIob()
    {
        // A 0.5 U bolus and a suspend on a 1 U/h schedule, both from 30 minutes before the window's end.
        // At the end the bolus keeps 0.5 * 0.922216 = 0.461 and the suspend has withheld six 1/12 U
        // chunks, (0.996296 + 0.988888 + 0.977776 + 0.96296 + 0.94444 + 0.922216) / 12 = 0.483, so the
        // tick is -0.022. Five minutes earlier the bolus keeps 0.5 * 0.94444 = 0.472 and five chunks are
        // withheld, (0.996296 + 0.988888 + 0.977776 + 0.96296 + 0.94444) / 12 = 0.406, so 0.066.
        _mockTherapyTimelineResolver
            .Setup(r => r.BuildAsync(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((long from, long to, string? _, CancellationToken _) =>
                new TherapyTimeline([new TherapySegment(from, to, FlatSchedule(1.0))]));

        var result = await RealIobStage().ExecuteAsync(new ChartDataContext
        {
            StartTime = TestMills - 5 * Minute,
            EndTime = TestMills,
            BufferStartTime = TestMills - 8L * 60 * Minute,
            IntervalMinutes = 5,
            DefaultBasalRate = 1.0,
            BolusList = [new Bolus
            {
                Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(TestMills - 30 * Minute).UtcDateTime,
                Insulin = 0.5,
            }],
            TempBasalList = [Temp(TestMills - 30 * Minute, TestMills, rate: 0, TempBasalOrigin.Suspended)],
        }, CancellationToken.None);

        result.IobSeries.Select(p => Math.Round(p.Value, 9)).Should().Equal(0.066, -0.022);
        result.MinIob.Should().Be(result.IobSeries[1].Value);
        result.MaxIob.Should().Be(3);
    }

    [Fact]
    public async Task ExecuteAsync_NoNegativeIob_MinIobIsZero()
    {
        _mockIobCalculator
            .Setup(s => s.FromBoluses(It.IsAny<List<Bolus>>(), It.IsAny<TherapySnapshot>(), It.IsAny<long>()))
            .Returns(new IobResult { Iob = 2.0 });

        var result = await _stage.ExecuteAsync(new ChartDataContext
        {
            StartTime = TestMills,
            EndTime = TestMills,
            IntervalMinutes = 5,
            DefaultBasalRate = 1.0,
            BolusList = [new Bolus
            {
                Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(TestMills - 5 * Minute).UtcDateTime,
                Insulin = 3.0,
            }],
        }, CancellationToken.None);

        result.IobSeries.Should().ContainSingle().Which.Value.Should().Be(2.0);
        result.MinIob.Should().Be(0);
    }

    /// <summary>
    /// Runs the stage over <paramref name="first"/> and then <paramref name="second"/>, which differ
    /// only in one input the series reads, and returns the single IOB or COB tick of each.
    /// </summary>
    private async Task<(double First, double Second)> TwoRuns(
        ChartDataContext first, ChartDataContext second, Func<ChartDataContext, double> tick)
    {
        var a = await _stage.ExecuteAsync(first, CancellationToken.None);
        var b = await _stage.ExecuteAsync(second, CancellationToken.None);
        return (tick(a), tick(b));
    }

    private static ChartDataContext WindowOf(
        List<TempBasal>? temps = null, List<Bolus>? boluses = null, List<CarbIntake>? carbs = null) => new()
    {
        StartTime = TestMills,
        EndTime = TestMills,
        IntervalMinutes = 5,
        DefaultBasalRate = 1.0,
        TempBasalList = temps ?? [],
        BolusList = boluses ?? [],
        CarbIntakeList = carbs ?? [],
    };

    private void BasalIobIs(Func<List<TempBasal>, double> read) => _mockIobCalculator
        .Setup(s => s.FromTempBasals(It.IsAny<List<TempBasal>>(), It.IsAny<TherapySnapshot>(), It.IsAny<Func<TempBasal, long, double>>(), It.IsAny<long>(), It.IsAny<long>()))
        .Returns((List<TempBasal> pieces, TherapySnapshot _, Func<TempBasal, long, double> _, long _, long _) => new IobResult { BasalIob = read(pieces) });

    private static double OnlyIob(ChartDataContext result) => result.IobSeries.Single().Value;

    [Fact]
    public async Task ExecuteAsync_TempOriginChange_InvalidatesCachedSeries()
    {
        BasalIobIs(pieces => pieces[0].Origin == TempBasalOrigin.Suspended ? -1 : 1);

        var (first, second) = await TwoRuns(
            WindowOf(temps: [Temp(TestMills - 10 * Minute, TestMills, rate: 2.0)]),
            WindowOf(temps: [Temp(TestMills - 10 * Minute, TestMills, rate: 2.0, TempBasalOrigin.Suspended)]),
            OnlyIob);

        (first, second).Should().Be((1.0, -1.0));
    }

    [Fact]
    public async Task ExecuteAsync_OverrideChange_InvalidatesCachedSeries()
    {
        _mockIobCalculator
            .Setup(s => s.FromTempBasals(It.IsAny<List<TempBasal>>(), It.IsAny<TherapySnapshot>(), It.IsAny<Func<TempBasal, long, double>>(), It.IsAny<long>(), It.IsAny<long>()))
            .Returns((List<TempBasal> pieces, TherapySnapshot _, Func<TempBasal, long, double> scheduledUnder, long _, long _) =>
                new IobResult { BasalIob = scheduledUnder(pieces[0], pieces[0].StartMills) });
        var window = WindowOf(temps: [Temp(TestMills - 10 * Minute, TestMills, rate: 2.0)]);

        var (first, second) = await TwoRuns(
            window,
            window with { Overrides = [new ScaleFactorInterval(TestMills - 60 * Minute, TestMills + 60 * Minute, 0.5)] },
            OnlyIob);

        (first, second).Should().Be((1.0, 0.5));
    }

    [Fact]
    public async Task ExecuteAsync_TempScheduledRateChange_InvalidatesCachedSeries()
    {
        BasalIobIs(pieces => pieces[0].ScheduledRate ?? 0);
        TempBasal WithScheduledRate(double rate)
        {
            var temp = Temp(TestMills - 10 * Minute, TestMills, rate: 2.0);
            temp.ScheduledRate = rate;
            return temp;
        }

        var (first, second) = await TwoRuns(
            WindowOf(temps: [WithScheduledRate(0.5)]), WindowOf(temps: [WithScheduledRate(0.8)]), OnlyIob);

        (first, second).Should().Be((0.5, 0.8));
    }

    [Fact]
    public async Task ExecuteAsync_TempInsulinContextDiaChange_InvalidatesCachedSeries()
    {
        BasalIobIs(pieces => pieces[0].InsulinContext?.Dia ?? 0);
        TempBasal WithDia(double dia)
        {
            var temp = Temp(TestMills - 10 * Minute, TestMills, rate: 2.0);
            temp.InsulinContext = new TreatmentInsulinContext { Dia = dia, Peak = 75 };
            return temp;
        }

        var (first, second) = await TwoRuns(WindowOf(temps: [WithDia(3)]), WindowOf(temps: [WithDia(5)]), OnlyIob);

        (first, second).Should().Be((3.0, 5.0));
    }

    [Fact]
    public async Task ExecuteAsync_BolusInsulinContextChange_InvalidatesCachedSeries()
    {
        _mockIobCalculator
            .Setup(s => s.FromBoluses(It.IsAny<List<Bolus>>(), It.IsAny<TherapySnapshot>(), It.IsAny<long>()))
            .Returns((List<Bolus> boluses, TherapySnapshot _, long _) => new IobResult { Iob = boluses[0].InsulinContext?.Dia ?? 0 });
        Bolus WithDia(double dia) => new()
        {
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(TestMills - 5 * Minute).UtcDateTime,
            Insulin = 1.0,
            InsulinContext = new TreatmentInsulinContext { Dia = dia, Peak = 75 },
        };

        var (first, second) = await TwoRuns(WindowOf(boluses: [WithDia(4)]), WindowOf(boluses: [WithDia(6)]), OnlyIob);

        (first, second).Should().Be((4.0, 6.0));
    }

    [Fact]
    public async Task ExecuteAsync_CarbAbsorptionTimeChange_InvalidatesCachedSeries()
    {
        _mockCobCalculator
            .Setup(s => s.FromCarbIntakes(It.IsAny<List<CarbIntake>>(), It.IsAny<List<Bolus>?>(), It.IsAny<List<TempBasal>?>(), It.IsAny<TherapySnapshot>(), It.IsAny<Func<TempBasal, long, double>>(), It.IsAny<long>()))
            .Returns((List<CarbIntake> carbs, List<Bolus>? _, List<TempBasal>? _, TherapySnapshot _, Func<TempBasal, long, double> _, long _) =>
                new CobResult { Cob = carbs[0].AbsorptionTime ?? 0 });
        CarbIntake WithAbsorption(int minutes) => new()
        {
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(TestMills - 5 * Minute).UtcDateTime,
            Carbs = 30,
            AbsorptionTime = minutes,
        };

        var (first, second) = await TwoRuns(
            WindowOf(carbs: [WithAbsorption(120)]), WindowOf(carbs: [WithAbsorption(180)]), r => r.CobSeries.Single().Value);

        (first, second).Should().Be((120.0, 180.0));
    }

    [Fact]
    public async Task ExecuteAsync_DeviceChangeThatMovesTheClipsWinner_InvalidatesCachedSeries()
    {
        // Two streams cover the same ten minutes. With nothing else between them the lower stream key
        // wins, so renaming the 3 U/h stream from "b" to "0" makes it the one delivered.
        BasalIobIs(pieces => pieces.Single().Rate!.Value);
        TempBasal OnDevice(string device, double rate)
        {
            var temp = Temp(TestMills - 10 * Minute, TestMills, rate);
            temp.Device = device;
            return temp;
        }

        var (first, second) = await TwoRuns(
            WindowOf(temps: [OnDevice("a", 2.0), OnDevice("b", 3.0)]),
            WindowOf(temps: [OnDevice("a", 2.0), OnDevice("0", 3.0)]),
            OnlyIob);

        (first, second).Should().Be((2.0, 3.0));
    }
}
