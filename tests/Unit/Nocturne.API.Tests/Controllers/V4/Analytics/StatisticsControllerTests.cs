using Microsoft.Extensions.Time.Testing;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.API.Controllers.V4.Analytics;
using Nocturne.API.Controllers.V4.Base;
using Nocturne.API.Extensions;
using Nocturne.API.Services.Analytics;
using Nocturne.API.Services.Auth;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Clinical.Statistics;
using Nocturne.API.Services.Caching;
using Nocturne.API.Services.Devices;
using Nocturne.API.Services.Identity;
using Nocturne.API.Tests.Infrastructure;
using Nocturne.API.Tests.Services.Profiles.Resolvers;
using Nocturne.Core.Contracts.Analytics;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Profiles;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Basal;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Cache.Abstractions;
using Nocturne.Infrastructure.Data.Services;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4.Analytics;

[Trait("Category", "Unit")]
public partial class StatisticsControllerTests
{
    private readonly Mock<IStatisticsService> _statsServiceMock = new();
    private readonly Mock<ISensorGlucoseRepository> _glucoseRepoMock = new();
    private readonly Mock<IBolusRepository> _bolusRepoMock = new();
    private readonly Mock<ICarbIntakeRepository> _carbIntakeRepoMock = new();
    private readonly Mock<ITempBasalRepository> _tempBasalRepoMock = new();
    private readonly Mock<ITherapySettingsResolver> _therapySettingsResolverMock = new();
    private readonly Mock<ITargetRangeScheduleRepository> _targetRangeScheduleRepoMock = new();
    private readonly Mock<IBasalInjectionRepository> _basalInjectionRepoMock = new();
    private readonly Mock<IActiveProfileResolver> _activeProfileResolverMock = ActiveProfileResolverMock.WithDefault();
    private readonly Mock<IBasalSegmentService> _basalSegmentsMock = new();
    private readonly Mock<IPatientDeviceRepository> _patientDeviceRepoMock = new();
    private readonly Mock<IApsSnapshotRepository> _apsSnapshotRepoMock = new();
    private readonly Mock<IDeviceEventRepository> _deviceEventRepoMock = new();
    private readonly Mock<IAidMetricsService> _aidMetricsServiceMock = new();
    private readonly Mock<IStateSpanRepository> _stateSpanRepoMock = new();
    private readonly Mock<ITenantGlucoseUnits> _glucoseUnitsMock = new();
    private readonly Mock<ILogger<StatisticsController>> _loggerMock = new();
    private readonly Mock<ILogger<TenantClockResolver>> _clockLoggerMock = new();
    private static readonly DateTimeOffset DefaultNow = new(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public StatisticsControllerTests()
    {
        _therapySettingsResolverMock
            .Setup(r => r.GetTimezoneAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync("UTC");
        SetupOverrides();
    }

    private void SetupOverrides(params StateSpan[] overrides) =>
        _stateSpanRepoMock
            .Setup(r => r.GetByCategories(
                It.IsAny<IEnumerable<StateSpanCategory>>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<StateSpanCategory, List<StateSpan>> { [StateSpanCategory.Override] = [.. overrides] });

    private StatisticsController CreateController(
        ICanonicalGlucoseService? canonicalGlucose = null,
        IStatisticsService? statisticsService = null,
        ITenantCache? cache = null,
        ICategoryReadContext? categoryReadContext = null,
        TimeProvider? timeProvider = null)
    {
        _deviceEventRepoMock
            .Setup(r => r.GetAllCopiesAsync(
                It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<IReadOnlyCollection<DeviceEventType>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _apsSnapshotRepoMock
            .Setup(r => r.GetLoopCyclesAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var statistics = statisticsService ?? _statsServiceMock.Object;
        var canonical = canonicalGlucose ?? TestDoubles.CanonicalGlucosePassThrough.Create();
        var tenantClock = new TenantClockResolver(
            _therapySettingsResolverMock.Object,
            categoryReadContext ?? new CategoryReadContext(),
            _clockLoggerMock.Object);
        var clock = timeProvider ?? new FakeTimeProvider(DefaultNow);
        var insulinDelivery = new InsulinDeliveryReader(
            _bolusRepoMock.Object,
            _carbIntakeRepoMock.Object,
            _tempBasalRepoMock.Object,
            _basalInjectionRepoMock.Object,
            _stateSpanRepoMock.Object,
            _basalSegmentsMock.Object,
            _therapySettingsResolverMock.Object,
            statistics,
            clock);

        var controller = new StatisticsController(
            statistics,
            cache ?? TestTenantCache.Create(partition: null),
            Mock.Of<IProfileProjectionService>(),
            _stateSpanRepoMock.Object,
            _glucoseRepoMock.Object,
            _carbIntakeRepoMock.Object,
            new AidSystemMetricsReader(
                _patientDeviceRepoMock.Object,
                _apsSnapshotRepoMock.Object,
                _tempBasalRepoMock.Object,
                _deviceEventRepoMock.Object,
                _glucoseRepoMock.Object,
                canonical,
                _targetRangeScheduleRepoMock.Object,
                _activeProfileResolverMock.Object,
                statistics,
                _aidMetricsServiceMock.Object,
                new DeviceDateClockResolver(
                    TestDoubles.DeviceDateClockDoubles.Timelines([]),
                    tenantClock),
                TimeProvider.System),
            new RangeAnalyticsReader(
                _glucoseRepoMock.Object,
                _bolusRepoMock.Object,
                _carbIntakeRepoMock.Object,
                _patientDeviceRepoMock.Object,
                canonical,
                statistics,
                tenantClock,
                _activeProfileResolverMock.Object,
                _targetRangeScheduleRepoMock.Object,
                insulinDelivery,
                clock),
            insulinDelivery,
            _deviceEventRepoMock.Object,
            canonical,
            tenantClock,
            _glucoseUnitsMock.Object,
            clock,
            _loggerMock.Object);

        var httpContext = new DefaultHttpContext();
        httpContext.Items["GrantedScopes"] = new HashSet<string> { Scope.FullAccess };
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        return controller;
    }

    private void SetupGlucose(IEnumerable<SensorGlucose> readings) =>
        _glucoseRepoMock
            .Setup(r => r.GetForAnalyticsAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(),
                It.IsAny<DateTime?>(), It.IsAny<Guid?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(readings);

    private void SetupEmptyTreatments()
    {
        _bolusRepoMock
            .Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(),
                It.IsAny<BolusKind?>(),
                It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Bolus>());

        _carbIntakeRepoMock
            .Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(),
                It.IsAny<DateTime?>(), It.IsAny<Guid?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<CarbIntake>());

        _tempBasalRepoMock
            .Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<TempBasal>());

        _basalInjectionRepoMock
            .Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<BasalInjection>());

        _statsServiceMock
            .Setup(s => s.CalculateDailyBasalBolusRatios(
                It.IsAny<IEnumerable<Bolus>>(),
                It.IsAny<IEnumerable<Bolus>>(),
                It.IsAny<IEnumerable<TempBasal>>(),
                It.IsAny<DateTime>(),
                It.IsAny<long>(),
                It.IsAny<TimeZoneInfo?>(),
                It.IsAny<IEnumerable<BasalInjection>?>(),
                It.IsAny<IEnumerable<CarbIntake>?>()))
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
                It.IsAny<int>(),
                It.IsAny<DiabetesPopulation>(),
                It.IsAny<ExtendedAnalysisConfig?>(),
                It.IsAny<TimeZoneInfo?>()))
            .Callback<IEnumerable<SensorGlucose>, IEnumerable<Bolus>, IEnumerable<CarbIntake>, int, DiabetesPopulation, ExtendedAnalysisConfig?, TimeZoneInfo?>(
                (entries, _, _, _, _, _, _) => analysedEntries = entries.ToList())
            .Returns(analysis);
        _statsServiceMock
            .Setup(s => s.CalculateAveragedStats(It.IsAny<IEnumerable<SensorGlucose>>(), It.IsAny<TimeZoneInfo>()))
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
        _glucoseRepoMock.Verify(r => r.GetForAnalyticsAsync(
            It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
            It.IsAny<string?>(), It.IsAny<string?>(),
            int.MaxValue, It.IsAny<int>(), It.IsAny<bool>(),
            It.IsAny<DateTime?>(), It.IsAny<Guid?>(),
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
                It.IsAny<int>(),
                It.IsAny<DiabetesPopulation>(),
                It.IsAny<ExtendedAnalysisConfig?>(),
                It.IsAny<TimeZoneInfo?>()))
            .Returns(new ExtendedGlucoseAnalytics());
        _statsServiceMock
            .Setup(s => s.CalculateAveragedStats(It.IsAny<IEnumerable<SensorGlucose>>(), It.IsAny<TimeZoneInfo>()))
            .Returns(new List<AveragedStats>());

        var controller = CreateController();

        await controller.GetRangeAnalytics(
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc));

        _statsServiceMock.Verify(s => s.AnalyzeGlucoseDataExtended(
            It.IsAny<IEnumerable<SensorGlucose>>(),
            It.IsAny<IEnumerable<Bolus>>(),
            It.IsAny<IEnumerable<CarbIntake>>(),
            It.IsAny<int>(),
            DiabetesPopulation.Type1Adult,
            It.IsAny<ExtendedAnalysisConfig?>(),
            It.IsAny<TimeZoneInfo?>()), Times.Once);
    }

    private static readonly GlycemicThresholds BandThresholds = new();

    private void SetupAnalysis()
    {
        _statsServiceMock.SetupGet(s => s.HourlyBandThresholds).Returns(BandThresholds);
        _statsServiceMock
            .Setup(s => s.AnalyzeGlucoseDataExtended(
                It.IsAny<IEnumerable<SensorGlucose>>(),
                It.IsAny<IEnumerable<Bolus>>(),
                It.IsAny<IEnumerable<CarbIntake>>(),
                It.IsAny<int>(),
                It.IsAny<DiabetesPopulation>(),
                It.IsAny<ExtendedAnalysisConfig?>(),
                It.IsAny<TimeZoneInfo?>()))
            .Returns(new ExtendedGlucoseAnalytics());
        _statsServiceMock
            .Setup(s => s.CalculateAveragedStats(It.IsAny<IEnumerable<SensorGlucose>>(), It.IsAny<TimeZoneInfo>()))
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
    public async Task GetWeekdayAverages_PassesTheCanonicalReadingsAndTherapyTimezoneToTheService()
    {
        var readings = new List<SensorGlucose> { new() { Mgdl = 100 }, new() { Mgdl = 120 } };
        SetupGlucose(readings);
        // The canonical stream is what the service must see, not the raw multi-device fetch.
        var canonical = new List<SensorGlucose> { new() { Mgdl = 110 } };
        var canonicalGlucose = new Mock<ICanonicalGlucoseService>();
        canonicalGlucose
            .Setup(s => s.SelectAsync(It.IsAny<IReadOnlyList<SensorGlucose>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(canonical);
        _therapySettingsResolverMock
            .Setup(r => r.GetTimezoneAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync("Europe/Stockholm");

        var slots = new List<WeekdayGlucoseSlot> { new() { MinuteOfDay = 480 } };
        List<SensorGlucose>? usedEntries = null;
        TimeZoneInfo? usedTz = null;
        _statsServiceMock
            .Setup(s => s.CalculateWeekdayAverages(It.IsAny<IEnumerable<SensorGlucose>>(), It.IsAny<TimeZoneInfo>()))
            .Callback<IEnumerable<SensorGlucose>, TimeZoneInfo>((entries, tz) =>
            {
                usedEntries = entries.ToList();
                usedTz = tz;
            })
            .Returns(slots);

        var result = await CreateController(canonicalGlucose.Object).GetWeekdayAverages(
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 1, 8, 0, 0, 0, DateTimeKind.Utc));

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var payload = ok.Value.Should().BeOfType<WeekdayAveragesResponse>().Subject;
        payload.Slots.Should().BeEquivalentTo(slots);
        payload.TimeZoneUnavailableReason.Should().BeNull();
        usedEntries.Should().BeEquivalentTo(canonical);
        canonicalGlucose.Verify(
            s => s.SelectAsync(
                It.Is<IReadOnlyList<SensorGlucose>>(raw => raw.SequenceEqual(readings)),
                It.IsAny<CancellationToken>()),
            Times.Once);
        usedTz.Should().Be(TimeZoneHelper.GetTimeZoneInfoFromId("Europe/Stockholm"));
        usedTz!.BaseUtcOffset.Should().Be(TimeSpan.FromHours(1));
    }

    [Fact]
    public async Task GetRangeAnalytics_BucketsTheHourlyStatsOnTheTherapyTimezone()
    {
        SetupGlucose(new List<SensorGlucose>());
        SetupEmptyTreatments();
        SetupAnalysis();
        _therapySettingsResolverMock
            .Setup(r => r.GetTimezoneAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync("Europe/Stockholm");
        TimeZoneInfo? usedTz = null;
        _statsServiceMock
            .Setup(s => s.CalculateAveragedStats(It.IsAny<IEnumerable<SensorGlucose>>(), It.IsAny<TimeZoneInfo>()))
            .Callback<IEnumerable<SensorGlucose>, TimeZoneInfo?>((_, tz) => usedTz = tz)
            .Returns(new List<AveragedStats>());

        await CreateController().GetRangeAnalytics(
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc));

        usedTz.Should().Be(TimeZoneHelper.GetTimeZoneInfoFromId("Europe/Stockholm"));
    }

    [Fact]
    public async Task GetRangeAnalytics_WithoutATherapyTimezone_LeavesTheHourlyStatsOnEachReadingsOffset()
    {
        SetupGlucose(new List<SensorGlucose>());
        SetupEmptyTreatments();
        SetupAnalysis();
        _therapySettingsResolverMock
            .Setup(r => r.GetTimezoneAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);
        TimeZoneInfo? usedTz = TimeZoneInfo.Local;
        _statsServiceMock
            .Setup(s => s.CalculateAveragedStats(It.IsAny<IEnumerable<SensorGlucose>>(), It.IsAny<TimeZoneInfo?>()))
            .Callback<IEnumerable<SensorGlucose>, TimeZoneInfo?>((_, tz) => usedTz = tz)
            .Returns(new List<AveragedStats>());

        await CreateController().GetRangeAnalytics(
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc));

        usedTz.Should().BeNull();
    }

    private static List<TargetRangeEntry> FlatSchedule() =>
        [new() { Time = "00:00", TimeAsSeconds = 0, Low = 80, High = 160 }];

    private static List<TargetRangeEntry> DayNightSchedule() =>
    [
        new() { Time = "00:00", TimeAsSeconds = 0, Low = 100, High = 180 },
        new() { Time = "06:00", TimeAsSeconds = 6 * 3600, Low = 80, High = 160 },
    ];

    private async Task<ReportAnalysisResult> RangeAnalyticsWith(List<TargetRangeEntry> schedule, string? timezone)
    {
        SetupGlucose(new List<SensorGlucose>());
        SetupEmptyTreatments();
        SetupAnalysis();
        _activeProfileResolverMock
            .Setup(r => r.GetActiveProfileNameAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("Default");
        _targetRangeScheduleRepoMock
            .Setup(r => r.GetActiveAtAsync("Default", It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TargetRangeSchedule { Entries = schedule });
        _therapySettingsResolverMock
            .Setup(r => r.GetTimezoneAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(timezone);

        var result = await CreateController().GetRangeAnalytics(
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc));

        return result.Result.Should().BeOfType<OkObjectResult>().Subject.Value
            .Should().BeOfType<ReportAnalysisResult>().Subject;
    }

    [Fact]
    public async Task GetRangeAnalytics_WithAZone_CarriesNoUnavailableReason()
    {
        var schedule = DayNightSchedule();
        var range = new PersonalRangeTimeInRange { InRangePercent = 42, Entries = schedule };
        var stockholm = TimeZoneHelper.GetTimeZoneInfoFromId("Europe/Stockholm");
        _statsServiceMock
            .Setup(s => s.CalculatePersonalRangeTime(It.IsAny<IEnumerable<SensorGlucose>>(), schedule, stockholm))
            .Returns(range);

        var payload = await RangeAnalyticsWith(schedule, "Europe/Stockholm");

        payload.PersonalRange.Should().BeSameAs(range);
        payload.TimeZoneUnavailableReason.Should().BeNull();
    }

    [Theory]
    [InlineData(null, TimeZoneUnavailableReason.NotConfigured)]
    [InlineData("Invalid/Timezone", TimeZoneUnavailableReason.Unrecognised)]
    public async Task GetRangeAnalytics_WithoutAZone_PassesNoZoneToThePersonalRangeAndSaysWhy(
        string? timezone, TimeZoneUnavailableReason reason)
    {
        var schedule = DayNightSchedule();

        var payload = await RangeAnalyticsWith(schedule, timezone);

        payload.TimeZoneUnavailableReason.Should().Be(reason);
        _statsServiceMock.Verify(
            s => s.CalculatePersonalRangeTime(It.IsAny<IEnumerable<SensorGlucose>>(), schedule, null),
            Times.Once);
        _statsServiceMock.Verify(
            s => s.CalculatePersonalRangeTime(
                It.IsAny<IEnumerable<SensorGlucose>>(), It.IsAny<List<TargetRangeEntry>>(), It.IsNotNull<TimeZoneInfo>()),
            Times.Never);
    }

    [Fact]
    public async Task GetRangeAnalytics_WithoutAZone_AndAFlatSchedule_ComputesThePersonalRange()
    {
        var schedule = FlatSchedule();
        var range = new PersonalRangeTimeInRange { InRangePercent = 42, Entries = schedule };
        _statsServiceMock
            .Setup(s => s.CalculatePersonalRangeTime(It.IsAny<IEnumerable<SensorGlucose>>(), schedule, null))
            .Returns(range);

        var payload = await RangeAnalyticsWith(schedule, null);

        payload.PersonalRange.Should().BeSameAs(range);
        payload.TimeZoneUnavailableReason.Should().Be(TimeZoneUnavailableReason.NotConfigured);
    }

    [Fact]
    public async Task GetRangeAnalytics_WhenTheTimezoneLookupFails_PassesNoZoneAndWarns()
    {
        var schedule = FlatSchedule();
        SetupGlucose(new List<SensorGlucose>());
        SetupEmptyTreatments();
        SetupAnalysis();
        _activeProfileResolverMock
            .Setup(r => r.GetActiveProfileNameAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("Default");
        _targetRangeScheduleRepoMock
            .Setup(r => r.GetActiveAtAsync("Default", It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TargetRangeSchedule { Entries = schedule });
        _therapySettingsResolverMock
            .Setup(r => r.GetTimezoneAsync(null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("db down"));

        var result = await CreateController().GetRangeAnalytics(
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc));

        var payload = result.Result.Should().BeOfType<OkObjectResult>().Subject.Value
            .Should().BeOfType<ReportAnalysisResult>().Subject;
        payload.TimeZoneUnavailableReason.Should().Be(TimeZoneUnavailableReason.LookupFailed);
        _statsServiceMock.Verify(
            s => s.CalculatePersonalRangeTime(It.IsAny<IEnumerable<SensorGlucose>>(), schedule, null),
            Times.Once);
        _clockLoggerMock.Verify(
            l => l.Log(
                LogLevel.Warning, It.IsAny<EventId>(), It.IsAny<It.IsAnyType>(),
                It.IsAny<InvalidOperationException>(), It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task GetRangeAnalytics_WhenTheTimezoneLookupFails_StillReturnsTheReport()
    {
        SetupGlucose(new List<SensorGlucose>());
        SetupEmptyTreatments();
        SetupAnalysis();
        _therapySettingsResolverMock
            .Setup(r => r.GetTimezoneAsync(null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("db down"));
        TimeZoneInfo? usedTz = TimeZoneInfo.Local;
        _statsServiceMock
            .Setup(s => s.CalculateAveragedStats(It.IsAny<IEnumerable<SensorGlucose>>(), It.IsAny<TimeZoneInfo?>()))
            .Callback<IEnumerable<SensorGlucose>, TimeZoneInfo?>((_, tz) => usedTz = tz)
            .Returns(new List<AveragedStats>());

        var result = await CreateController().GetRangeAnalytics(
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc));

        result.Result.Should().BeOfType<OkObjectResult>();
        usedTz.Should().BeNull();
    }

    [Fact]
    public async Task GetRangeAnalytics_ResolvesTheTimezoneOnceForBothHourlyStatsAndPersonalRange()
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
            .ReturnsAsync("Europe/Stockholm");
        var stockholm = TimeZoneHelper.GetTimeZoneInfoFromId("Europe/Stockholm");

        await CreateController().GetRangeAnalytics(
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc));

        _therapySettingsResolverMock.Verify(
            r => r.GetTimezoneAsync(null, It.IsAny<CancellationToken>()), Times.Once);
        _statsServiceMock.Verify(
            s => s.CalculatePersonalRangeTime(It.IsAny<IEnumerable<SensorGlucose>>(), scheduleEntries, stockholm),
            Times.Once);
    }

    [Fact]
    public async Task GetRangeAnalytics_PublishesTheHourlyBandThresholds()
    {
        SetupGlucose(new List<SensorGlucose>());
        SetupEmptyTreatments();
        SetupAnalysis();

        var result = await CreateController().GetRangeAnalytics(
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc));

        var payload = result.Result.Should().BeOfType<OkObjectResult>().Subject.Value
            .Should().BeOfType<ReportAnalysisResult>().Subject;
        payload.HourlyBandThresholds.Should().BeSameAs(BandThresholds);
    }

    [Fact]
    public async Task GetHourlyPatterns_WithoutATherapyTimezone_LeavesTheHoursOnEachReadingsOffset()
    {
        SetupGlucose(new List<SensorGlucose>());
        _therapySettingsResolverMock
            .Setup(r => r.GetTimezoneAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);
        TimeZoneInfo? usedTz = TimeZoneInfo.Local;
        _statsServiceMock
            .Setup(s => s.CalculateHourlyPatterns(It.IsAny<IEnumerable<SensorGlucose>>(), It.IsAny<TimeZoneInfo?>()))
            .Callback<IEnumerable<SensorGlucose>, TimeZoneInfo?>((_, tz) => usedTz = tz)
            .Returns(new HourlyPatterns());

        var result = await CreateController().GetHourlyPatterns(
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc));

        var payload = result.Result.Should().BeOfType<OkObjectResult>().Subject.Value
            .Should().BeOfType<HourlyPatterns>().Subject;
        usedTz.Should().BeNull();
        payload.TimeZoneUnavailableReason.Should().Be(TimeZoneUnavailableReason.NotConfigured);
    }

    [Theory]
    [InlineData(true, TimeZoneUnavailableReason.Share)]
    [InlineData(false, TimeZoneUnavailableReason.NotConfigured)]
    public async Task GetHourlyPatterns_SaysAShareCannotSeeTheTimezoneRatherThanThatNoneIsSet(
        bool share, TimeZoneUnavailableReason expected)
    {
        SetupGlucose(new List<SensorGlucose>());
        var category = new CategoryReadContext();
        if (share)
            category.MarkShare();
        _therapySettingsResolverMock
            .Setup(r => r.GetTimezoneAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);
        _statsServiceMock
            .Setup(s => s.CalculateHourlyPatterns(It.IsAny<IEnumerable<SensorGlucose>>(), It.IsAny<TimeZoneInfo?>()))
            .Returns(new HourlyPatterns());

        var result = await CreateController(categoryReadContext: category).GetHourlyPatterns(
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc));

        result.Result.Should().BeOfType<OkObjectResult>().Subject.Value
            .Should().BeOfType<HourlyPatterns>().Subject
            .TimeZoneUnavailableReason.Should().Be(expected);
    }

    [Theory]
    [InlineData(false, TimeZoneUnavailableReason.Unrecognised)]
    [InlineData(true, TimeZoneUnavailableReason.Share)]
    public async Task GetHourlyPatterns_WithAStoredTimezoneThatDoesNotResolve_SaysItIsUnrecognised(
        bool share, TimeZoneUnavailableReason expected)
    {
        SetupGlucose(new List<SensorGlucose>());
        var category = new CategoryReadContext();
        if (share)
            category.MarkShare();
        _therapySettingsResolverMock
            .Setup(r => r.GetTimezoneAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync("Mars/Olympus_Mons");
        _statsServiceMock
            .Setup(s => s.CalculateHourlyPatterns(It.IsAny<IEnumerable<SensorGlucose>>(), It.IsAny<TimeZoneInfo?>()))
            .Returns(new HourlyPatterns());

        var result = await CreateController(categoryReadContext: category).GetHourlyPatterns(
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc));

        result.Result.Should().BeOfType<OkObjectResult>().Subject.Value
            .Should().BeOfType<HourlyPatterns>().Subject
            .TimeZoneUnavailableReason.Should().Be(expected);
    }

    [Fact]
    public async Task GetHourlyPatterns_WhenTheTimezoneLookupFails_SaysSo()
    {
        SetupGlucose(new List<SensorGlucose>());
        _therapySettingsResolverMock
            .Setup(r => r.GetTimezoneAsync(null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("db down"));
        _statsServiceMock
            .Setup(s => s.CalculateHourlyPatterns(It.IsAny<IEnumerable<SensorGlucose>>(), It.IsAny<TimeZoneInfo?>()))
            .Returns(new HourlyPatterns());

        var result = await CreateController().GetHourlyPatterns(
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc));

        result.Result.Should().BeOfType<OkObjectResult>().Subject.Value
            .Should().BeOfType<HourlyPatterns>().Subject
            .TimeZoneUnavailableReason.Should().Be(TimeZoneUnavailableReason.LookupFailed);
    }

    [Fact]
    public async Task CalculateAveragedStats_BucketsThePostedReadingsOnTheTherapyTimezone()
    {
        var posted = new[] { new SensorGlucose { Mgdl = 100 } };
        _therapySettingsResolverMock
            .Setup(r => r.GetTimezoneAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync("Australia/Sydney");
        TimeZoneInfo? usedTz = null;
        var averaged = new List<AveragedStats> { new() { Hour = 3 } };
        _statsServiceMock
            .Setup(s => s.CalculateAveragedStats(posted, It.IsAny<TimeZoneInfo>()))
            .Callback<IEnumerable<SensorGlucose>, TimeZoneInfo?>((_, tz) => usedTz = tz)
            .Returns(averaged);

        var result = await CreateController().CalculateAveragedStats(posted);

        result.Result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeSameAs(averaged);
        usedTz.Should().Be(TimeZoneHelper.GetTimeZoneInfoFromId("Australia/Sydney"));
    }

    [Fact]
    public async Task GetHourlyPatterns_PassesTheCanonicalReadingsAndTherapyTimezoneToTheService()
    {
        var readings = new List<SensorGlucose> { new() { Mgdl = 100 }, new() { Mgdl = 120 } };
        SetupGlucose(readings);
        var canonical = new List<SensorGlucose> { new() { Mgdl = 110 } };
        var canonicalGlucose = new Mock<ICanonicalGlucoseService>();
        canonicalGlucose
            .Setup(s => s.SelectAsync(It.IsAny<IReadOnlyList<SensorGlucose>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(canonical);
        _therapySettingsResolverMock
            .Setup(r => r.GetTimezoneAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync("Europe/Stockholm");

        var patterns = new HourlyPatterns();
        List<SensorGlucose>? usedEntries = null;
        TimeZoneInfo? usedTz = null;
        _statsServiceMock
            .Setup(s => s.CalculateHourlyPatterns(It.IsAny<IEnumerable<SensorGlucose>>(), It.IsAny<TimeZoneInfo>()))
            .Callback<IEnumerable<SensorGlucose>, TimeZoneInfo?>((entries, tz) =>
            {
                usedEntries = entries.ToList();
                usedTz = tz;
            })
            .Returns(patterns);

        var result = await CreateController(canonicalGlucose.Object).GetHourlyPatterns(
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc));

        result.Result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeSameAs(patterns);
        patterns.TimeZoneUnavailableReason.Should().BeNull();
        usedEntries.Should().BeEquivalentTo(canonical);
        usedTz.Should().Be(TimeZoneHelper.GetTimeZoneInfoFromId("Europe/Stockholm"));
        _glucoseRepoMock.Verify(r => r.GetForAnalyticsAsync(
            It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
            It.IsAny<string?>(), It.IsAny<string?>(),
            int.MaxValue, It.IsAny<int>(), It.IsAny<bool>(),
            It.IsAny<DateTime?>(), It.IsAny<Guid?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetBasalAnalysis_WithNoTempBasals_SynthesizesOneScheduledTempBasalPerProfileSegment()
    {
        var start = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
        var segments = new[]
        {
            new BasalSegment(Mills(start), Mills(start.AddHours(6)), 0.8, 0.8, "Default"),
            new BasalSegment(Mills(start.AddHours(6)), Mills(start.AddHours(18)), 1.2, 1.2, "Default"),
            new BasalSegment(Mills(start.AddHours(18)), Mills(start.AddDays(1)), 0.9, 0.9, "Default"),
        };

        SetupEmptyTreatments();
        _therapySettingsResolverMock
            .Setup(r => r.HasDataAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _basalSegmentsMock
            .Setup(s => s.GetSegmentsAsync(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .Returns(AsAsync(segments));

        List<TempBasal>? synthesized = null;
        _statsServiceMock
            .Setup(s => s.CalculateBasalAnalysis(
                It.IsAny<IEnumerable<TempBasal>>(),
                It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<long>(), It.IsAny<TimeZoneInfo?>()))
            .Callback<IEnumerable<TempBasal>, DateTime, DateTime, long, TimeZoneInfo?>(
                (tempBasals, _, _, _, _) => synthesized = tempBasals.ToList())
            .Returns(new BasalAnalysisResponse());

        await CreateController().GetBasalAnalysis(start, start.AddDays(1));

        synthesized.Should().NotBeNull();
        synthesized!.Should().OnlyContain(t => t.Origin == TempBasalOrigin.Scheduled);
        synthesized.Select(t => (t.StartTimestamp, t.EndTimestamp, t.Rate!.Value)).Should().Equal(
            segments.Select(s => (
                DateTimeOffset.FromUnixTimeMilliseconds(s.StartMills).UtcDateTime,
                (DateTime?)DateTimeOffset.FromUnixTimeMilliseconds(s.EndMills).UtcDateTime,
                s.UnitsPerHour)));
    }

    [Fact]
    public async Task GetBasalAnalysis_WithNoTempBasals_SynthesizesTheOverrideScaledSchedule()
    {
        var start = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
        SetupEmptyTreatments();
        _therapySettingsResolverMock
            .Setup(r => r.HasDataAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _basalSegmentsMock
            .Setup(s => s.GetSegmentsAsync(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .Returns(AsAsync([new BasalSegment(Mills(start), Mills(start.AddDays(1)), 1.0, 1.0, "Default")]));
        SetupOverrides(new StateSpan
        {
            Category = StateSpanCategory.Override,
            StartTimestamp = start.AddHours(-1),
            EndTimestamp = start.AddHours(6),
            Metadata = new Dictionary<string, object> { ["insulinNeedsScaleFactor"] = 0.5 },
        });
        List<TempBasal>? synthesized = null;
        _statsServiceMock
            .Setup(s => s.CalculateBasalAnalysis(
                It.IsAny<IEnumerable<TempBasal>>(),
                It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<long>(), It.IsAny<TimeZoneInfo?>()))
            .Callback<IEnumerable<TempBasal>, DateTime, DateTime, long, TimeZoneInfo?>(
                (tempBasals, _, _, _, _) => synthesized = tempBasals.ToList())
            .Returns(new BasalAnalysisResponse());

        await CreateController().GetBasalAnalysis(start, start.AddDays(1));

        synthesized.Should().NotBeNull();
        synthesized!.Select(t => (t.StartTimestamp, t.EndTimestamp, t.Rate!.Value)).Should().Equal(
            (start, (DateTime?)start.AddHours(6), 0.5),
            (start.AddHours(6), (DateTime?)start.AddDays(1), 1.0));
    }

    [Fact]
    public async Task GetInsulinDeliveryStatistics_FillsMissingScheduledRateUnderTheRequestToken()
    {
        var start = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
        using var cts = new CancellationTokenSource();
        SetupEmptyTreatments();
        _tempBasalRepoMock
            .Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<TempBasal>
            {
                new()
                {
                    StartTimestamp = start,
                    EndTimestamp = start.AddMinutes(30),
                    Rate = 1.5,
                    Origin = TempBasalOrigin.Manual,
                },
            });
        _therapySettingsResolverMock
            .Setup(r => r.HasDataAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _basalSegmentsMock
            .Setup(s => s.GetSegmentsAsync(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .Returns(AsAsync([]));

        await CreateController(statisticsService: new StatisticsService(TimeProvider.System))
            .GetInsulinDeliveryStatistics(start, start.AddDays(1), cancellationToken: cts.Token);

        _basalSegmentsMock.Verify(
            x => x.GetSegmentsAsync(Mills(start.AddHours(-24)), Mills(start.AddDays(1)), cts.Token), Times.Once);
        _stateSpanRepoMock.Verify(
            r => r.GetByCategories(
                It.IsAny<IEnumerable<StateSpanCategory>>(), start.AddHours(-24), start.AddDays(1), cts.Token),
            Times.Once);
    }

    [Fact]
    public async Task GetBasalAnalysis_FillsMissingScheduledRate_SoLegacyTempsCountAsHighAndLow()
    {
        var payload = await LegacyTempsAnalysisAsync();

        payload.TempBasalInfo.HighTemps.Should().Be(1);
        payload.TempBasalInfo.LowTemps.Should().Be(1);
    }

    [Fact]
    public async Task GetBasalAnalysis_FillsMissingScheduledRateFromTheOverrideScaledProfile()
    {
        var start = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
        SetupOverrides(new StateSpan
        {
            Category = StateSpanCategory.Override,
            StartTimestamp = start.AddHours(-1),
            EndTimestamp = start.AddHours(1),
            Metadata = new Dictionary<string, object> { ["insulinNeedsScaleFactor"] = 2.0 },
        });

        var payload = await LegacyTempsAnalysisAsync();

        payload.TempBasalInfo.HighTemps.Should().Be(0);
        payload.TempBasalInfo.LowTemps.Should().Be(2);
    }

    private async Task<BasalAnalysisResponse> LegacyTempsAnalysisAsync()
    {
        var start = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
        var tempBasals = new List<TempBasal>
        {
            new()
            {
                StartTimestamp = start,
                EndTimestamp = start.AddMinutes(30),
                Rate = 1.5,
                Origin = TempBasalOrigin.Manual,
            },
            new()
            {
                StartTimestamp = start.AddHours(2),
                EndTimestamp = start.AddHours(2).AddMinutes(30),
                Rate = 0.5,
                Origin = TempBasalOrigin.Manual,
            },
        };

        _tempBasalRepoMock
            .Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(tempBasals);
        _bolusRepoMock
            .Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(),
                It.IsAny<BolusKind?>(),
                It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Bolus>());
        _therapySettingsResolverMock
            .Setup(r => r.HasDataAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _basalSegmentsMock
            .Setup(s => s.GetSegmentsAsync(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .Returns(AsAsync([new BasalSegment(Mills(start.AddDays(-1)), Mills(start.AddDays(2)), 1.0, 1.0, "Default")]));

        var result = await CreateController(statisticsService: new StatisticsService(TimeProvider.System))
            .GetBasalAnalysis(start, start.AddDays(1));

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        return ok.Value.Should().BeOfType<BasalAnalysisResponse>().Subject;
    }

    [Fact]
    public async Task GetHourlyInsulinDelivery_WithBasalInjections_DoesNotSynthesizeScheduledBasal()
    {
        var start = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);

        SetupEmptyTreatments();
        _basalInjectionRepoMock
            .Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<BasalInjection> { new() { Timestamp = start, Units = 22 } });
        _therapySettingsResolverMock
            .Setup(r => r.HasDataAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _basalSegmentsMock
            .Setup(s => s.GetSegmentsAsync(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .Returns(AsAsync([new BasalSegment(Mills(start), Mills(start.AddDays(1)), 1.0, 1.0, "Default")]));

        List<TempBasal>? passed = null;
        _statsServiceMock
            .Setup(s => s.CalculateHourlyInsulinDelivery(
                It.IsAny<IEnumerable<TempBasal>>(), It.IsAny<IEnumerable<Bolus>>(),
                It.IsAny<IEnumerable<Bolus>>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<long>(),
                It.IsAny<TimeZoneInfo?>(), It.IsAny<IEnumerable<BasalInjection>?>(),
                It.IsAny<IEnumerable<BasalSegment>?>()))
            .Callback<IEnumerable<TempBasal>, IEnumerable<Bolus>, IEnumerable<Bolus>, DateTime, DateTime, long, TimeZoneInfo?, IEnumerable<BasalInjection>?, IEnumerable<BasalSegment>?>(
                (tempBasals, _, _, _, _, _, _, _, _) => passed = tempBasals.ToList())
            .Returns(new HourlyInsulinDeliveryResponse());

        await CreateController().GetHourlyInsulinDelivery(start, start.AddDays(1));

        passed.Should().BeEmpty(
            "MDI injections are already the day's basal, so a profile baseline on top would double-count it");
    }

    [Fact]
    public async Task GetHourlyInsulinDelivery_PassesTheProfileScheduleToTheService()
    {
        var start = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
        var schedule = new BasalSegment(Mills(start), Mills(start.AddDays(1)), 0.9, 0.9, "Default");

        SetupEmptyTreatments();
        _therapySettingsResolverMock
            .Setup(r => r.HasDataAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _basalSegmentsMock
            .Setup(s => s.GetSegmentsAsync(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .Returns(() => AsAsync([schedule]));

        List<BasalSegment>? passed = null;
        _statsServiceMock
            .Setup(s => s.CalculateHourlyInsulinDelivery(
                It.IsAny<IEnumerable<TempBasal>>(), It.IsAny<IEnumerable<Bolus>>(),
                It.IsAny<IEnumerable<Bolus>>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<long>(),
                It.IsAny<TimeZoneInfo?>(), It.IsAny<IEnumerable<BasalInjection>?>(),
                It.IsAny<IEnumerable<BasalSegment>?>()))
            .Callback<IEnumerable<TempBasal>, IEnumerable<Bolus>, IEnumerable<Bolus>, DateTime, DateTime, long, TimeZoneInfo?, IEnumerable<BasalInjection>?, IEnumerable<BasalSegment>?>(
                (_, _, _, _, _, _, _, _, segments) => passed = segments?.ToList())
            .Returns(new HourlyInsulinDeliveryResponse());

        await CreateController().GetHourlyInsulinDelivery(start, start.AddDays(1));

        passed.Should().Equal(schedule);
    }

    [Fact]
    public async Task GetHourlyInsulinDelivery_WithNoTherapySettings_PassesNoProfileSchedule()
    {
        var start = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
        SetupEmptyTreatments();

        List<BasalSegment>? passed = null;
        _statsServiceMock
            .Setup(s => s.CalculateHourlyInsulinDelivery(
                It.IsAny<IEnumerable<TempBasal>>(), It.IsAny<IEnumerable<Bolus>>(),
                It.IsAny<IEnumerable<Bolus>>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<long>(),
                It.IsAny<TimeZoneInfo?>(), It.IsAny<IEnumerable<BasalInjection>?>(),
                It.IsAny<IEnumerable<BasalSegment>?>()))
            .Callback<IEnumerable<TempBasal>, IEnumerable<Bolus>, IEnumerable<Bolus>, DateTime, DateTime, long, TimeZoneInfo?, IEnumerable<BasalInjection>?, IEnumerable<BasalSegment>?>(
                (_, _, _, _, _, _, _, _, segments) => passed = segments?.ToList())
            .Returns(new HourlyInsulinDeliveryResponse());

        await CreateController().GetHourlyInsulinDelivery(start, start.AddDays(1));

        passed.Should().BeEmpty();
    }

    [Fact]
    public async Task GetInsulinDeliveryStatistics_KeepsManualAndAlgorithmBolusesInTheirOwnArguments()
    {
        var start = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc);
        var manual = new List<Bolus> { new() { Timestamp = start, Insulin = 4.5 } };
        var algorithm = new List<Bolus>
        {
            new() { Timestamp = start.AddMinutes(5), Insulin = 0.15 },
            new() { Timestamp = start.AddMinutes(10), Insulin = 0.2 },
        };

        SetupEmptyTreatments();
        SetupBoluses(BolusKind.Manual, manual);
        SetupBoluses(BolusKind.Algorithm, algorithm);

        List<Bolus>? passedManual = null;
        List<Bolus>? passedAlgorithm = null;
        _statsServiceMock
            .Setup(s => s.CalculateInsulinDeliveryStatistics(
                It.IsAny<IEnumerable<Bolus>>(), It.IsAny<IEnumerable<Bolus>>(),
                It.IsAny<IEnumerable<TempBasal>>(), It.IsAny<IEnumerable<CarbIntake>>(),
                It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<long>(), It.IsAny<IEnumerable<BasalInjection>?>()))
            .Callback<IEnumerable<Bolus>, IEnumerable<Bolus>, IEnumerable<TempBasal>, IEnumerable<CarbIntake>, DateTime, DateTime, long, IEnumerable<BasalInjection>?>(
                (m, a, _, _, _, _, _, _) => { passedManual = m.ToList(); passedAlgorithm = a.ToList(); })
            .Returns(new InsulinDeliveryStatistics());

        await CreateController().GetInsulinDeliveryStatistics(start, start.AddDays(1));

        passedManual.Should().BeEquivalentTo(manual);
        passedAlgorithm.Should().BeEquivalentTo(algorithm);
    }

    [Fact]
    public async Task GetInsulinDeliveryStatistics_BolusArcPlusBasalArcsEqualTotalInsulinOnADayWithAutomaticBoluses()
    {
        var start = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc);
        var manual = new List<Bolus>
        {
            new() { Timestamp = start.AddHours(8), Insulin = 4.5 },
            new() { Timestamp = start.AddHours(12), Insulin = 3.25 },
            new() { Timestamp = start.AddHours(13), Insulin = 0 },
        };
        var algorithm = new List<Bolus>
        {
            new() { Timestamp = start.AddMinutes(5), Insulin = 0.15, Kind = BolusKind.Algorithm },
            new() { Timestamp = start.AddMinutes(10), Insulin = 0.2, Kind = BolusKind.Algorithm },
        };

        SetupEmptyTreatments();
        SetupBoluses(BolusKind.Manual, manual);
        SetupBoluses(BolusKind.Algorithm, algorithm);
        _tempBasalRepoMock
            .Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<TempBasal>
            {
                new()
                {
                    StartTimestamp = start,
                    EndTimestamp = start.AddHours(4),
                    Rate = 1.5,
                    ScheduledRate = 1.0,
                },
            });

        var stats = await DeliveryStatsAsync(start, Scope.ReportsRead, Scope.TreatmentsRead);

        stats.ManualBoluses.Should().HaveCount(2);
        stats.ManualBoluses!.Sum(b => b.Insulin).Should().BeApproximately(stats.ManualBolus, 1e-9);
        stats.ManualBolus.Should().Be(7.75);
        stats.AutomaticBolus.Should().Be(0.35);
        stats.TotalBolus.Should().Be(8.1);
        (stats.TotalBolus + stats.ScheduledBasal + stats.AdditionalBasal)
            .Should().BeApproximately(stats.TotalInsulin, 0.011);
        stats.ScheduledBasal.Should().Be(4);
        stats.AdditionalBasal.Should().Be(2);
        stats.TotalBasal.Should().Be(6);
        stats.TotalInsulin.Should().Be(14.1);
    }

    [Fact]
    public async Task GetInsulinDeliveryStatistics_KeepsTheBolusRecordsFromACallerWithoutTreatmentsRead()
    {
        var start = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc);
        SetupEmptyTreatments();
        SetupBoluses(BolusKind.Manual, new List<Bolus> { new() { Timestamp = start, Insulin = 2 } });
        SetupBoluses(BolusKind.Algorithm, new List<Bolus>());

        var withRecords = await DeliveryStatsAsync(start, Scope.ReportsRead, Scope.TreatmentsRead);
        var reportsOnly = await DeliveryStatsAsync(start, Scope.ReportsRead);

        withRecords.ManualBoluses.Should().ContainSingle();
        reportsOnly.ManualBoluses.Should().BeNull();
        reportsOnly.TotalBolus.Should().Be(2);
        reportsOnly.TotalInsulin.Should().Be(withRecords.TotalInsulin);
    }

    [Fact]
    public async Task GetInsulinDeliveryStatistics_LeavesTheBolusRecordsOutUnlessAsked()
    {
        var start = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc);
        SetupEmptyTreatments();
        SetupBoluses(BolusKind.Manual, new List<Bolus> { new() { Timestamp = start, Insulin = 2 } });
        SetupBoluses(BolusKind.Algorithm, new List<Bolus>());

        var result = await CreateController(statisticsService: new StatisticsService(TimeProvider.System))
            .GetInsulinDeliveryStatistics(start, start.AddDays(1));

        result.Result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<InsulinDeliveryStatistics>().Subject
            .ManualBoluses.Should().BeNull();
    }

    [Fact]
    public async Task GetInsulinDeliveryStatistics_WindowEndingAfterNow_StopsARunningTempAtNow()
    {
        var start = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc);
        SetupEmptyTreatments();
        _tempBasalRepoMock
            .Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<TempBasal>
            {
                new() { StartTimestamp = start.AddHours(10), Rate = 2.0, ScheduledRate = 1.0 },
            });
        var now = new FakeTimeProvider(new DateTimeOffset(start.AddHours(10).AddMinutes(30), TimeSpan.Zero));

        var result = await CreateController(statisticsService: new StatisticsService(TimeProvider.System), timeProvider: now)
            .GetInsulinDeliveryStatistics(start, start.AddDays(1));

        result.Result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<InsulinDeliveryStatistics>().Subject
            .TotalBasal.Should().BeApproximately(1.0, 1e-9);
    }

    [Fact]
    public async Task GetHourlyInsulinDelivery_FetchesUncapped()
    {
        var start = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc);
        SetupEmptyTreatments();
        _statsServiceMock
            .Setup(s => s.CalculateHourlyInsulinDelivery(
                It.IsAny<IEnumerable<TempBasal>>(), It.IsAny<IEnumerable<Bolus>>(),
                It.IsAny<IEnumerable<Bolus>>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<long>(),
                It.IsAny<TimeZoneInfo?>(), It.IsAny<IEnumerable<BasalInjection>?>(),
                It.IsAny<IEnumerable<BasalSegment>?>()))
            .Returns(new HourlyInsulinDeliveryResponse());

        await CreateController().GetHourlyInsulinDelivery(start, start.AddDays(90));

        VerifyBolusLimit(BolusKind.Manual, int.MaxValue);
        VerifyBolusLimit(BolusKind.Algorithm, int.MaxValue);
    }

    private static readonly DateTime DenseDay = new(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime LateRecordAt = new(2026, 4, 3, 12, 0, 0, DateTimeKind.Utc);
    private const string LateRecordDate = "2026-04-03";
    private const int OldTreatmentLimit = 10_000;
    private const int OldGlucoseLimit = 100_000;

    private readonly List<CancellationToken> _repositoryTokens = [];

    /// <summary>
    /// Fills <see cref="DenseDay"/> with as many records of each kind as the old fixed limits
    /// returned, then adds one record of each kind at <see cref="LateRecordAt"/>. The repository
    /// mocks page by limit and direction, so a capped ascending read never sees the late records.
    /// </summary>
    private void SetupHistoryPastTheOldLimits(int earlyGlucose = 0)
    {
        var glucose = Enumerable.Range(0, earlyGlucose)
            .Select(i => new SensorGlucose { Timestamp = DenseDay.AddMilliseconds(500 * i), Mgdl = 100 })
            .Append(new SensorGlucose { Timestamp = LateRecordAt, Mgdl = 150 })
            .ToList();
        var manual = Dense(i => new Bolus { Timestamp = DenseDay.AddSeconds(i), Insulin = 0.5 })
            .Append(new Bolus { Timestamp = LateRecordAt, Insulin = 5 })
            .ToList();
        var algorithm = Dense(i => new Bolus { Timestamp = DenseDay.AddSeconds(i), Insulin = 0.1 })
            .Append(new Bolus { Timestamp = LateRecordAt, Insulin = 0.3 })
            .ToList();
        var carbs = Dense(i => new CarbIntake { Timestamp = DenseDay.AddSeconds(i), Carbs = 1 })
            .Append(new CarbIntake { Timestamp = LateRecordAt, Carbs = 40 })
            .ToList();
        var tempBasals = Dense(i => new TempBasal
            {
                StartTimestamp = DenseDay.AddSeconds(i),
                EndTimestamp = DenseDay.AddSeconds(i + 1),
                Rate = 1,
                ScheduledRate = 1,
                Origin = TempBasalOrigin.Algorithm,
            })
            .Append(new TempBasal
            {
                StartTimestamp = LateRecordAt,
                EndTimestamp = LateRecordAt.AddHours(1),
                Rate = 2,
                ScheduledRate = 1,
                Origin = TempBasalOrigin.Algorithm,
            })
            .ToList();
        var injections = Dense(i => new BasalInjection { Timestamp = DenseDay.AddSeconds(i), Units = 1 })
            .Append(new BasalInjection { Timestamp = LateRecordAt, Units = 10 })
            .ToList();

        _glucoseRepoMock
            .Setup(r => r.GetForAnalyticsAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(),
                It.IsAny<DateTime?>(), It.IsAny<Guid?>(),
                It.IsAny<CancellationToken>(), It.IsAny<Guid?>()))
            .Returns((DateTime? _, DateTime? _, string? _, string? _, int limit, int _, bool descending,
                DateTime? _, Guid? _, CancellationToken ct, Guid? _) =>
                Page(glucose, limit, descending, ct));
        _bolusRepoMock
            .Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(),
                It.IsAny<BolusKind?>(),
                It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .Returns((DateTime? _, DateTime? _, string? _, string? _, int limit, int _, bool descending,
                BolusKind? kind, DateTime? _, Guid? _, CancellationToken ct) =>
                Page(kind == BolusKind.Algorithm ? algorithm : manual, limit, descending, ct));
        _carbIntakeRepoMock
            .Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(),
                It.IsAny<DateTime?>(), It.IsAny<Guid?>(),
                It.IsAny<CancellationToken>()))
            .Returns((DateTime? _, DateTime? _, string? _, string? _, int limit, int _, bool descending,
                DateTime? _, Guid? _, CancellationToken ct) =>
                Page(carbs, limit, descending, ct));
        _tempBasalRepoMock
            .Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .Returns((DateTime? _, DateTime? _, string? _, string? _, int limit, int _, bool descending,
                CancellationToken ct) =>
                Page(tempBasals, limit, descending, ct));
        _basalInjectionRepoMock
            .Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .Returns((DateTime? _, DateTime? _, string? _, string? _, int limit, int _, bool descending,
                CancellationToken ct) =>
                Page(injections, limit, descending, ct));

        static IEnumerable<T> Dense<T>(Func<int, T> create) => Enumerable.Range(0, OldTreatmentLimit).Select(create);
    }

    private Task<IEnumerable<T>> Page<T>(IEnumerable<T> records, int limit, bool descending, CancellationToken ct)
        where T : IV4Record
    {
        _repositoryTokens.Add(ct);
        var ordered = descending ? records.OrderByDescending(r => r.Mills) : records.OrderBy(r => r.Mills);
        return Task.FromResult<IEnumerable<T>>(ordered.Take(limit).ToList());
    }

    [Fact]
    public async Task GetDailyBasalBolusRatios_CountsInsulinAndCarbsPastTheOldLimitUnderTheRequestToken()
    {
        SetupHistoryPastTheOldLimits();
        using var cts = new CancellationTokenSource();

        var result = await CreateController(statisticsService: new StatisticsService(TimeProvider.System))
            .GetDailyBasalBolusRatios(DenseDay, DenseDay.AddDays(4), cts.Token);

        var payload = result.Result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<DailyBasalBolusRatioResponse>().Subject;
        var lateDay = payload.DailyData.Should().ContainSingle(d => d.Date == LateRecordDate).Subject;
        lateDay.Bolus.Should().Be(5.3);
        lateDay.ManualBolus.Should().Be(5);
        lateDay.AutomaticBolus.Should().Be(0.3);
        lateDay.Basal.Should().BeApproximately(12.0, 1e-9);
        lateDay.Carbs.Should().Be(40);

        _repositoryTokens.Should().HaveCount(5).And.OnlyContain(t => t == cts.Token);
        _therapySettingsResolverMock.Verify(r => r.GetTimezoneAsync(null, cts.Token), Times.Once);
    }

    [Fact]
    public async Task GetPunchCardData_IncludesGlucoseCarbsAndInsulinPastTheOldLimits()
    {
        SetupHistoryPastTheOldLimits(earlyGlucose: OldGlucoseLimit);
        var canonicalGlucose = TestDoubles.CanonicalGlucosePassThrough.Create();
        using var cts = new CancellationTokenSource();

        var result = await CreateController(canonicalGlucose, statisticsService: new StatisticsService(TimeProvider.System))
            .GetPunchCardData(DateOnly.FromDateTime(DenseDay), DateOnly.FromDateTime(LateRecordAt), cts.Token);

        var lateDay = result.Result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<PunchCardResponse>().Subject
            .Months.Should().ContainSingle().Subject
            .Days.Should().ContainSingle(d => d.Date == LateRecordDate).Subject;
        lateDay.TotalReadings.Should().Be(1);
        lateDay.AverageGlucose.Should().Be(150);
        lateDay.TotalCarbs.Should().Be(40);
        lateDay.TotalBolus.Should().Be(5.3);
        lateDay.TotalBasal.Should().BeApproximately(12.0, 1e-9);

        _repositoryTokens.Should().HaveCount(6).And.OnlyContain(t => t == cts.Token);
        _therapySettingsResolverMock.Verify(r => r.GetTimezoneAsync(null, cts.Token), Times.Once);
        Mock.Get(canonicalGlucose).Verify(
            s => s.SelectAsync(It.IsAny<IReadOnlyList<SensorGlucose>>(), cts.Token), Times.Once);
    }

    [Fact]
    public async Task GetInsulinDeliveryStatistics_CountsRecordsPastTheOldLimitUnderTheRequestToken()
    {
        SetupHistoryPastTheOldLimits();
        using var cts = new CancellationTokenSource();

        var result = await CreateController(statisticsService: new StatisticsService(TimeProvider.System))
            .GetInsulinDeliveryStatistics(DenseDay, DenseDay.AddDays(4), cancellationToken: cts.Token);

        var payload = result.Result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<InsulinDeliveryStatistics>().Subject;
        payload.CarbCount.Should().Be(OldTreatmentLimit + 1);
        payload.TotalCarbs.Should().Be(OldTreatmentLimit + 40);
        payload.ManualBolusCount.Should().Be(OldTreatmentLimit + 1);
        payload.AutomaticBolusCount.Should().Be(OldTreatmentLimit + 1);
        payload.BasalInjectionCount.Should().Be(OldTreatmentLimit + 1);
        payload.BasalCount.Should().Be(2 * (OldTreatmentLimit + 1));

        _repositoryTokens.Should().HaveCount(5).And.OnlyContain(t => t == cts.Token);
    }

    [Fact]
    public async Task GetBasalAnalysis_ReadsTempBasalsFromADayBeforeTheWindow_AndCountsOnlyTheTimeInsideIt()
    {
        var start = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
        DateTime? readFrom = null;
        _tempBasalRepoMock
            .Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .Callback<DateTime?, DateTime?, string?, string?, int, int, bool, CancellationToken>(
                (from, _, _, _, _, _, _, _) => readFrom = from)
            .ReturnsAsync(new List<TempBasal>
            {
                new()
                {
                    StartTimestamp = start.AddMinutes(-30),
                    EndTimestamp = start.AddMinutes(90),
                    Rate = 2.0,
                    Origin = TempBasalOrigin.Manual,
                    ScheduledRate = 1.0,
                },
            });

        var result = await CreateController(statisticsService: new StatisticsService(TimeProvider.System))
            .GetBasalAnalysis(start, start.AddDays(1));

        readFrom.Should().Be(start.AddHours(-24));
        var payload = result.Result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<BasalAnalysisResponse>().Subject;
        payload.Stats.TotalDelivered.Should().Be(3.0);
    }

    [Fact]
    public async Task GetBasalAnalysis_ForwardsTheRequestTokenToEveryFetch()
    {
        SetupHistoryPastTheOldLimits();
        using var cts = new CancellationTokenSource();

        var result = await CreateController(statisticsService: new StatisticsService(TimeProvider.System))
            .GetBasalAnalysis(DenseDay, DenseDay.AddDays(4), cts.Token);

        result.Result.Should().BeOfType<OkObjectResult>();
        _repositoryTokens.Should().HaveCount(1).And.OnlyContain(t => t == cts.Token);
        _therapySettingsResolverMock.Verify(r => r.GetTimezoneAsync(null, cts.Token), Times.Once);
    }

    [Fact]
    public async Task GetBasalAnalysis_WithNoTempBasals_ForwardsTheRequestTokenToTheScheduledFallback()
    {
        using var cts = new CancellationTokenSource();
        SetupEmptyTreatments();
        SetupScheduledFallbackFor(cts.Token);

        await CreateController(statisticsService: new StatisticsService(TimeProvider.System))
            .GetBasalAnalysis(DenseDay, DenseDay.AddDays(1), cts.Token);

        VerifyScheduledFallbackRanUnder(cts.Token);
        _therapySettingsResolverMock.Verify(r => r.GetTimezoneAsync(null, cts.Token), Times.Once);
    }

    [Fact]
    public async Task GetHourlyInsulinDelivery_ForwardsTheRequestTokenToEveryFetch()
    {
        SetupHistoryPastTheOldLimits();
        using var cts = new CancellationTokenSource();

        var result = await CreateController(statisticsService: new StatisticsService(TimeProvider.System))
            .GetHourlyInsulinDelivery(DenseDay, DenseDay.AddDays(4), cts.Token);

        result.Result.Should().BeOfType<OkObjectResult>();
        _repositoryTokens.Should().HaveCount(4).And.OnlyContain(t => t == cts.Token);
        _therapySettingsResolverMock.Verify(r => r.GetTimezoneAsync(null, cts.Token), Times.Once);
    }

    [Fact]
    public async Task GetHourlyInsulinDelivery_WithNoRecords_ForwardsTheRequestTokenToTheScheduledFallback()
    {
        using var cts = new CancellationTokenSource();
        SetupEmptyTreatments();
        SetupScheduledFallbackFor(cts.Token);

        await CreateController(statisticsService: new StatisticsService(TimeProvider.System))
            .GetHourlyInsulinDelivery(DenseDay, DenseDay.AddDays(1), cts.Token);

        VerifyScheduledFallbackRanUnder(cts.Token);
        _therapySettingsResolverMock.Verify(r => r.GetTimezoneAsync(null, cts.Token), Times.Once);
    }

    private void SetupScheduledFallbackFor(CancellationToken token)
    {
        _therapySettingsResolverMock
            .Setup(r => r.HasDataAsync(token))
            .ReturnsAsync(true);
        _basalSegmentsMock
            .Setup(s => s.GetSegmentsAsync(It.IsAny<long>(), It.IsAny<long>(), token))
            .Returns(AsAsync([new BasalSegment(Mills(DenseDay), Mills(DenseDay.AddDays(1)), 1.0, 1.0, "Default")]));
    }

    private void VerifyScheduledFallbackRanUnder(CancellationToken token)
    {
        _stateSpanRepoMock.Verify(
            r => r.GetByCategories(
                It.IsAny<IEnumerable<StateSpanCategory>>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), token),
            Times.Once);
        _therapySettingsResolverMock.Verify(r => r.HasDataAsync(token), Times.Once);
        _basalSegmentsMock.Verify(
            s => s.GetSegmentsAsync(It.IsAny<long>(), It.IsAny<long>(), token), Times.Once);
    }

    [Fact]
    public async Task GetMultiPeriodStatistics_WithOnlyProfileBasal_CountsTheProfileAsScheduledBasal()
    {
        var start = DefaultNow.UtcDateTime.AddDays(-1);
        SetupMultiPeriodWithProfile(
            [
                new BasalSegment(Mills(start), Mills(start.AddHours(6)), 0.8, 0.8, "Default"),
                new BasalSegment(Mills(start.AddHours(6)), Mills(start.AddHours(18)), 1.2, 1.2, "Default"),
                new BasalSegment(Mills(start.AddHours(18)), Mills(start.AddDays(1)), 0.9, 0.9, "Default"),
            ]);
        SetupBoluses(BolusKind.Manual,
        [
            new() { Timestamp = start.AddHours(2), Insulin = 4.5 },
            new() { Timestamp = start.AddHours(8), Insulin = 3.2 },
        ]);

        var delivery = await MultiPeriodLastDayDelivery();

        delivery.TotalBasal.Should().Be(24.6);
        delivery.ScheduledBasal.Should().Be(24.6);
        delivery.AdditionalBasal.Should().Be(0);
        delivery.BasalCount.Should().Be(3);
        delivery.InsulinEventCount.Should().Be(5);
        delivery.TotalInsulin.Should().Be(32.3);
        delivery.Tdd.Should().Be(32.3);
        delivery.BasalPercent.Should().Be(76.2);
        delivery.BolusPercent.Should().Be(23.8);
    }

    [Fact]
    public async Task GetMultiPeriodStatistics_WithOnlyProfileBasal_ScalesTheProfileByAnActiveOverride()
    {
        var start = DefaultNow.UtcDateTime.AddDays(-1);
        SetupMultiPeriodWithProfile(
            [
                new BasalSegment(Mills(start), Mills(start.AddHours(6)), 0.8, 0.8, "Default"),
                new BasalSegment(Mills(start.AddHours(6)), Mills(start.AddHours(18)), 1.2, 1.2, "Default"),
                new BasalSegment(Mills(start.AddHours(18)), Mills(start.AddDays(1)), 0.9, 0.9, "Default"),
            ]);
        SetupOverrides(new StateSpan
        {
            Category = StateSpanCategory.Override,
            StartTimestamp = start.AddHours(-1),
            EndTimestamp = start.AddHours(6),
            Metadata = new Dictionary<string, object> { ["insulinNeedsScaleFactor"] = 0.5 },
        });

        var delivery = await MultiPeriodLastDayDelivery();

        delivery.TotalBasal.Should().Be(22.2);
        delivery.ScheduledBasal.Should().Be(22.2);
        _stateSpanRepoMock.Verify(
            r => r.GetByCategories(
                It.IsAny<IEnumerable<StateSpanCategory>>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task GetMultiPeriodStatistics_DoesNotCountAZeroRateProfileSegmentAsABasalEvent()
    {
        var start = DefaultNow.UtcDateTime.AddDays(-1);
        SetupMultiPeriodWithProfile(
            [
                new BasalSegment(Mills(start), Mills(start.AddHours(12)), 0.8, 0.8, "Default"),
                new BasalSegment(Mills(start.AddHours(12)), Mills(start.AddDays(1)), 0, 0, "Default"),
            ]);

        var delivery = await MultiPeriodLastDayDelivery();

        delivery.ScheduledBasal.Should().Be(9.6);
        delivery.BasalCount.Should().Be(1);
        delivery.InsulinEventCount.Should().Be(1);
    }

    [Fact]
    public async Task GetMultiPeriodStatistics_DerivesTddFromTheUnroundedProfileBasal()
    {
        var start = DefaultNow.UtcDateTime.AddDays(-1);
        SetupMultiPeriodWithProfile(
            [new BasalSegment(Mills(start), Mills(start) + 44_117_640, 1.0, 1.0, "Default")]);

        var delivery = await MultiPeriodLastDayDelivery();

        delivery.TotalBasal.Should().Be(12.25);
        delivery.Tdd.Should().Be(12.3, "12.2549 U rounds to 12.3, where the 2dp total 12.25 would round to even");
    }

    [Fact]
    public async Task GetMultiPeriodStatistics_WithAlgorithmBolusesAndNoTempBasals_CountsTheProfileAsBasalAndTheBolusesAsBolus()
    {
        var start = DefaultNow.UtcDateTime.AddDays(-1);
        SetupMultiPeriodWithProfile(
            [new BasalSegment(Mills(start), Mills(start.AddDays(1)), 1.0, 1.0, "Default")]);
        SetupBoluses(BolusKind.Algorithm,
        [
            new() { Timestamp = start.AddHours(2), Insulin = 0.15, Automatic = true },
            new() { Timestamp = start.AddHours(3), Insulin = 0.2, Automatic = true },
        ]);

        var delivery = await MultiPeriodLastDayDelivery();

        delivery.TotalBasal.Should().Be(24);
        delivery.ScheduledBasal.Should().Be(24);
        delivery.AdditionalBasal.Should().Be(0);
        delivery.AutomaticBolus.Should().Be(0.35);
        delivery.TotalBolus.Should().Be(0.35);
        delivery.AutomaticBolusCount.Should().Be(2);
    }

    [Fact]
    public async Task GetMultiPeriodStatistics_WithBasalInjections_DoesNotFallBackToProfileBasal()
    {
        var start = DefaultNow.UtcDateTime.AddDays(-1);
        SetupMultiPeriodWithProfile(
            [new BasalSegment(Mills(start), Mills(start.AddDays(1)), 1.0, 1.0, "Default")]);
        _basalInjectionRepoMock
            .Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<BasalInjection> { new() { Timestamp = start.AddHours(1), Units = 22 } });

        var delivery = await MultiPeriodLastDayDelivery();

        delivery.ScheduledBasal.Should().Be(22);
        delivery.TotalBasal.Should().Be(22);
        _basalSegmentsMock.Verify(
            s => s.GetSegmentsAsync(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task GetMultiPeriodStatistics_WithTempBasals_DoesNotFallBackToProfileBasal()
    {
        var start = DefaultNow.UtcDateTime.AddHours(-23);
        SetupMultiPeriodWithProfile(
            [new BasalSegment(Mills(start), Mills(start.AddDays(1)), 1.0, 1.0, "Default")]);
        _tempBasalRepoMock
            .Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<TempBasal>
            {
                new()
                {
                    StartTimestamp = start,
                    EndTimestamp = start.AddHours(2),
                    Rate = 1.5,
                    Origin = TempBasalOrigin.Manual,
                },
            });

        var delivery = await MultiPeriodLastDayDelivery();

        delivery.TotalBasal.Should().Be(3);
        delivery.BasalCount.Should().Be(1);
        _basalSegmentsMock.Verify(
            s => s.GetSegmentsAsync(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    private void SetupTempBasals(params TempBasal[] tempBasals) =>
        _tempBasalRepoMock
            .Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(tempBasals.ToList());

    [Fact]
    public async Task GetMultiPeriodStatistics_WithOnlyATempBeforeTheWindow_FallsBackToProfileBasal()
    {
        var start = DefaultNow.UtcDateTime.AddHours(-24);
        SetupMultiPeriodWithProfile(
            [new BasalSegment(Mills(start), Mills(start.AddDays(1)), 1.0, 1.0, "Default")]);
        SetupTempBasals(new TempBasal
        {
            StartTimestamp = start.AddHours(-6),
            EndTimestamp = start.AddHours(-2),
            Rate = 1.5,
            Origin = TempBasalOrigin.Manual,
        });

        var delivery = await MultiPeriodLastDayDelivery();

        delivery.TotalBasal.Should().Be(24);
    }

    [Fact]
    public async Task GetMultiPeriodStatistics_TempStartedBeforeThePeriod_CountsOnlyTheTimeInsideIt()
    {
        var start = DefaultNow.UtcDateTime.AddHours(-24);
        SetupMultiPeriodWithProfile([]);
        SetupTempBasals(new TempBasal
        {
            StartTimestamp = start.AddMinutes(-30),
            EndTimestamp = start.AddMinutes(90),
            Rate = 2.0,
            Origin = TempBasalOrigin.Manual,
        });

        var delivery = await MultiPeriodLastDayDelivery();

        delivery.TotalBasal.Should().Be(3);
    }

    [Fact]
    public async Task GetDailyBasalBolusRatios_TempStartedBeforeTheWindow_CountsOnlyTheTimeInsideItOnTheFirstDay()
    {
        var start = new DateTime(2026, 3, 2, 0, 0, 0, DateTimeKind.Utc);
        SetupEmptyTreatments();
        SetupTempBasals(new TempBasal
        {
            StartTimestamp = start.AddMinutes(-30),
            EndTimestamp = start.AddMinutes(90),
            Rate = 2.0,
            Origin = TempBasalOrigin.Manual,
        });

        var result = await CreateController(statisticsService: new StatisticsService(TimeProvider.System))
            .GetDailyBasalBolusRatios(start, start.AddDays(1));

        var payload = result.Result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<DailyBasalBolusRatioResponse>().Subject;
        payload.DailyData.Select(d => (d.Date, d.Basal)).Should().Equal(("2026-03-02", 3.0));
    }

    [Fact]
    public async Task GetBasalAnalysis_ReadsTheScheduleAndOverridesFromTheTempBasalReadStart()
    {
        var start = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
        using var cts = new CancellationTokenSource();
        SetupEmptyTreatments();
        _tempBasalRepoMock
            .Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<TempBasal>
            {
                new()
                {
                    StartTimestamp = start,
                    EndTimestamp = start.AddMinutes(30),
                    Rate = 1.5,
                    Origin = TempBasalOrigin.Manual,
                },
            });
        _therapySettingsResolverMock
            .Setup(r => r.HasDataAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _basalSegmentsMock
            .Setup(x => x.GetSegmentsAsync(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .Returns(AsAsync([]));

        await CreateController(statisticsService: new StatisticsService(TimeProvider.System))
            .GetBasalAnalysis(start, start.AddDays(1), cts.Token);

        _basalSegmentsMock.Verify(
            x => x.GetSegmentsAsync(Mills(start.AddHours(-24)), Mills(start.AddDays(1)), cts.Token),
            Times.Once);
        _stateSpanRepoMock.Verify(
            r => r.GetByCategories(
                It.IsAny<IEnumerable<StateSpanCategory>>(), start.AddHours(-24), start.AddDays(1), cts.Token),
            Times.Once);
    }

    private void SetupMultiPeriodWithProfile(BasalSegment[] segments)
    {
        var now = DefaultNow.UtcDateTime;
        SetupGlucose(Enumerable.Range(1, 12)
            .Select(i => new SensorGlucose { Timestamp = now.AddMinutes(-5 * i), Mgdl = 120 })
            .ToList());
        SetupEmptyTreatments();
        _therapySettingsResolverMock
            .Setup(r => r.HasDataAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _basalSegmentsMock
            .Setup(s => s.GetSegmentsAsync(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .Returns(() => AsAsync(segments));
    }

    [Fact]
    public async Task GetMultiPeriodStatistics_ScalesTheRecommendedMinimumToEachPeriod()
    {
        SetupMultiPeriodWithProfile([]);

        var result = await CreateController(statisticsService: new StatisticsService(TimeProvider.System))
            .GetMultiPeriodStatistics();

        var payload = result.Result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<MultiPeriodStatistics>().Subject;
        var periods = new[] { payload.LastDay, payload.Last3Days, payload.LastWeek, payload.LastMonth, payload.Last90Days };
        periods.Select(p => p.Reliability!.RecommendedMinimumDays).Should().Equal(1, 3, 7, 14, 14);
    }

    [Fact]
    public async Task AnalyzeGlucoseDataExtended_PassesTheRequestedWindowAndTheTenantZone()
    {
        _therapySettingsResolverMock
            .Setup(r => r.GetTimezoneAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync("Australia/Sydney");
        _statsServiceMock
            .Setup(s => s.AnalyzeGlucoseDataExtended(
                It.IsAny<IEnumerable<SensorGlucose>>(),
                It.IsAny<IEnumerable<Bolus>>(),
                It.IsAny<IEnumerable<CarbIntake>>(),
                It.IsAny<int>(),
                It.IsAny<DiabetesPopulation>(),
                It.IsAny<ExtendedAnalysisConfig?>(),
                It.IsAny<TimeZoneInfo?>()))
            .Returns(new ExtendedGlucoseAnalytics());

        await CreateController().AnalyzeGlucoseDataExtended(new ExtendedGlucoseAnalyticsRequest
        {
            StartDate = new DateTime(2026, 3, 9, 13, 0, 0, DateTimeKind.Utc),
            EndDate = new DateTime(2026, 3, 10, 13, 0, 0, DateTimeKind.Utc),
        });

        _statsServiceMock.Verify(s => s.AnalyzeGlucoseDataExtended(
            It.IsAny<IEnumerable<SensorGlucose>>(),
            It.IsAny<IEnumerable<Bolus>>(),
            It.IsAny<IEnumerable<CarbIntake>>(),
            1,
            It.IsAny<DiabetesPopulation>(),
            It.IsAny<ExtendedAnalysisConfig?>(),
            It.Is<TimeZoneInfo?>(z => z != null && z.Id == "Australia/Sydney")), Times.Once);
    }

    [Fact]
    public async Task GetRangeAnalytics_PassesTheRequestedWindowAndTheTenantZone()
    {
        SetupGlucose(new List<SensorGlucose>());
        SetupEmptyTreatments();
        SetupAnalysis();
        _therapySettingsResolverMock
            .Setup(r => r.GetTimezoneAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync("Australia/Sydney");

        await CreateController().GetRangeAnalytics(
            new DateTime(2026, 2, 28, 13, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 3, 7, 12, 59, 59, 999, DateTimeKind.Utc));

        _statsServiceMock.Verify(s => s.AnalyzeGlucoseDataExtended(
            It.IsAny<IEnumerable<SensorGlucose>>(),
            It.IsAny<IEnumerable<Bolus>>(),
            It.IsAny<IEnumerable<CarbIntake>>(),
            7,
            It.IsAny<DiabetesPopulation>(),
            It.IsAny<ExtendedAnalysisConfig?>(),
            It.Is<TimeZoneInfo?>(z => z != null && z.Id == "Australia/Sydney")), Times.Once);
    }

    [Fact]
    public async Task AnalyzeGlucoseDataExtended_WithNoWindow_PassesTheClinicalMinimum()
    {
        _statsServiceMock
            .Setup(s => s.AnalyzeGlucoseDataExtended(
                It.IsAny<IEnumerable<SensorGlucose>>(),
                It.IsAny<IEnumerable<Bolus>>(),
                It.IsAny<IEnumerable<CarbIntake>>(),
                It.IsAny<int>(),
                It.IsAny<DiabetesPopulation>(),
                It.IsAny<ExtendedAnalysisConfig?>(),
                It.IsAny<TimeZoneInfo?>()))
            .Returns(new ExtendedGlucoseAnalytics());

        await CreateController().AnalyzeGlucoseDataExtended(new ExtendedGlucoseAnalyticsRequest());

        _statsServiceMock.Verify(s => s.AnalyzeGlucoseDataExtended(
            It.IsAny<IEnumerable<SensorGlucose>>(),
            It.IsAny<IEnumerable<Bolus>>(),
            It.IsAny<IEnumerable<CarbIntake>>(),
            StatisticsService.ClinicalMinimumDays,
            It.IsAny<DiabetesPopulation>(),
            It.IsAny<ExtendedAnalysisConfig?>(),
            It.IsAny<TimeZoneInfo?>()), Times.Once);
    }

    private async Task<InsulinDeliveryStatistics> DeliveryStatsAsync(DateTime start, params string[] grantedScopes)
    {
        var controller = CreateController(statisticsService: new StatisticsService(TimeProvider.System));
        controller.HttpContext.Items[AuthContextKeys.GrantedScopes] =
            (IReadOnlySet<string>)new HashSet<string>(grantedScopes);

        var result = await controller.GetInsulinDeliveryStatistics(start, start.AddDays(1), includeBoluses: true);

        return result.Result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<InsulinDeliveryStatistics>().Subject;
    }

    private async Task<InsulinDeliveryStatistics> MultiPeriodLastDayDelivery()
    {
        var result = await CreateController(statisticsService: new StatisticsService(TimeProvider.System))
            .GetMultiPeriodStatistics();

        var payload = result.Result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<MultiPeriodStatistics>().Subject;
        return payload.LastDay!.InsulinDelivery!;
    }

    private void SetupBoluses(BolusKind kind, IEnumerable<Bolus> boluses) =>
        _bolusRepoMock
            .Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(),
                kind,
                It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(boluses);

    private void VerifyBolusLimit(BolusKind kind, int limit) =>
        _bolusRepoMock.Verify(r => r.GetAsync(
            It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
            It.IsAny<string?>(), It.IsAny<string?>(),
            limit, It.IsAny<int>(), It.IsAny<bool>(),
            kind,
            It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Once);

    private static long Mills(DateTime utc) => new DateTimeOffset(utc, TimeSpan.Zero).ToUnixTimeMilliseconds();

    private static async IAsyncEnumerable<BasalSegment> AsAsync(IEnumerable<BasalSegment> segments)
    {
        foreach (var segment in segments)
        {
            yield return segment;
        }

        await Task.CompletedTask;
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
            .Setup(r => r.GetForAnalyticsAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(),
                It.IsAny<DateTime?>(), It.IsAny<Guid?>(),
                It.IsAny<CancellationToken>(), It.IsAny<Guid?>()))
            .Callback<DateTime?, DateTime?, string?, string?, int, int, bool, DateTime?, Guid?, CancellationToken, Guid?>(
                (from, to, _, _, _, _, _, _, _, _, _) =>
                {
                    capturedFrom = from;
                    capturedTo = to;
                })
            .ReturnsAsync(new[] { reading });
        SetupEmptyTreatments();

        var controller = CreateController(statisticsService: new StatisticsService(TimeProvider.System));

        var result = await controller.GetPunchCardData(
            new DateOnly(2026, 6, 1),
            new DateOnly(2026, 6, 2));

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

    [Fact]
    public async Task GetPunchCardData_EastOfUtcTenantWindowStartsAtTheLocalFirstDay()
    {
        DateTime? capturedFrom = null;
        DateTime? capturedTo = null;

        _therapySettingsResolverMock
            .Setup(r => r.GetTimezoneAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync("Australia/Sydney");

        _glucoseRepoMock
            .Setup(r => r.GetForAnalyticsAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(),
                It.IsAny<DateTime?>(), It.IsAny<Guid?>(),
                It.IsAny<CancellationToken>(), It.IsAny<Guid?>()))
            .Callback<DateTime?, DateTime?, string?, string?, int, int, bool, DateTime?, Guid?, CancellationToken, Guid?>(
                (from, to, _, _, _, _, _, _, _, _, _) =>
                {
                    capturedFrom = from;
                    capturedTo = to;
                })
            .ReturnsAsync(Array.Empty<SensorGlucose>());
        SetupEmptyTreatments();

        var result = await CreateController(statisticsService: new StatisticsService(TimeProvider.System))
            .GetPunchCardData(new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30));

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var payload = ok.Value.Should().BeOfType<PunchCardResponse>().Subject;

        capturedFrom.Should().Be(new DateTime(2026, 8, 31, 14, 0, 0, DateTimeKind.Utc));
        capturedTo.Should().Be(new DateTime(2026, 9, 30, 13, 59, 59, 999, DateTimeKind.Utc).AddTicks(9999));

        var month = payload.Months.Should().ContainSingle().Subject;
        month.Days.Should().ContainSingle(d => d.Date == "2026-09-01");
    }

    [Fact]
    public async Task GetPunchCardData_CountsTheReadingsThemselvesRatherThanDividingDurations()
    {
        // A one-minute sensor: 60 readings covering 60 minutes, which the old count of
        // duration-minutes over a five-minute interval would have reported as 12 readings.
        var dayStart = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var readings = Enumerable
            .Range(0, 60)
            .Select(i => new SensorGlucose { Timestamp = dayStart.AddMinutes(i), Mgdl = 100 })
            .ToArray();

        _glucoseRepoMock
            .Setup(r => r.GetForAnalyticsAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(),
                It.IsAny<DateTime?>(), It.IsAny<Guid?>(),
                It.IsAny<CancellationToken>(), It.IsAny<Guid?>()))
            .ReturnsAsync(readings);
        SetupEmptyTreatments();

        var result = await CreateController(statisticsService: new StatisticsService(TimeProvider.System)).GetPunchCardData(
            DateOnly.FromDateTime(dayStart),
            DateOnly.FromDateTime(dayStart.AddDays(1)));

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var payload = ok.Value.Should().BeOfType<PunchCardResponse>().Subject;
        var month = payload.Months.Should().ContainSingle().Subject;
        var day = month.Days.Should().ContainSingle(d => d.Date == "2026-06-01").Subject;

        day.TotalReadings.Should().Be(60);
        day.InRangeCount.Should().Be(60);
        month.TotalReadings.Should().Be(60);
        month.Summary!.TotalReadings.Should().Be(60);
    }

    [Fact]
    public async Task GetPunchCardData_ServesAllFiveBandsWithTheModerateBandsExcludingTheExtremes()
    {
        var dayStart = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var readings = Enumerable
            .Range(0, 100)
            .Select(i => new SensorGlucose
            {
                Timestamp = dayStart.AddMinutes(i),
                Mgdl = i switch { < 5 => 40, < 15 => 60, < 65 => 100, < 85 => 200, _ => 300 },
            })
            .ToArray();
        SetupPunchCardGlucose(readings);
        SetupEmptyTreatments();

        var result = await CreateController(statisticsService: new StatisticsService(TimeProvider.System)).GetPunchCardData(
            DateOnly.FromDateTime(dayStart), DateOnly.FromDateTime(dayStart));

        var month = result.Result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<PunchCardResponse>().Subject
            .Months.Should().ContainSingle().Subject;
        var day = month.Days.Should().ContainSingle().Subject;
        day.VeryLowPercent.Should().Be(5);
        day.LowPercent.Should().Be(10);
        day.InRangePercent.Should().Be(50);
        day.HighPercent.Should().Be(20);
        day.VeryHighPercent.Should().Be(15);
        month.Summary!.VeryLowPercent.Should().BeApproximately(5, 0.01);
        month.Summary.LowPercent.Should().BeApproximately(10, 0.01);
        month.Summary.HighPercent.Should().BeApproximately(20, 0.01);
        month.Summary.VeryHighPercent.Should().BeApproximately(15, 0.01);
    }

    [Fact]
    public async Task GetPunchCardData_AveragesEveryReadingOfTheDayAndWeightsTheMonthByReadings()
    {
        var dayStart = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var readings = new[]
        {
            new SensorGlucose { Timestamp = dayStart.AddHours(1), Mgdl = 60 },
            new SensorGlucose { Timestamp = dayStart.AddHours(2), Mgdl = 100 },
            new SensorGlucose { Timestamp = dayStart.AddHours(3), Mgdl = 200 },
            new SensorGlucose { Timestamp = dayStart.AddDays(1).AddHours(1), Mgdl = 200 },
        };

        _glucoseRepoMock
            .Setup(r => r.GetForAnalyticsAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(),
                It.IsAny<DateTime?>(), It.IsAny<Guid?>(),
                It.IsAny<CancellationToken>(), It.IsAny<Guid?>()))
            .ReturnsAsync(readings);
        SetupEmptyTreatments();

        var result = await CreateController(statisticsService: new StatisticsService(TimeProvider.System)).GetPunchCardData(
            DateOnly.FromDateTime(dayStart),
            DateOnly.FromDateTime(dayStart.AddDays(1)));

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var month = ok.Value.Should().BeOfType<PunchCardResponse>().Subject.Months.Should().ContainSingle().Subject;

        month.Days.Single(d => d.Date == "2026-06-01").AverageGlucose.Should().Be(120);
        month.Days.Single(d => d.Date == "2026-06-02").AverageGlucose.Should().Be(200);
        // Four readings averaging 140, not the 160 an average of the two daily means would give.
        month.Summary!.AvgGlucose.Should().Be(140);
    }

    private const double DailyBolus = 10;
    private const double DailyBasalInjection = 20;
    private const double DailyCarbs = 50;

    private void SetupPunchCardGlucose(IEnumerable<SensorGlucose> glucose) =>
        _glucoseRepoMock
            .Setup(r => r.GetForAnalyticsAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(),
                It.IsAny<DateTime?>(), It.IsAny<Guid?>(),
                It.IsAny<CancellationToken>(), It.IsAny<Guid?>()))
            .ReturnsAsync(glucose);

    private void SetupDailyTreatmentsForJune(int firstDay, int lastDay, IEnumerable<SensorGlucose> glucose)
    {
        var days = Enumerable.Range(firstDay, lastDay - firstDay + 1)
            .Select(d => new DateTime(2026, 6, d, 12, 0, 0, DateTimeKind.Utc))
            .ToList();
        var boluses = days.Select(t => new Bolus { Timestamp = t, Insulin = DailyBolus }).ToList();
        var carbs = days.Select(t => new CarbIntake { Timestamp = t, Carbs = DailyCarbs }).ToList();
        var injections = days.Select(t => new BasalInjection { Timestamp = t, Units = DailyBasalInjection }).ToList();

        SetupEmptyTreatments();
        SetupPunchCardGlucose(glucose);
        _bolusRepoMock
            .Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(),
                BolusKind.Manual,
                It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(boluses);
        _carbIntakeRepoMock
            .Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(),
                It.IsAny<DateTime?>(), It.IsAny<Guid?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(carbs);
        _basalInjectionRepoMock
            .Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(injections);
    }

    private static IEnumerable<SensorGlucose> JuneReadings(int firstDay, int lastDay) =>
        Enumerable.Range(firstDay, lastDay - firstDay + 1)
            .Select(d => new SensorGlucose { Timestamp = new DateTime(2026, 6, d, 8, 0, 0, DateTimeKind.Utc), Mgdl = 110 });

    private async Task<PunchCardMonthSummary> JuneSummary(DateOnly from, DateOnly to, TimeProvider? timeProvider = null)
    {
        var result = await CreateController(statisticsService: new StatisticsService(TimeProvider.System), timeProvider: timeProvider)
            .GetPunchCardData(from, to);

        return result.Result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<PunchCardResponse>().Subject
            .Months.Should().ContainSingle().Subject
            .Summary!;
    }

    [Fact]
    public async Task GetPunchCardData_DividesTddAndCarbsByTheMonthsDaysNotTheDaysTheSensorRead()
    {
        SetupDailyTreatmentsForJune(1, 30, JuneReadings(1, 20));

        var summary = await JuneSummary(new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 30));

        summary.DayCount.Should().Be(20);
        summary.Tdd.Should().BeApproximately(DailyBolus + DailyBasalInjection, 1e-9);
        summary.AvgDailyCarbs.Should().BeApproximately(DailyCarbs, 1e-9);
    }

    [Theory]
    [InlineData(new[] { Scope.GlucoseRead }, false)]
    [InlineData(new[] { Scope.ReportsRead }, true)]
    [InlineData(new[] { Scope.GlucoseRead, Scope.TreatmentsRead }, true)]
    public async Task GetPunchCardData_ServesInsulinAndCarbAggregatesOnlyToReportsOrTreatmentsRead(string[] granted, bool served)
    {
        SetupDailyTreatmentsForJune(1, 30, JuneReadings(1, 30));
        var controller = CreateController(statisticsService: new StatisticsService(TimeProvider.System));
        controller.HttpContext.Items[AuthContextKeys.GrantedScopes] =
            (IReadOnlySet<string>)new HashSet<string>(granted);

        var result = await controller.GetPunchCardData(new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 30));

        var summary = result.Result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<PunchCardResponse>().Subject
            .Months.Should().ContainSingle().Subject
            .Summary!;
        (summary.Tdd ?? 0).Should().BeApproximately(served ? DailyBolus + DailyBasalInjection : 0, 1e-9);
        (summary.AvgDailyCarbs ?? 0).Should().BeApproximately(served ? DailyCarbs : 0, 1e-9);
    }

    [Fact]
    public async Task GetPunchCardData_DividesTddAndCarbsByTheRangeDaysWhenTheMonthIsPartial()
    {
        SetupDailyTreatmentsForJune(1, 30, JuneReadings(1, 30));

        var summary = await JuneSummary(new DateOnly(2026, 6, 10), new DateOnly(2026, 6, 20));

        summary.DayCount.Should().Be(11);
        summary.Tdd.Should().BeApproximately(DailyBolus + DailyBasalInjection, 1e-9);
        summary.AvgDailyCarbs.Should().BeApproximately(DailyCarbs, 1e-9);
    }

    [Fact]
    public async Task GetPunchCardData_AveragesTheCurrentMonthOverTheDaysSoFarIncludingToday()
    {
        SetupDailyTreatmentsForJune(1, 19, JuneReadings(1, 19));
        var midMonth = new FakeTimeProvider(new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero));

        var summary = await JuneSummary(new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 30), midMonth);

        summary.Tdd.Should().BeApproximately(DailyBolus + DailyBasalInjection, 1e-9);
        summary.AvgDailyCarbs.Should().BeApproximately(DailyCarbs, 1e-9);
    }

    [Fact]
    public async Task GetPunchCardData_ReadsTodayInTheTenantTimezone()
    {
        _therapySettingsResolverMock
            .Setup(r => r.GetTimezoneAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync("Australia/Sydney");
        SetupDailyTreatmentsForJune(1, 19, JuneReadings(1, 19));
        // 20 June 00:30 in Sydney is still 19 June in UTC.
        var sydneyNextDay = new FakeTimeProvider(new DateTimeOffset(2026, 6, 19, 14, 30, 0, TimeSpan.Zero));

        var summary = await JuneSummary(new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 30), sydneyNextDay);

        summary.Tdd.Should().BeApproximately(19 * (DailyBolus + DailyBasalInjection) / 20, 1e-9);
    }

    [Fact]
    public async Task GetPunchCardData_SpreadsTreatmentOnSomeDaysAcrossEveryDayOfTheRange()
    {
        SetupDailyTreatmentsForJune(1, 10, JuneReadings(1, 30));

        var summary = await JuneSummary(new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 30));

        summary.Tdd.Should().BeApproximately(10 * (DailyBolus + DailyBasalInjection) / 30, 1e-9);
        summary.AvgDailyCarbs.Should().BeApproximately(10 * DailyCarbs / 30, 1e-9);
    }

    [Fact]
    public async Task GetPunchCardData_LeavesTddAndCarbsNullWhenNoneWereRecorded()
    {
        SetupEmptyTreatments();
        SetupPunchCardGlucose(JuneReadings(1, 3).ToArray());

        var summary = await JuneSummary(new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 30));

        summary.Tdd.Should().BeNull();
        summary.AvgDailyCarbs.Should().BeNull();
    }

    [Fact]
    public async Task GetPunchCardData_LeavesImplausibleReadingsOutOfTheDay()
    {
        var dayStart = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var readings = new[]
        {
            new SensorGlucose { Timestamp = dayStart.AddHours(1), Mgdl = 100 },
            new SensorGlucose { Timestamp = dayStart.AddHours(2), Mgdl = 700 },
            new SensorGlucose { Timestamp = dayStart.AddHours(3), Mgdl = double.NaN },
        };

        _glucoseRepoMock
            .Setup(r => r.GetForAnalyticsAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(),
                It.IsAny<DateTime?>(), It.IsAny<Guid?>(),
                It.IsAny<CancellationToken>(), It.IsAny<Guid?>()))
            .ReturnsAsync(readings);
        SetupEmptyTreatments();

        var result = await CreateController(statisticsService: new StatisticsService(TimeProvider.System)).GetPunchCardData(
            DateOnly.FromDateTime(dayStart),
            DateOnly.FromDateTime(dayStart));

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var day = ok.Value.Should().BeOfType<PunchCardResponse>().Subject
            .Months.Single().Days.Single(d => d.Date == "2026-06-01");

        day.TotalReadings.Should().Be(1);
        day.AverageGlucose.Should().Be(100);
    }

    private void SetupPumps(params PatientDevice[] pumps)
    {
        _patientDeviceRepoMock
            .Setup(r => r.GetByDateRangeAsync(
                It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(pumps);
        SetupAidMetricsEcho();
    }

    private void SetupAidMetricsEcho() =>
        _aidMetricsServiceMock
            .Setup(s => s.Calculate(
                It.IsAny<IReadOnlyList<DeviceSegmentInput>>(),
                It.IsAny<IReadOnlyList<ApsLoopCycle>>(),
                It.IsAny<IReadOnlyList<TempBasal>>(),
                It.IsAny<int>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<double?>(),
                It.IsAny<double?>(),
                It.IsAny<double?>(),
                It.IsAny<DateTime>(),
                It.IsAny<DateTime>()))
            .Returns((
                IReadOnlyList<DeviceSegmentInput> _,
                IReadOnlyList<ApsLoopCycle> _,
                IReadOnlyList<TempBasal> _,
                int _,
                string? cgmNames,
                string? pumpNames,
                double? _,
                double? _,
                double? _,
                DateTime _,
                DateTime _) => new AidSystemMetrics
                {
                    CgmDeviceNames = cgmNames,
                    PumpDeviceNames = pumpNames,
                });

    [Fact]
    public async Task GetAidSystemMetrics_MeasuresCgmActiveTimeOnTheCanonicalReadings()
    {
        SetupAidMetricsEcho();
        var start = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var raw = new[]
        {
            new SensorGlucose { Timestamp = start, Mgdl = 100 },
            new SensorGlucose { Timestamp = start, Mgdl = 180 },
        };
        var canonical = new[] { raw[0] };

        SetupGlucose(raw);

        var canonicalGlucose = new Mock<ICanonicalGlucoseService>();
        canonicalGlucose
            .Setup(s => s.SelectAsync(
                It.IsAny<IReadOnlyList<SensorGlucose>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(canonical);

        IEnumerable<SensorGlucose>? measured = null;
        _statsServiceMock
            .Setup(s => s.CalculateCgmActivePercent(
                It.IsAny<IEnumerable<SensorGlucose>>(),
                It.IsAny<DateTime?>(),
                It.IsAny<DateTime?>(),
                It.IsAny<IReadOnlyCollection<CgmDeviceWindow>?>()))
            .Callback((
                IEnumerable<SensorGlucose> readings,
                DateTime? _,
                DateTime? _,
                IReadOnlyCollection<CgmDeviceWindow>? _) => measured = readings)
            .Returns(42.0);

        await CreateController(canonicalGlucose.Object)
            .GetAidSystemMetrics(start, start.AddDays(1));

        measured.Should().BeEquivalentTo(canonical);
    }

    [Fact]
    public async Task GetAidSystemMetrics_ReportsReadAlone_WithholdsEveryCategorisedFigure()
    {
        _aidMetricsServiceMock
            .Setup(s => s.Calculate(
                It.IsAny<IReadOnlyList<DeviceSegmentInput>>(),
                It.IsAny<IReadOnlyList<ApsLoopCycle>>(),
                It.IsAny<IReadOnlyList<TempBasal>>(),
                It.IsAny<int>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<double?>(),
                It.IsAny<double?>(),
                It.IsAny<double?>(),
                It.IsAny<DateTime>(),
                It.IsAny<DateTime>()))
            .Returns(new AidSystemMetrics
            {
                CgmDeviceNames = "Dexcom G7",
                PumpDeviceNames = "Omnipod 5",
                PumpUsePercent = 98,
                AidActivePercent = 91,
                CgmActivePercent = 95,
                TargetLow = 80,
                TargetHigh = 140,
                SiteChangeCount = 3,
                Segments = [new AidTimeSegment { Algorithm = AidAlgorithm.Trio, DurationHours = 24 }],
            });
        var start = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var controller = CreateController();
        controller.HttpContext.Items["GrantedScopes"] = new HashSet<string> { Scope.ReportsRead };

        var result = await controller.GetAidSystemMetrics(start, start.AddDays(1));

        var metrics = (AidSystemMetrics)((OkObjectResult)result.Result!).Value!;
        metrics.Should().BeEquivalentTo(
            new AidSystemMetrics(), o => o.Excluding(m => m.TimeZoneUnavailableReason));
    }

    [Fact]
    public async Task GetAidSystemMetrics_FetchesTheWholeWindowRatherThanACappedFirstPage()
    {
        SetupAidMetricsEcho();
        var start = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

        await CreateController().GetAidSystemMetrics(start, start.AddDays(90), CancellationToken.None);

        _apsSnapshotRepoMock.Verify(r => r.GetLoopCyclesAsync(
            It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Once);

        _tempBasalRepoMock.Verify(r => r.GetAsync(
            It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
            It.IsAny<string?>(), It.IsAny<string?>(),
            int.MaxValue, It.IsAny<int>(), false, It.IsAny<CancellationToken>()), Times.Once);

        _deviceEventRepoMock.Verify(r => r.GetAsync(
            It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
            It.IsAny<string?>(), It.IsAny<string?>(),
            int.MaxValue, It.IsAny<int>(), false,
            It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Once);

        _glucoseRepoMock.Verify(r => r.GetForAnalyticsAsync(
            It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
            It.IsAny<string?>(), It.IsAny<string?>(),
            int.MaxValue, It.IsAny<int>(), false,
            It.IsAny<DateTime?>(), It.IsAny<Guid?>(),
            It.IsAny<CancellationToken>(), It.IsAny<Guid?>()), Times.Once);
    }

    [Fact]
    public async Task GetAidSystemMetrics_MeasuresGlucoseThroughToTheEndOfTheWindow()
    {
        SetupAidMetricsEcho();
        // A one-minute sensor over 90 days: 129,600 readings, more than any fixed page. The stub
        // honours the limit it is given, so a capped, oldest-first fetch stops around day 35.
        var start = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var end = start.AddDays(90);
        var readings = Enumerable
            .Range(0, 129_600)
            .Select(i => new SensorGlucose { Timestamp = start.AddMinutes(i), Mgdl = 100 })
            .ToArray();

        _glucoseRepoMock
            .Setup(r => r.GetForAnalyticsAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(),
                It.IsAny<DateTime?>(), It.IsAny<Guid?>(),
                It.IsAny<CancellationToken>(), It.IsAny<Guid?>()))
            .ReturnsAsync((
                DateTime? _, DateTime? _, string? _, string? _,
                int limit, int offset, bool descending,
                DateTime? _, Guid? _, CancellationToken _, Guid? _) =>
                    (descending ? readings.OrderByDescending(r => r.Timestamp) : readings.OrderBy(r => r.Timestamp))
                        .Skip(offset)
                        .Take(limit));

        List<SensorGlucose>? measured = null;
        _statsServiceMock
            .Setup(s => s.CalculateCgmActivePercent(
                It.IsAny<IEnumerable<SensorGlucose>>(),
                It.IsAny<DateTime?>(),
                It.IsAny<DateTime?>(),
                It.IsAny<IReadOnlyCollection<CgmDeviceWindow>?>()))
            .Callback((
                IEnumerable<SensorGlucose> captured,
                DateTime? _,
                DateTime? _,
                IReadOnlyCollection<CgmDeviceWindow>? _) => measured = captured.ToList())
            .Returns(100.0);

        await CreateController().GetAidSystemMetrics(start, end, CancellationToken.None);

        measured.Should().NotBeNull();
        measured!.Count.Should().Be(readings.Length);
        measured.Max(r => r.Timestamp).Should().Be(end.AddMinutes(-1));
    }

    private void VerifyAidMetricsWindow(DateTime start, DateTime end) =>
        _aidMetricsServiceMock.Verify(s => s.Calculate(
            It.IsAny<IReadOnlyList<DeviceSegmentInput>>(),
            It.IsAny<IReadOnlyList<ApsLoopCycle>>(),
            It.IsAny<IReadOnlyList<TempBasal>>(),
            It.IsAny<int>(),
            It.IsAny<string?>(),
            It.IsAny<string?>(),
            It.IsAny<double?>(),
            It.IsAny<double?>(),
            It.IsAny<double?>(),
            start,
            end), Times.Once);

    private StatisticsController CreateClampedController(CategoryReadContext reads)
    {
        var controller = CreateController(categoryReadContext: reads);
        controller.HttpContext.RequestServices = new ServiceCollection()
            .AddSingleton<ICategoryReadContext>(reads)
            .BuildServiceProvider();
        return controller;
    }

    [Fact]
    public async Task GetAidSystemMetrics_ForAClampedMember_MeasuresTheLast24HoursWhateverWasAsked()
    {
        SetupAidMetricsEcho();
        var reads = new CategoryReadContext();
        reads.ClampMemberHistory();
        var now = DefaultNow.UtcDateTime;

        var result = await CreateClampedController(reads)
            .GetAidSystemMetrics(now.AddDays(-30), now);

        result.Result.Should().BeOfType<OkObjectResult>();
        VerifyAidMetricsWindow(now.AddHours(-HistoryClamp.WindowHours), now);
    }

    [Fact]
    public async Task GetAidSystemMetrics_ForAShareWithoutFullHistory_MeasuresTheLast24Hours()
    {
        SetupAidMetricsEcho();
        var reads = new CategoryReadContext();
        reads.MarkShare();
        var now = DefaultNow.UtcDateTime;

        await CreateClampedController(reads).GetAidSystemMetrics(now.AddDays(-30), now);

        VerifyAidMetricsWindow(now.AddHours(-HistoryClamp.WindowHours), now);
    }

    [Fact]
    public async Task GetAidSystemMetrics_ForAClampedMember_IgnoresALongSpanItCannotRead()
    {
        SetupAidMetricsEcho();
        var reads = new CategoryReadContext();
        reads.ClampMemberHistory();
        var now = DefaultNow.UtcDateTime;

        var result = await CreateClampedController(reads).GetAidSystemMetrics(now.AddDays(-400), now);

        result.Result.Should().BeOfType<OkObjectResult>();
        VerifyAidMetricsWindow(now.AddHours(-HistoryClamp.WindowHours), now);
    }

    [Fact]
    public async Task GetAidSystemMetrics_ForAnUnclampedCaller_MeasuresTheWindowAsked()
    {
        SetupAidMetricsEcho();
        var now = DefaultNow.UtcDateTime;
        var start = now.AddDays(-30);
        var end = now.AddDays(-1);

        await CreateController().GetAidSystemMetrics(start, end);

        VerifyAidMetricsWindow(start, end);
    }

    [Fact]
    public async Task GetAidSystemMetrics_StopsAtNowWhenTheWindowEndsInTheFuture()
    {
        SetupAidMetricsEcho();
        var now = DefaultNow.UtcDateTime;
        var start = now.AddDays(-2);

        await CreateController().GetAidSystemMetrics(start, now.AddDays(5));

        VerifyAidMetricsWindow(start, now);
    }

    [Fact]
    public async Task GetAidSystemMetrics_RejectsASpanOverTheReadLimit()
    {
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var result = await CreateController().GetAidSystemMetrics(
            start, start.AddDays(V4ReadLimits.MaxDateSpanDays + 1));

        result.Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(400);
        _patientDeviceRepoMock.Verify(r => r.GetByDateRangeAsync(
            It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetAidSystemMetrics_PropagatesCancellationRatherThanReturningAPartialReport()
    {
        var start = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        using var cts = new CancellationTokenSource();

        _activeProfileResolverMock
            .Setup(r => r.GetActiveProfileNameAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException(cts.Token));

        var act = async () => await CreateController()
            .GetAidSystemMetrics(start, start.AddDays(1), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task GetAidSystemMetrics_FallsBackToTheModelForAnUncataloguedPump()
    {
        var start = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

        _patientDeviceRepoMock
            .Setup(r => r.GetByDateRangeAsync(
                It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new PatientDevice
                {
                    Id = Guid.NewGuid(),
                    DeviceCategory = DeviceCategory.InsulinPump,
                    CatalogId = null,
                    Manufacturer = "Roche",
                    Model = "Accu-Chek Combo",
                    AidAlgorithm = null,
                },
            });
        SetupAidMetricsEcho();

        var result = await CreateController()
            .GetAidSystemMetrics(start, start.AddDays(1), CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var payload = ok.Value.Should().BeOfType<AidSystemMetrics>().Subject;
        payload.PumpDeviceNames.Should().Be("Accu-Chek Combo");
    }

    [Fact]
    public async Task GetAidSystemMetrics_FallsBackToTheManufacturerWhenThePumpHasNoModel()
    {
        var start = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

        SetupPumps(new PatientDevice
        {
            Id = Guid.NewGuid(),
            DeviceCategory = DeviceCategory.InsulinPump,
            CatalogId = null,
            Manufacturer = "Roche",
            Model = "",
            AidAlgorithm = null,
        });

        var result = await CreateController()
            .GetAidSystemMetrics(start, start.AddDays(1), CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var payload = ok.Value.Should().BeOfType<AidSystemMetrics>().Subject;
        payload.PumpDeviceNames.Should().Be("Roche");
    }

    [Fact]
    public async Task GetAidSystemMetrics_ReportsAnUnnameablePumpAsUnknownRatherThanBlank()
    {
        // IAidMetricsService documents null for "unknown"; the report renders null as "--"
        // and an empty string as an empty box.
        var start = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

        SetupPumps(new PatientDevice
        {
            Id = Guid.NewGuid(),
            DeviceCategory = DeviceCategory.InsulinPump,
            CatalogId = null,
            Manufacturer = "",
            Model = "",
            AidAlgorithm = null,
        });

        var result = await CreateController()
            .GetAidSystemMetrics(start, start.AddDays(1), CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var payload = ok.Value.Should().BeOfType<AidSystemMetrics>().Subject;
        payload.PumpDeviceNames.Should().BeNull();
    }

    [Fact]
    public async Task GetAidSystemMetrics_StillReturnsTheReportWhenTheTargetRangeFetchFails()
    {
        var start = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

        _activeProfileResolverMock
            .Setup(r => r.GetActiveProfileNameAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("no active profile"));
        SetupAidMetricsEcho();

        var result = await CreateController()
            .GetAidSystemMetrics(start, start.AddDays(1), CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeOfType<AidSystemMetrics>();
    }

    [Fact]
    public async Task GetAidSystemMetrics_PassesTheCallersCancellationTokenToEveryFetch()
    {
        SetupAidMetricsEcho();
        var start = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        using var cts = new CancellationTokenSource();
        var token = cts.Token;

        await CreateController().GetAidSystemMetrics(start, start.AddDays(1), token);

        _patientDeviceRepoMock.Verify(r => r.GetByDateRangeAsync(
            It.IsAny<DateTime>(), It.IsAny<DateTime>(), token), Times.Once);

        _apsSnapshotRepoMock.Verify(r => r.GetLoopCyclesAsync(
            It.IsAny<DateTime>(), It.IsAny<DateTime>(), token), Times.Once);

        _tempBasalRepoMock.Verify(r => r.GetAsync(
            It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
            It.IsAny<string?>(), It.IsAny<string?>(),
            It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), token), Times.Once);

        _deviceEventRepoMock.Verify(r => r.GetAsync(
            It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
            It.IsAny<string?>(), It.IsAny<string?>(),
            It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(),
            It.IsAny<Guid?>(), token), Times.Once);

        _glucoseRepoMock.Verify(r => r.GetForAnalyticsAsync(
            It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
            It.IsAny<string?>(), It.IsAny<string?>(),
            It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(),
            It.IsAny<DateTime?>(), It.IsAny<Guid?>(),
            token, It.IsAny<Guid?>()), Times.Once);
    }

    [Fact]
    public async Task GetAidSystemMetrics_NamesARegisteredPumpThatRunsNoAidAlgorithm()
    {
        var start = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

        _patientDeviceRepoMock
            .Setup(r => r.GetByDateRangeAsync(
                It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new PatientDevice
                {
                    Id = Guid.NewGuid(),
                    DeviceCategory = DeviceCategory.InsulinPump,
                    CatalogId = "ypsopump",
                    Manufacturer = "Ypsomed",
                    Model = "YpsoPump",
                    AidAlgorithm = null,
                },
            });

        SetupAidMetricsEcho();

        var result = await CreateController()
            .GetAidSystemMetrics(start, start.AddDays(1), CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var payload = ok.Value.Should().BeOfType<AidSystemMetrics>().Subject;
        payload.PumpDeviceNames.Should().Be("YpsoPump");
    }

    private async Task<(AidSystemMetrics Report, DeviceSegmentInput Segment, CgmDeviceWindow CgmWindow)> ReadDeviceWindows(
        string? zoneId, DateOnly first, DateOnly last)
    {
        var start = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
        _therapySettingsResolverMock
            .Setup(r => r.GetTimezoneAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(zoneId);
        _patientDeviceRepoMock
            .Setup(r => r.GetByDateRangeAsync(
                It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new PatientDevice
                {
                    Id = Guid.NewGuid(),
                    DeviceCategory = DeviceCategory.InsulinPump,
                    AidAlgorithm = AidAlgorithm.Loop,
                    StartDate = first,
                    EndDate = last,
                },
                new PatientDevice
                {
                    Id = Guid.NewGuid(),
                    DeviceCategory = DeviceCategory.CGM,
                    StartDate = first,
                    EndDate = last,
                },
            });

        IReadOnlyList<DeviceSegmentInput>? segments = null;
        _aidMetricsServiceMock
            .Setup(s => s.Calculate(
                It.IsAny<IReadOnlyList<DeviceSegmentInput>>(),
                It.IsAny<IReadOnlyList<ApsLoopCycle>>(),
                It.IsAny<IReadOnlyList<TempBasal>>(),
                It.IsAny<int>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<double?>(),
                It.IsAny<double?>(),
                It.IsAny<double?>(),
                It.IsAny<DateTime>(),
                It.IsAny<DateTime>()))
            .Callback((IReadOnlyList<DeviceSegmentInput> given, IReadOnlyList<ApsLoopCycle> _, IReadOnlyList<TempBasal> _,
                int _, string? _, string? _, double? _, double? _, double? _, DateTime _, DateTime _) => segments = given)
            .Returns(new AidSystemMetrics());

        IReadOnlyCollection<CgmDeviceWindow>? cgmWindows = null;
        _statsServiceMock
            .Setup(s => s.CalculateCgmActivePercent(
                It.IsAny<IEnumerable<SensorGlucose>>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<IReadOnlyCollection<CgmDeviceWindow>?>()))
            .Callback((IEnumerable<SensorGlucose> _, DateTime? _, DateTime? _, IReadOnlyCollection<CgmDeviceWindow>? given) => cgmWindows = given);

        var result = await CreateController().GetAidSystemMetrics(start, start.AddDays(60), CancellationToken.None);

        var report = result.Result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeOfType<AidSystemMetrics>().Subject;
        return (report, segments!.Single(), cgmWindows!.Single());
    }

    [Fact]
    public async Task GetAidSystemMetrics_BoundsADeviceAtLocalMidnightInAHalfHourZone()
    {
        var (report, segment, cgm) = await ReadDeviceWindows(
            "Australia/Adelaide", new DateOnly(2026, 2, 3), new DateOnly(2026, 2, 5));

        var from = new DateTime(2026, 2, 2, 13, 30, 0, DateTimeKind.Utc);
        var to = new DateTime(2026, 2, 5, 13, 30, 0, DateTimeKind.Utc);
        segment.StartDate.Should().Be(from);
        segment.EndDate.Should().Be(to);
        cgm.Start.Should().Be(from);
        cgm.End.Should().Be(to);
        report.TimeZoneUnavailableReason.Should().BeNull();
    }

    [Fact]
    public async Task GetAidSystemMetrics_UsesEachEndOfADeviceWindowsOwnOffsetAcrossADstTransition()
    {
        var (_, segment, cgm) = await ReadDeviceWindows(
            "Australia/Adelaide", new DateOnly(2026, 3, 30), new DateOnly(2026, 4, 5));

        var from = new DateTime(2026, 3, 29, 13, 30, 0, DateTimeKind.Utc);
        var to = new DateTime(2026, 4, 5, 14, 30, 0, DateTimeKind.Utc);
        segment.StartDate.Should().Be(from);
        segment.EndDate.Should().Be(to);
        cgm.Start.Should().Be(from);
        cgm.End.Should().Be(to);
    }

    [Fact]
    public async Task GetAidSystemMetrics_ReadsDeviceDatesAsUtcDaysAndSaysSoWhenNoZoneIsConfigured()
    {
        var (report, segment, cgm) = await ReadDeviceWindows(
            null, new DateOnly(2026, 2, 3), new DateOnly(2026, 2, 5));

        var from = new DateTime(2026, 2, 3, 0, 0, 0, DateTimeKind.Utc);
        var to = new DateTime(2026, 2, 6, 0, 0, 0, DateTimeKind.Utc);
        segment.StartDate.Should().Be(from);
        segment.EndDate.Should().Be(to);
        cgm.Start.Should().Be(from);
        cgm.End.Should().Be(to);
        report.TimeZoneUnavailableReason.Should().Be(TimeZoneUnavailableReason.NotConfigured);
    }

    [Fact]
    public async Task GetMultiPeriodStatistics_ReadsTheWidestWindowOnce_AndSlicesEachPeriodFromIt()
    {
        var now = DefaultNow.UtcDateTime;
        SetupGlucose(new[] { TimeSpan.FromHours(1), TimeSpan.FromHours(2), TimeSpan.FromDays(2),
                TimeSpan.FromDays(5), TimeSpan.FromDays(20), TimeSpan.FromDays(60) }
            .Concat(Enumerable.Range(1, 10).Select(i => TimeSpan.FromMinutes(5 * i)))
            .Select(age => new SensorGlucose { Timestamp = now - age, Mgdl = 120 }).ToList());
        SetupEmptyTreatments();
        var ages = new[] { TimeSpan.FromHours(1), TimeSpan.FromDays(2), TimeSpan.FromDays(60) };
        SetupBoluses(BolusKind.Manual, ages.Select(a => new Bolus { Timestamp = now - a, Insulin = 1 }));
        SetupBoluses(BolusKind.Algorithm, ages.Select(a => new Bolus { Timestamp = now - a, Insulin = 1, Kind = BolusKind.Algorithm }));
        _carbIntakeRepoMock
            .Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(),
                It.IsAny<DateTime?>(), It.IsAny<Guid?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(ages.Select(a => new CarbIntake { Timestamp = now - a, Carbs = 10 }).ToList());
        _tempBasalRepoMock
            .Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(ages.Select(a => new TempBasal
            {
                StartTimestamp = now - a,
                EndTimestamp = now - a + TimeSpan.FromHours(1),
                Rate = 1,
                Origin = TempBasalOrigin.Manual,
            }).ToList());
        _basalInjectionRepoMock
            .Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(ages.Select(a => new BasalInjection { Timestamp = now - a, Units = 10 }).ToList());

        var result = await CreateController(statisticsService: new StatisticsService(TimeProvider.System)).GetMultiPeriodStatistics();

        var payload = result.Result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<MultiPeriodStatistics>().Subject;
        var periods = new[] { payload.LastDay, payload.Last3Days, payload.LastWeek, payload.LastMonth, payload.Last90Days };
        periods.Select(p => p!.EntryCount).Should().Equal(12, 13, 14, 15, 16);
        periods.Select(p => p!.TreatmentCount).Should().Equal(2, 4, 4, 4, 6);
        periods.Select(p => p!.InsulinDelivery!.TotalCarbs).Should().Equal(10, 20, 20, 20, 30);
        periods.Select(p => p!.InsulinDelivery!.BasalCount).Should().Equal(2, 4, 4, 4, 6);
        periods.Select(p => p!.InsulinDelivery!.ManualBolusCount).Should().Equal(1, 2, 2, 2, 3);
        periods.Select(p => p!.InsulinDelivery!.AutomaticBolusCount).Should().Equal(1, 2, 2, 2, 3);
        periods.Select(p => p!.InsulinDelivery!.TotalBasal).Should().Equal(11, 22, 22, 22, 33);
        periods.Select(p => p!.InsulinDelivery!.TotalBolus).Should().Equal(2, 4, 4, 4, 6);

        _glucoseRepoMock.Verify(r => r.GetForAnalyticsAsync(
            It.Is<DateTime?>(d => d < now.AddDays(-89)), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(),
            It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(),
            It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>(), It.IsAny<Guid?>()),
            Times.Once);
        _glucoseRepoMock.Verify(r => r.GetForAnalyticsAsync(
            It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(),
            It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(),
            It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>(), It.IsAny<Guid?>()),
            Times.Once);
        _tempBasalRepoMock.Verify(r => r.GetAsync(
            It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(),
            It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task GetMultiPeriodStatistics_ServesTheTenantCache()
    {
        var cached = new MultiPeriodStatistics();
        var cache = new Mock<ITenantCache>();
        cache.Setup(c => c.GetOrSetAsync(
                It.IsAny<string>(), It.IsAny<Func<Task<MultiPeriodStatistics>>>(),
                It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(cached);

        var result = await CreateController(cache: cache.Object).GetMultiPeriodStatistics();

        result.Result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeSameAs(cached);
    }

    [Theory]
    [InlineData("Australia/Sydney", "2026-09-01", "2026-11-30")]
    [InlineData("America/New_York", "2026-10-01", "2026-12-15")]
    [InlineData("Europe/Stockholm", "2026-03-01", "2026-04-30")]
    [InlineData("UTC", "2026-01-01", "2026-02-28")]
    [InlineData("America/Asuncion", "2023-09-15", "2023-10-15")]
    [InlineData("America/Asuncion", "2024-03-10", "2024-03-31")]
    [InlineData("Asia/Amman", "2022-02-10", "2022-03-10")]
    public async Task GetPunchCardData_BucketsEveryDayAsThePerDayScanDid(string zone, string from, string to)
    {
        var tz = TimeZoneInfo.FindSystemTimeZoneById(zone);
        var start = DateOnly.Parse(from);
        var end = DateOnly.Parse(to);
        try
        {
            for (var date = start; date <= end; date = date.AddDays(1))
            {
                PunchCardDayWindow.For(date.ToDateTime(TimeOnly.MinValue), tz);
            }
        }
        catch (ArgumentException) when (OperatingSystem.IsWindows())
        {
            // Windows time zone data skips local midnight for these zones, which the endpoint rejects; the overlap only exists in IANA data.
            return;
        }

        var (glucose, boluses, carbs, injections) = PunchCardFixture(tz, start, end);

        SetupEmptyTreatments();
        SetupPunchCardGlucose(glucose);
        _therapySettingsResolverMock
            .Setup(r => r.GetTimezoneAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(zone);
        _bolusRepoMock
            .Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(),
                BolusKind.Manual,
                It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(boluses);
        _carbIntakeRepoMock
            .Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(),
                It.IsAny<DateTime?>(), It.IsAny<Guid?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(carbs);
        _basalInjectionRepoMock
            .Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(injections);

        var stats = new StatisticsService(TimeProvider.System);
        var result = await CreateController(statisticsService: stats).GetPunchCardData(start, end);

        var days = result.Result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<PunchCardResponse>().Subject
            .Months.SelectMany(m => m.Days).OrderBy(d => d.Date, StringComparer.Ordinal).ToList();
        var expected = PerDayScanDays(glucose, boluses, carbs, injections, start, end, tz, stats);

        days.Should().HaveCount(end.DayNumber - start.DayNumber + 1);
        days.Should().Contain(d => d.TotalReadings == 0, "the fixture leaves days empty");
        days.Should().Contain(d => d.TotalReadings > 0);
        days.Should().BeEquivalentTo(expected, options => options.WithStrictOrdering());
    }

    [Fact]
    public async Task GetPunchCardData_PutsARecordAtLocalMidnightOnTheDayItOpens()
    {
        var tz = TimeZoneInfo.FindSystemTimeZoneById("Australia/Sydney");
        var midnightUtc = TimeZoneInfo.ConvertTimeToUtc(new DateTime(2026, 10, 5, 0, 0, 0, DateTimeKind.Unspecified), tz);

        SetupEmptyTreatments();
        SetupPunchCardGlucose(
        [
            new SensorGlucose { Timestamp = midnightUtc.AddMilliseconds(-1), Mgdl = 100 },
            new SensorGlucose { Timestamp = midnightUtc, Mgdl = 110 },
        ]);
        _therapySettingsResolverMock
            .Setup(r => r.GetTimezoneAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync("Australia/Sydney");

        var result = await CreateController(statisticsService: new StatisticsService(TimeProvider.System))
            .GetPunchCardData(new DateOnly(2026, 10, 4), new DateOnly(2026, 10, 5));

        var days = result.Result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<PunchCardResponse>().Subject
            .Months.Single().Days;
        days.Single(d => d.Date == "2026-10-04").Entries.Select(e => e.Mgdl).Should().Equal(100);
        days.Single(d => d.Date == "2026-10-05").Entries.Select(e => e.Mgdl).Should().Equal(110);
    }

    private static (List<SensorGlucose> Glucose, List<Bolus> Boluses, List<CarbIntake> Carbs, List<BasalInjection> Injections)
        PunchCardFixture(TimeZoneInfo tz, DateOnly start, DateOnly end)
    {
        var glucose = new List<SensorGlucose>();
        var boluses = new List<Bolus>();
        var carbs = new List<CarbIntake>();
        var injections = new List<BasalInjection>();

        for (var date = start; date <= end; date = date.AddDays(1))
        {
            var n = date.DayNumber - start.DayNumber;
            var localMidnight = new DateTime(date.Year, date.Month, date.Day, 0, 0, 0, DateTimeKind.Unspecified);
            var midnightUtc = TimeZoneInfo.ConvertTimeToUtc(localMidnight, tz);

            if (n % 7 != 3 && n % 7 != 4)
            {
                for (var minute = -150; minute <= 150; minute += 15)
                {
                    var at = midnightUtc.AddMinutes(minute);
                    glucose.Add(new SensorGlucose { Timestamp = at, Mgdl = 70 + (n * 7 + minute / 15 + 20) % 200 });
                    boluses.Add(new Bolus { Timestamp = at, Insulin = 0.5 });
                    carbs.Add(new CarbIntake { Timestamp = at, Carbs = 5 });
                }
            }

            if (n % 7 != 3)
            {
                glucose.Add(new SensorGlucose { Timestamp = midnightUtc, Mgdl = 60 + n % 150 });
                if ((n - 1) % 7 != 3)
                {
                    glucose.Add(new SensorGlucose { Timestamp = midnightUtc.AddMilliseconds(-1), Mgdl = 90 + n % 90 });
                }
                for (var minute = 17; minute < 24 * 60; minute += 83)
                {
                    glucose.Add(new SensorGlucose
                    {
                        Timestamp = midnightUtc.AddMinutes(minute),
                        Mgdl = 40 + (n * 31 + minute) % 300,
                    });
                }
            }

            if (n % 5 != 1)
            {
                boluses.Add(new Bolus { Timestamp = midnightUtc, Insulin = 1 + n % 4 });
                boluses.Add(new Bolus { Timestamp = midnightUtc.AddHours(13), Insulin = 2.5 });
            }

            if (n % 4 != 2)
            {
                if ((n - 1) % 4 != 2)
                {
                    carbs.Add(new CarbIntake { Timestamp = midnightUtc.AddMilliseconds(-1), Carbs = 10 + n % 20 });
                }
                carbs.Add(new CarbIntake { Timestamp = midnightUtc.AddHours(7), Carbs = 35 });
            }

            if (n % 6 == 0)
            {
                injections.Add(new BasalInjection { Timestamp = midnightUtc.AddHours(9), Units = 18 });
            }
        }

        glucose.Sort((a, b) => a.Mills.CompareTo(b.Mills));
        boluses.Sort((a, b) => a.Mills.CompareTo(b.Mills));
        carbs.Sort((a, b) => a.Mills.CompareTo(b.Mills));
        return (glucose, boluses, carbs, injections);
    }

    /// <summary>
    /// The punch-card day loop as it was when each day filtered the whole of each list, kept so the
    /// bucketed endpoint is held to the figures that loop produced.
    /// </summary>
    private static List<PunchCardDay> PerDayScanDays(
        List<SensorGlucose> glucoseData,
        List<Bolus> manualBoluses,
        List<CarbIntake> carbs,
        List<BasalInjection> injections,
        DateOnly start,
        DateOnly end,
        TimeZoneInfo tz,
        StatisticsService stats)
    {
        var dailyBasalMap = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var d in stats.CalculateDailyBasalBolusRatios(manualBoluses, [], [], start.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc), Mills(end.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)), tz, injections).DailyData)
        {
            if (!string.IsNullOrEmpty(d.Date)) dailyBasalMap[d.Date] = d.Basal;
        }

        var days = new List<PunchCardDay>();
        for (var day = start.ToDateTime(TimeOnly.MinValue); day <= end.ToDateTime(TimeOnly.MinValue); day = day.AddDays(1))
        {
            var dayStartLocal = DateTime.SpecifyKind(day, DateTimeKind.Unspecified);
            var dayEndLocal = dayStartLocal.AddDays(1).AddTicks(-1);
            var dayStartMs = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(dayStartLocal, tz), TimeSpan.Zero).ToUnixTimeMilliseconds();
            var dayEndMs = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(dayEndLocal, tz), TimeSpan.Zero).ToUnixTimeMilliseconds();

            var dayEntries = glucoseData.Where(e => e.Mills >= dayStartMs && e.Mills <= dayEndMs).ToList();
            var dayBoluses = manualBoluses.Where(b => b.Mills >= dayStartMs && b.Mills <= dayEndMs).ToList();
            var dayCarbs = carbs.Where(c => c.Mills >= dayStartMs && c.Mills <= dayEndMs).ToList();

            var tir = dayEntries.Count > 0 ? stats.CalculateTimeInRange(dayEntries) : null;
            var treatment = dayBoluses.Count > 0 || dayCarbs.Count > 0
                ? stats.CalculateTreatmentSummary(dayBoluses, dayCarbs)
                : null;

            var pct = tir?.Percentages;
            var inRangePct = pct?.Target ?? 0;
            var veryLowPct = pct?.VeryLow ?? 0;
            var lowPct = pct?.Low ?? 0;
            var highPct = pct?.High ?? 0;
            var veryHighPct = pct?.VeryHigh ?? 0;

            var dateStr = day.ToString("yyyy-MM-dd");
            var totals = treatment?.Totals;
            var totalBolus = totals?.Insulin?.Bolus ?? 0;
            var totalBasal = dailyBasalMap.GetValueOrDefault(dateStr, 0.0);

            var entries = dayEntries
                .Where(e => GlucoseStatistics.IsPlausibleReading(e.Mgdl))
                .OrderBy(e => e.Mills)
                .Select(e => new PunchCardEntry { Mills = e.Mills, Mgdl = e.Mgdl })
                .ToList();
            var totalReadings = entries.Count;

            days.Add(new PunchCardDay
            {
                Date = dateStr,
                Timestamp = dayStartMs,
                EndTimestamp = dayEndMs + 1,
                TotalReadings = totalReadings,
                InRangeCount = (int)Math.Round(inRangePct / 100.0 * totalReadings),
                VeryLowCount = (int)Math.Round(veryLowPct / 100.0 * totalReadings),
                LowCount = (int)Math.Round(lowPct / 100.0 * totalReadings),
                HighCount = (int)Math.Round(highPct / 100.0 * totalReadings),
                VeryHighCount = (int)Math.Round(veryHighPct / 100.0 * totalReadings),
                InRangePercent = inRangePct,
                VeryLowPercent = veryLowPct,
                LowPercent = lowPct,
                HighPercent = highPct,
                VeryHighPercent = veryHighPct,
                AverageGlucose = totalReadings > 0 ? entries.Average(e => e.Mgdl) : 0,
                TotalCarbs = totals?.Food?.Carbs ?? 0,
                TotalInsulin = totalBolus + totalBasal,
                TotalBolus = totalBolus,
                TotalBasal = totalBasal,
                CarbToInsulinRatio = treatment?.CarbToInsulinRatio,
                Entries = entries,
            });
        }

        return days;
    }
}
