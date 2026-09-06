using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Nocturne.API.Controllers.V4.Analytics;
using Nocturne.Core.Contracts.Analytics;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Profiles;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Basal;
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
    private readonly Mock<IBasalInjectionRepository> _basalInjectionRepoMock = new();
    private readonly Mock<IActiveProfileResolver> _activeProfileResolverMock = new();
    private readonly Mock<IBasalSegmentService> _basalSegmentsMock = new();

    private StatisticsController CreateController(ICanonicalGlucoseService? canonicalGlucose = null)
    {
        var controller = new StatisticsController(
            _statsServiceMock.Object,
            Mock.Of<ICacheService>(),
            Mock.Of<IProfileProjectionService>(),
            Mock.Of<IBasalRateResolver>(),
            _basalSegmentsMock.Object,
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
            _basalInjectionRepoMock.Object,
            _activeProfileResolverMock.Object,
            canonicalGlucose ?? TestDoubles.CanonicalGlucosePassThrough.Create());

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
                It.IsAny<TimeZoneInfo?>(),
                It.IsAny<IEnumerable<BasalInjection>?>()))
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
        ok.Value.Should().BeSameAs(slots);
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
    public async Task GetWeekdayAverages_WithoutATherapyTimezone_BucketsOnUtc()
    {
        SetupGlucose(Array.Empty<SensorGlucose>());
        TimeZoneInfo? usedTz = null;
        _statsServiceMock
            .Setup(s => s.CalculateWeekdayAverages(It.IsAny<IEnumerable<SensorGlucose>>(), It.IsAny<TimeZoneInfo>()))
            .Callback<IEnumerable<SensorGlucose>, TimeZoneInfo>((_, tz) => usedTz = tz)
            .Returns(new List<WeekdayGlucoseSlot>());

        await CreateController().GetWeekdayAverages(
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 1, 8, 0, 0, 0, DateTimeKind.Utc));

        usedTz.Should().Be(TimeZoneInfo.Utc);
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
                It.IsAny<IEnumerable<TempBasal>>(), It.IsAny<IEnumerable<Bolus>>(),
                It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<TimeZoneInfo?>()))
            .Callback<IEnumerable<TempBasal>, IEnumerable<Bolus>, DateTime, DateTime, TimeZoneInfo?>(
                (tempBasals, _, _, _, _) => synthesized = tempBasals.ToList())
            .Returns(new BasalAnalysisResponse());

        await CreateController().GetBasalAnalysis(start, start.AddDays(1));

        synthesized.Should().NotBeNull();
        synthesized!.Should().OnlyContain(t => t.Origin == TempBasalOrigin.Scheduled);
        synthesized.Select(t => (t.StartTimestamp, t.EndTimestamp, t.Rate)).Should().Equal(
            segments.Select(s => (
                DateTimeOffset.FromUnixTimeMilliseconds(s.StartMills).UtcDateTime,
                (DateTime?)DateTimeOffset.FromUnixTimeMilliseconds(s.EndMills).UtcDateTime,
                s.UnitsPerHour)));
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
                It.IsAny<IEnumerable<Bolus>>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(),
                It.IsAny<TimeZoneInfo?>(), It.IsAny<IEnumerable<BasalInjection>?>()))
            .Callback<IEnumerable<TempBasal>, IEnumerable<Bolus>, IEnumerable<Bolus>, DateTime, DateTime, TimeZoneInfo?, IEnumerable<BasalInjection>?>(
                (tempBasals, _, _, _, _, _, _) => passed = tempBasals.ToList())
            .Returns(new HourlyInsulinDeliveryResponse());

        await CreateController().GetHourlyInsulinDelivery(start, start.AddDays(1));

        passed.Should().BeEmpty(
            "MDI injections are already the day's basal, so a profile baseline on top would double-count it");
        _basalSegmentsMock.Verify(
            s => s.GetSegmentsAsync(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CancellationToken>()),
            Times.Never);
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
                It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<IEnumerable<BasalInjection>?>()))
            .Callback<IEnumerable<Bolus>, IEnumerable<Bolus>, IEnumerable<TempBasal>, IEnumerable<CarbIntake>, DateTime, DateTime, IEnumerable<BasalInjection>?>(
                (m, a, _, _, _, _, _) => { passedManual = m.ToList(); passedAlgorithm = a.ToList(); })
            .Returns(new InsulinDeliveryStatistics());

        await CreateController().GetInsulinDeliveryStatistics(start, start.AddDays(1));

        passedManual.Should().BeEquivalentTo(manual);
        passedAlgorithm.Should().BeEquivalentTo(algorithm);

        VerifyBolusLimit(BolusKind.Manual, 10000);
        VerifyBolusLimit(BolusKind.Algorithm, 10000);
    }

    [Fact]
    public async Task GetHourlyInsulinDelivery_FetchesUncapped()
    {
        var start = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc);
        SetupEmptyTreatments();
        _statsServiceMock
            .Setup(s => s.CalculateHourlyInsulinDelivery(
                It.IsAny<IEnumerable<TempBasal>>(), It.IsAny<IEnumerable<Bolus>>(),
                It.IsAny<IEnumerable<Bolus>>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(),
                It.IsAny<TimeZoneInfo?>(), It.IsAny<IEnumerable<BasalInjection>?>()))
            .Returns(new HourlyInsulinDeliveryResponse());

        await CreateController().GetHourlyInsulinDelivery(start, start.AddDays(90));

        VerifyBolusLimit(BolusKind.Manual, int.MaxValue);
        VerifyBolusLimit(BolusKind.Algorithm, int.MaxValue);
    }

    private void SetupBoluses(BolusKind kind, IEnumerable<Bolus> boluses) =>
        _bolusRepoMock
            .Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(),
                It.IsAny<bool>(), kind,
                It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(boluses);

    private void VerifyBolusLimit(BolusKind kind, int limit) =>
        _bolusRepoMock.Verify(r => r.GetAsync(
            It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
            It.IsAny<string?>(), It.IsAny<string?>(),
            limit, It.IsAny<int>(), It.IsAny<bool>(),
            It.IsAny<bool>(), kind,
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
