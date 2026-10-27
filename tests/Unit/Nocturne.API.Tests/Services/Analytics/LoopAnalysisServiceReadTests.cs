using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Nocturne.API.Services.Analytics;
using Nocturne.API.Tests.TestDoubles;
using Nocturne.Core.Clinical.LoopAnalysis;
using Nocturne.API.Services.ChartData;
using Nocturne.Core.Clinical.Treatments;
using Nocturne.Core.Contracts.Analytics;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.LoopAnalysis;
using Nocturne.Core.Models.V4;
using Nocturne.Tests.Shared.Mocks;
using Xunit;

namespace Nocturne.API.Tests.Services.Analytics;

/// <summary>What the Loop Analysis pages read around a range: now, and the AID evidence of the open-loop mask.</summary>
public class LoopAnalysisServiceReadTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    private static readonly DateTime From = new(2026, 6, 9, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime To = From.AddDays(1);

    private readonly Mock<ITempBasalRepository> _tempBasals = new();
    private readonly Mock<IApsSnapshotRepository> _snapshots = new();
    private readonly Mock<IPatientDeviceRepository> _devices = new();
    private readonly Mock<IBasalSeriesBuilder> _series = new();
    private readonly List<TempBasal> _seriesTemps = [];
    private readonly List<ScaleFactorInterval> _seriesOverrides = [];
    private readonly List<StateSpan> _overrideSpans = [];
    private readonly List<long> _timelineStarts = [];

    public LoopAnalysisServiceReadTests()
    {
        _tempBasals
            .Setup(r => r.GetAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _snapshots
            .Setup(r => r.GetIobCobPointsAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _snapshots
            .Setup(r => r.GetPredictionsAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _devices
            .Setup(r => r.GetByDateRangeAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _series
            .Setup(b => b.BuildAsync(
                It.IsAny<List<TempBasal>>(), It.IsAny<long>(), It.IsAny<long>(), It.IsAny<double>(), It.IsAny<IReadOnlyList<ScaleFactorInterval>>(), It.IsAny<CancellationToken>()))
            .Callback((List<TempBasal> temps, long _, long _, double _, IReadOnlyList<ScaleFactorInterval> overrides, CancellationToken _) =>
            {
                _seriesTemps.AddRange(temps);
                _seriesOverrides.AddRange(overrides);
            })
            .ReturnsAsync([]);
    }

    private LoopAnalysisService Service()
    {
        var profiles = new Mock<IActiveProfileResolver>();
        profiles
            .Setup(r => r.GetProfileSpansForRangeAsync(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        var spans = new Mock<IStateSpanRepository>();
        spans
            .Setup(r => r.GetByCategories(
                It.IsAny<IEnumerable<StateSpanCategory>>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new Dictionary<StateSpanCategory, List<StateSpan>>
            {
                [StateSpanCategory.Override] = [.. _overrideSpans],
            });
        var glucose = new Mock<ISensorGlucoseRepository>();
        glucose
            .Setup(r => r.GetForAnalyticsAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<DateTime?>(), It.IsAny<Guid?>(),
                It.IsAny<CancellationToken>(), It.IsAny<Guid?>()))
            .ReturnsAsync([]);
        var timeline = new Mock<ITherapyTimelineResolver>();
        timeline
            .Setup(t => t.BuildAsync(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback((long from, long _, string? _, CancellationToken _) => _timelineStarts.Add(from))
            .ReturnsAsync(new TherapyTimeline([
                new TherapySegment(0, long.MaxValue,
                    new TherapySnapshot(5.0, TherapySnapshot.DefaultCarbsPerHour,
                        null, null, 0, null, null, null)),
            ]));

        return new LoopAnalysisService(
            glucoseRepository: glucose.Object,
            canonicalGlucose: CanonicalGlucosePassThrough.Create(),
            bolusRepository: Mock.Of<IBolusRepository>(),
            carbIntakeRepository: Mock.Of<ICarbIntakeRepository>(),
            tempBasalRepository: _tempBasals.Object,
            apsSnapshotRepository: _snapshots.Object,
            pumpSnapshotRepository: Mock.Of<IPumpSnapshotRepository>(),
            stateSpanRepository: spans.Object,
            patientDeviceRepository: _devices.Object,
            targetRangeScheduleRepository: Mock.Of<ITargetRangeScheduleRepository>(),
            therapyTimelineResolver: timeline.Object,
            therapySettingsResolver: Mock.Of<ITherapySettingsResolver>(),
            activeProfileResolver: profiles.Object,
            basalSeriesBuilder: _series.Object,
            basalSegmentService: Mock.Of<IBasalSegmentService>(),
            iobCalculator: new IobCalculator(
                Mock.Of<ITherapySettingsResolver>(), Mock.Of<ISensitivityResolver>(), Mock.Of<IBasalSegmentService>(),
                Mock.Of<IApsSnapshotRepository>(), Mock.Of<IPumpSnapshotRepository>(), MockStateSpans.Empty().Object),
            cobCalculator: Mock.Of<ICobCalculator>(),
            therapySettingsRepository: Mock.Of<ITherapySettingsRepository>(),
            aidSystemMetrics: null!,
            deviceDates: DeviceDateClockDoubles.Utc(),
            deviceEventRepository: Mock.Of<IDeviceEventRepository>());
    }

    [Fact]
    public async Task SettingsEvidence_ReadPastTheRangesEnd_StopsARunningTempAtNow()
    {
        var running = new TempBasal
        {
            StartTimestamp = To.AddMinutes(-30),
            Rate = 2.0,
            Origin = TempBasalOrigin.Algorithm,
            Device = "pump",
        };
        _tempBasals
            .Setup(r => r.GetAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([running]);

        await Service().GetSettingsEvidenceAsync(new LoopAnalysisFilter { From = From, To = To, Now = To }, Utc);

        _seriesTemps.Should().ContainSingle().Which.EndTimestamp.Should().Be(To,
            "the load reads past the range for climbs, but nothing after now has been delivered");
    }

    [Fact]
    public async Task SettingsEvidence_HandsTheBasalSeriesTheOverridesItRead()
    {
        _overrideSpans.Add(new StateSpan
        {
            Category = StateSpanCategory.Override,
            StartTimestamp = From.AddHours(2),
            EndTimestamp = From.AddHours(4),
            Metadata = new Dictionary<string, object> { ["insulinNeedsScaleFactor"] = 0.5 },
        });

        await Service().GetSettingsEvidenceAsync(new LoopAnalysisFilter { From = From, To = To, Now = To }, Utc);

        _seriesOverrides.Should().Equal(new ScaleFactorInterval(
            new DateTimeOffset(From.AddHours(2)).ToUnixTimeMilliseconds(),
            new DateTimeOffset(From.AddHours(4)).ToUnixTimeMilliseconds(),
            0.5));
    }

    [Fact]
    public async Task DailyProfile_BuildsTheTimelineFromTheLookback()
    {
        // Temp basals are read from the look-back before the range, and the net basal IOB at the
        // first tick reads the schedule under each of their chunks, so the timeline starts there too.
        await Service().GetDailyProfileAsync(new LoopAnalysisFilter { From = From, To = To, Now = To }, Utc);

        _timelineStarts.Should().NotBeEmpty().And.OnlyContain(start =>
            start == new DateTimeOffset(From).ToUnixTimeMilliseconds() - DailyProfileCalculator.ActingMs);
    }

    [Theory]
    [InlineData(-10, 1)]
    [InlineData(40, 0)]
    public async Task Predictions_TakeTheirOpenLoopEvidence_FromWhenEachApsSnapshotWasStored(int storedMinutesAfter, int counted)
    {
        var start = From.AddHours(10);
        _devices
            .Setup(r => r.GetByDateRangeAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new PatientDevice
            {
                Id = Guid.NewGuid(),
                DeviceCategory = DeviceCategory.InsulinPump,
                AidAlgorithm = AidAlgorithm.None,
                StartDate = DateOnly.FromDateTime(From),
                EndDate = DateOnly.FromDateTime(From),
            }]);
        _snapshots
            .Setup(r => r.GetPredictionsAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new ApsPredictionPoint(start, "loop://iPhone", true, AidAlgorithm.Loop, "[120,120,120,120,120,120,120]", null, null, null, null)]);
        _snapshots
            .Setup(r => r.GetIobCobPointsAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new ApsIobCobPoint(start.AddMinutes(storedMinutesAfter), null, null, null)]);

        var report = await Service().GetPredictionAccuracyAsync(new LoopAnalysisFilter { From = From, To = To, Now = To.AddDays(1) }, Utc);

        report.Primary.Predictions.Should().Be(counted,
            "a registered pump with no AID algorithm is open loop wherever no APS snapshot was stored in the half hour before, as on every page");
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 1)]
    [InlineData(null, 1)]
    public async Task Predictions_OnALoopPump_AreOpenLoop_WhereASnapshotHadDosingOff(bool? closedLoop, int counted)
    {
        var start = From.AddHours(10);
        _devices
            .Setup(r => r.GetByDateRangeAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new PatientDevice
            {
                Id = Guid.NewGuid(),
                DeviceCategory = DeviceCategory.InsulinPump,
                AidAlgorithm = AidAlgorithm.Loop,
                StartDate = DateOnly.FromDateTime(From),
                EndDate = DateOnly.FromDateTime(From),
            }]);
        _snapshots
            .Setup(r => r.GetPredictionsAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new ApsPredictionPoint(start, "loop://iPhone", true, AidAlgorithm.Loop, "[120,120,120,120,120,120,120]", null, null, null, null)]);
        _snapshots
            .Setup(r => r.GetIobCobPointsAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new ApsIobCobPoint(start.AddMinutes(-5), null, null, closedLoop)]);

        var report = await Service().GetPredictionAccuracyAsync(new LoopAnalysisFilter { From = From, To = To, Now = To.AddDays(1) }, Utc);

        report.Primary.Predictions.Should().Be(counted,
            "a Loop snapshot with dosing off is open loop, and one with dosing on or unknown is closed, whatever the pump is registered as");
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(null, true)]
    public void LoopStates_ReadTheClosedLoopState_OfACycleAndOfAnIobCobPoint(bool? closedLoop, bool closed)
    {
        var at = From.AddHours(3);

        LoopAnalysisService.LoopStates([new ApsLoopCycle { Timestamp = at, ClosedLoop = closedLoop }])
            .Should().Equal(new ApsLoopState(at.ToMills(), closed));
        LoopAnalysisService.LoopStates([new ApsIobCobPoint(at, null, null, closedLoop)])
            .Should().Equal(new ApsLoopState(at.ToMills(), closed));
    }

    [Fact]
    public async Task ActiveTargetRange_ReadsNowOnce_FromTheTimeProvider()
    {
        var now = new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);
        var profiles = new Mock<IActiveProfileResolver>();
        profiles.Setup(r => r.GetProfileNameAsync(It.IsAny<long>(), It.IsAny<CancellationToken>())).ReturnsAsync("Default");
        var schedules = new Mock<ITargetRangeScheduleRepository>();
        var schedule = new TargetRangeSchedule();
        schedules.Setup(r => r.GetActiveAtAsync("Default", now.UtcDateTime, It.IsAny<CancellationToken>())).ReturnsAsync(schedule);

        var active = await ActiveTargetRange.ScheduleAsync(profiles.Object, schedules.Object, new FakeTimeProvider(now), CancellationToken.None);

        active.Should().BeSameAs(schedule);
        profiles.Verify(r => r.GetProfileNameAsync(now.ToUnixTimeMilliseconds(), It.IsAny<CancellationToken>()), Times.Once);
    }
}
