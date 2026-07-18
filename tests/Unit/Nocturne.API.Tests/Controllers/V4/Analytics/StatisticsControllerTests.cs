using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Nocturne.API.Controllers.V4.Analytics;
using Nocturne.Core.Contracts.Analytics;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Profiles;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Cache.Abstractions;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4.Analytics;

[Trait("Category", "Unit")]
public class StatisticsControllerTests
{
    private readonly Mock<IStatisticsService> _statsServiceMock = new();
    private readonly Mock<ISensorGlucoseRepository> _glucoseRepoMock = new();
    private readonly Mock<IBolusRepository> _bolusRepoMock = new();
    private readonly Mock<ICarbIntakeRepository> _carbIntakeRepoMock = new();
    private readonly Mock<ITempBasalRepository> _tempBasalRepoMock = new();
    private readonly Mock<ITherapySettingsResolver> _therapySettingsResolverMock = new();
    private readonly Mock<ITargetRangeScheduleRepository> _targetRangeScheduleRepoMock = new();
    private readonly Mock<IActiveProfileResolver> _activeProfileResolverMock = new();

    private StatisticsController CreateController()
    {
        var controller = new StatisticsController(
            _statsServiceMock.Object,
            Mock.Of<ICacheService>(),
            Mock.Of<IProfileProjectionService>(),
            Mock.Of<IBasalRateResolver>(),
            Mock.Of<IBasalSegmentService>(),
            _therapySettingsResolverMock.Object,
            _glucoseRepoMock.Object,
            _bolusRepoMock.Object,
            _carbIntakeRepoMock.Object,
            _tempBasalRepoMock.Object,
            Mock.Of<ITenantAccessor>(),
            Mock.Of<IAidMetricsService>(),
            Mock.Of<IPatientDeviceRepository>(),
            Mock.Of<IApsSnapshotRepository>(),
            Mock.Of<IDeviceEventRepository>(),
            _targetRangeScheduleRepoMock.Object,
            _activeProfileResolverMock.Object,
            TestDoubles.CanonicalGlucosePassThrough.Create());

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };

        return controller;
    }

    private void SetupGlucose(IEnumerable<SensorGlucose> readings) =>
        _glucoseRepoMock
            .Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(),
                It.IsAny<bool>(), It.IsAny<DateTime?>(), It.IsAny<Guid?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(readings);

    private void SetupEmptyTreatments()
    {
        _bolusRepoMock
            .Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(),
                It.IsAny<bool>(), It.IsAny<BolusKind?>(),
                It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Bolus>());

        _carbIntakeRepoMock
            .Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(),
                It.IsAny<bool>(), It.IsAny<DateTime?>(), It.IsAny<Guid?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<CarbIntake>());

        _tempBasalRepoMock
            .Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<TempBasal>());

        _statsServiceMock
            .Setup(s => s.CalculateDailyBasalBolusRatios(
                It.IsAny<IEnumerable<Bolus>>(),
                It.IsAny<IEnumerable<Bolus>>(),
                It.IsAny<IEnumerable<TempBasal>>(),
                It.IsAny<TimeZoneInfo?>()))
            .Returns(new DailyBasalBolusRatioResponse());
    }

    [Fact]
    public async Task GetRangeAnalytics_FetchesUncapped_AndReturnsServiceResults()
    {
        // 12,000 readings — above the legacy 10,000 cap that truncated dense tenants.
        var readings = Enumerable.Range(0, 12_000)
            .Select(_ => new SensorGlucose())
            .ToList();
        var analysis = new ExtendedGlucoseAnalytics();
        var averaged = new List<AveragedStats> { new() };

        SetupGlucose(readings);
        SetupEmptyTreatments();

        List<SensorGlucose>? analysedEntries = null;
        _statsServiceMock
            .Setup(s => s.AnalyzeGlucoseDataExtended(
                It.IsAny<IEnumerable<SensorGlucose>>(),
                It.IsAny<IEnumerable<Bolus>>(),
                It.IsAny<IEnumerable<CarbIntake>>(),
                It.IsAny<DiabetesPopulation>(),
                It.IsAny<ExtendedAnalysisConfig?>()))
            .Callback<IEnumerable<SensorGlucose>, IEnumerable<Bolus>, IEnumerable<CarbIntake>, DiabetesPopulation, ExtendedAnalysisConfig?>(
                (entries, _, _, _, _) => analysedEntries = entries.ToList())
            .Returns(analysis);
        _statsServiceMock
            .Setup(s => s.CalculateAveragedStats(It.IsAny<IEnumerable<SensorGlucose>>()))
            .Returns(averaged);

        var controller = CreateController();

        var result = await controller.GetRangeAnalytics(
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc));

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var payload = ok.Value.Should().BeOfType<ReportAnalysisResult>().Subject;
        payload.Analysis.Should().BeSameAs(analysis);
        payload.AveragedStats.Should().BeEquivalentTo(averaged);

        // Every fetched reading reaches the analysis engine — nothing truncated.
        analysedEntries.Should().HaveCount(12_000);

        // The glucose fetch requests an uncapped limit.
        _glucoseRepoMock.Verify(r => r.GetAsync(
            It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
            It.IsAny<string?>(), It.IsAny<string?>(),
            int.MaxValue, It.IsAny<int>(), It.IsAny<bool>(),
            It.IsAny<bool>(), It.IsAny<DateTime?>(), It.IsAny<Guid?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetRangeAnalytics_DefaultsToType1AdultPopulation()
    {
        SetupGlucose(new List<SensorGlucose>());
        SetupEmptyTreatments();
        _statsServiceMock
            .Setup(s => s.AnalyzeGlucoseDataExtended(
                It.IsAny<IEnumerable<SensorGlucose>>(),
                It.IsAny<IEnumerable<Bolus>>(),
                It.IsAny<IEnumerable<CarbIntake>>(),
                It.IsAny<DiabetesPopulation>(),
                It.IsAny<ExtendedAnalysisConfig?>()))
            .Returns(new ExtendedGlucoseAnalytics());
        _statsServiceMock
            .Setup(s => s.CalculateAveragedStats(It.IsAny<IEnumerable<SensorGlucose>>()))
            .Returns(new List<AveragedStats>());

        var controller = CreateController();

        await controller.GetRangeAnalytics(
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc));

        _statsServiceMock.Verify(s => s.AnalyzeGlucoseDataExtended(
            It.IsAny<IEnumerable<SensorGlucose>>(),
            It.IsAny<IEnumerable<Bolus>>(),
            It.IsAny<IEnumerable<CarbIntake>>(),
            DiabetesPopulation.Type1Adult,
            It.IsAny<ExtendedAnalysisConfig?>()), Times.Once);
    }

    private void SetupAnalysis()
    {
        _statsServiceMock
            .Setup(s => s.AnalyzeGlucoseDataExtended(
                It.IsAny<IEnumerable<SensorGlucose>>(),
                It.IsAny<IEnumerable<Bolus>>(),
                It.IsAny<IEnumerable<CarbIntake>>(),
                It.IsAny<DiabetesPopulation>(),
                It.IsAny<ExtendedAnalysisConfig?>()))
            .Returns(new ExtendedGlucoseAnalytics());
        _statsServiceMock
            .Setup(s => s.CalculateAveragedStats(It.IsAny<IEnumerable<SensorGlucose>>()))
            .Returns(new List<AveragedStats>());
    }

    [Fact]
    public async Task GetRangeAnalytics_WithTargetRangeSchedule_PopulatesPersonalRange()
    {
        SetupGlucose(new List<SensorGlucose>());
        SetupEmptyTreatments();
        SetupAnalysis();

        var scheduleEntries = new List<TargetRangeEntry>
        {
            new() { Time = "00:00", TimeAsSeconds = 0, Low = 80, High = 160 },
        };
        _activeProfileResolverMock
            .Setup(r => r.GetActiveProfileNameAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("Default");
        _targetRangeScheduleRepoMock
            .Setup(r => r.GetActiveAtAsync("Default", It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TargetRangeSchedule { Entries = scheduleEntries });
        _therapySettingsResolverMock
            .Setup(r => r.GetTimezoneAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync("UTC");

        var personalRange = new PersonalRangeTimeInRange { InRangePercent = 42, Entries = scheduleEntries };
        _statsServiceMock
            .Setup(s => s.CalculatePersonalRangeTime(
                It.IsAny<IEnumerable<SensorGlucose>>(), scheduleEntries, TimeZoneInfo.Utc))
            .Returns(personalRange);

        var controller = CreateController();

        var result = await controller.GetRangeAnalytics(
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc));

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var payload = ok.Value.Should().BeOfType<ReportAnalysisResult>().Subject;
        payload.PersonalRange.Should().BeSameAs(personalRange);
    }

    [Fact]
    public async Task GetRangeAnalytics_WhenTargetRangeFetchFails_StillReturnsBaseAnalytics()
    {
        SetupGlucose(new List<SensorGlucose>());
        SetupEmptyTreatments();
        SetupAnalysis();

        _activeProfileResolverMock
            .Setup(r => r.GetActiveProfileNameAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("Default");
        _targetRangeScheduleRepoMock
            .Setup(r => r.GetActiveAtAsync(It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("db down"));

        var controller = CreateController();

        var result = await controller.GetRangeAnalytics(
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc));

        // The personal range is optional garnish — its failure must not 400 the report.
        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var payload = ok.Value.Should().BeOfType<ReportAnalysisResult>().Subject;
        payload.PersonalRange.Should().BeNull();
        payload.Analysis.Should().NotBeNull();
    }

    [Fact]
    public async Task GetPunchCardData_UsesTherapyTimezoneForCalendarDayBuckets()
    {
        var reading = new SensorGlucose
        {
            Timestamp = new DateTime(2026, 6, 1, 22, 30, 0, DateTimeKind.Utc),
            Mgdl = 100,
        };
        DateTime? capturedFrom = null;
        DateTime? capturedTo = null;

        _therapySettingsResolverMock
            .Setup(r => r.GetTimezoneAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync("Europe/Stockholm");

        _glucoseRepoMock
            .Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(),
                It.IsAny<bool>(), It.IsAny<DateTime?>(), It.IsAny<Guid?>(),
                It.IsAny<CancellationToken>(), It.IsAny<Guid?>()))
            .Callback<DateTime?, DateTime?, string?, string?, int, int, bool, bool, DateTime?, Guid?, CancellationToken, Guid?>(
                (from, to, _, _, _, _, _, _, _, _, _, _) =>
                {
                    capturedFrom = from;
                    capturedTo = to;
                })
            .ReturnsAsync(new[] { reading });
        SetupEmptyTreatments();
        _statsServiceMock
            .Setup(s => s.CalculateTimeInRange(
                It.IsAny<IEnumerable<SensorGlucose>>(),
                It.IsAny<GlycemicThresholds?>()))
            .Returns(new TimeInRangeMetrics
            {
                Percentages = new TimeInRangePercentages { Target = 100 },
                Durations = new TimeInRangeDurations { Target = 5 },
                RangeStats = new TimeInRangeDetailedStats
                {
                    Target = new PeriodMetrics { PeriodName = "In Range", Mean = 100 },
                },
            });

        var controller = CreateController();

        var result = await controller.GetPunchCardData(
            new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 6, 2, 0, 0, 0, DateTimeKind.Utc));

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var payload = ok.Value.Should().BeOfType<PunchCardResponse>().Subject;
        var month = payload.Months.Should().ContainSingle().Subject;
        var juneFirst = month.Days.Should().ContainSingle(d => d.Date == "2026-06-01").Subject;
        var juneSecond = month.Days.Should().ContainSingle(d => d.Date == "2026-06-02").Subject;

        juneFirst.Entries.Should().BeEmpty();
        juneSecond.Entries.Should().ContainSingle(e => e.Mills == reading.Mills);
        capturedFrom.Should().Be(new DateTime(2026, 5, 31, 22, 0, 0, DateTimeKind.Utc));
        capturedTo.Should().Be(new DateTime(2026, 6, 2, 21, 59, 59, 999, DateTimeKind.Utc).AddTicks(9999));
    }
}
