using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Nocturne.API.Configuration;
using Nocturne.API.Controllers.V1;
using Nocturne.API.Controllers.V4.Analytics;
using Nocturne.API.Services.Alerts;
using Nocturne.API.Services.Analytics;
using Nocturne.API.Services.Auth;
using Nocturne.API.Services.BolusWizard;
using Nocturne.API.Services.Identity;
using Nocturne.API.Services.ChartData;
using Nocturne.API.Services.ChartData.Stages;
using Nocturne.API.Services.Devices;
using Nocturne.API.Services.Glucose;
using Nocturne.API.Services.Profiles;
using Nocturne.API.Tests.Services.Profiles.Resolvers;
using Nocturne.API.Services.Treatments;
using Nocturne.API.Tests.Infrastructure;
using Nocturne.Core.Clinical.Treatments;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Analytics;
using Nocturne.Core.Contracts;
using Nocturne.Core.Contracts.Devices;
using Nocturne.Core.Contracts.DeviceStatuses;
using Nocturne.Core.Contracts.Legacy;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Entries;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Notifications;
using Nocturne.Core.Contracts.Profiles;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Contracts.Sleep;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Queries;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Abstractions;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Tests.Shared.Mocks;
using Xunit;

namespace Nocturne.API.Tests.Services.Analytics;

/// <summary>
/// Every reader of IOB reports the one total, bolus plus net basal, for the same treatments on the
/// local path: the chart, alert enrichment, the widget, <c>/properties</c>, v1 <c>/iob</c> and
/// Retrospective.
/// </summary>
/// <remarks>
/// A 0.5 U bolus 30 minutes before the reading and a suspend over the same 30 minutes, on a 1 U/h
/// schedule and the DIA 3 h curve, where a dose m minutes old keeps 1 - 0.001852 (m/5)(m/5 + 1) of
/// itself. The bolus keeps 0.5 * 0.922216 = 0.461 U. The suspend withheld six 1/12 U chunks, 5 to 30
/// minutes old: (0.996296 + 0.988888 + 0.977776 + 0.96296 + 0.94444 + 0.922216) / 12 = 0.483 U. The
/// total is 0.461 - 0.483 = -0.022 U, below the bolus IOB alone. The widget and <c>/properties</c> read
/// at the wall clock; the widget reports two decimals, -0.02, and <c>/properties</c> three, as
/// Nightscout does. Every other reader is asked for a fixed instant on the five-minute grid, which
/// the Retrospective timeline samples.
/// </remarks>
[Trait("Category", "Unit")]
public class TotalIobAgreementTests
{
    private const long Minute = 60 * 1000;
    private const double Total = -0.022;
    private const double BolusPart = 0.461;
    private const double BasalPart = -0.483;

    private static readonly DateTimeOffset Reading = new(2024, 1, 15, 10, 0, 0, TimeSpan.Zero);
    private static readonly long ReadingMills = Reading.ToUnixTimeMilliseconds();

    private readonly IobCalculator _calculator;
    private readonly Mock<ICobCalculator> _cob = new();
    private readonly Mock<ITherapyTimelineResolver> _timelines = new();
    private readonly Mock<ICarbIntakeRepository> _carbs = new();
    private readonly List<ApsSnapshot> _apsRows = [];
    private readonly List<PumpSnapshot> _pumpRows = [];
    private readonly Mock<IApsSnapshotRepository> _aps = new();
    private readonly Mock<IPumpSnapshotRepository> _pump = new();

    public TotalIobAgreementTests()
    {
        var therapy = new Mock<ITherapySettingsResolver>();
        therapy.Setup(t => t.GetDIAAsync(It.IsAny<long>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(3.0);
        var sensitivity = new Mock<ISensitivityResolver>();
        sensitivity.Setup(s => s.GetSensitivityAsync(It.IsAny<long>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(95.0);
        _aps.Setup(r => r.GetAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DateTime? from, DateTime? to, string? _, string? _, int limit, int _, bool _, CancellationToken _) =>
                (IEnumerable<ApsSnapshot>)_apsRows.Where(s => s.Timestamp >= from && s.Timestamp <= to)
                    .OrderByDescending(s => s.Timestamp).Take(limit).ToList());
        _aps.Setup(r => r.GetIobCobPointsAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DateTime from, DateTime to, CancellationToken _) =>
                (IReadOnlyList<ApsIobCobPoint>)ApsPoints(from, to));
        _pump.Setup(r => r.GetAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DateTime? from, DateTime? to, string? _, string? _, int limit, int _, bool _, CancellationToken _) =>
                (IEnumerable<PumpSnapshot>)_pumpRows.Where(s => s.Timestamp >= from && s.Timestamp <= to)
                    .OrderByDescending(s => s.Timestamp).Take(limit).ToList());
        _pump.Setup(r => r.GetIobPointsAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DateTime from, DateTime to, CancellationToken _) =>
                (IReadOnlyList<PumpIobPoint>)PumpPoints(from, to));
        _calculator = new IobCalculator(
            therapy.Object, sensitivity.Object, MockBasalSegments.Flat(1.0).Object, _aps.Object, _pump.Object, MockStateSpans.Empty().Object);

        _cob
            .Setup(c => c.CalculateTotalAsync(
                It.IsAny<List<CarbIntake>>(), It.IsAny<List<Bolus>?>(), It.IsAny<List<TempBasal>?>(),
                It.IsAny<long>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CobResult());
        _carbs
            .Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(),
                It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var snapshot = new TherapySnapshot(
            3.0, 30.0, null, null, 0,
            [new ScheduleEntry { TimeAsSeconds = 0, Value = 95.0 }], null,
            [new ScheduleEntry { TimeAsSeconds = 0, Value = 1.0 }]);
        _timelines
            .Setup(r => r.BuildAsync(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((long from, long to, string? _, CancellationToken _) =>
                new TherapyTimeline([new TherapySegment(from, to, snapshot)]));
    }

    private List<ApsIobCobPoint> ApsPoints(DateTime from, DateTime to) =>
        [.. _apsRows.Where(s => s.Timestamp >= from && s.Timestamp <= to).OrderBy(s => s.Timestamp)
            .Select(s => new ApsIobCobPoint(s.Timestamp, s.Iob, s.Cob, null))];

    private List<PumpIobPoint> PumpPoints(DateTime from, DateTime to) =>
        [.. _pumpRows.Where(s => s.Timestamp >= from && s.Timestamp <= to).OrderBy(s => s.Timestamp)
            .Select(s => new PumpIobPoint(s.Timestamp, s.Iob, s.BolusIob))];

    private static Bolus BolusBefore(long reading) => new()
    {
        Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(reading - 30 * Minute).UtcDateTime,
        Insulin = 0.5,
    };

    private static TempBasal SuspendBefore(long reading) => new()
    {
        StartTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(reading - 30 * Minute).UtcDateTime,
        EndTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(reading).UtcDateTime,
        Rate = 0,
        Origin = TempBasalOrigin.Suspended,
    };

    private bool _noTreatments;

    private IIobCobReader Reader(long at) =>
        new IobCobReader(_calculator, _cob.Object, Boluses(at), TempBasals(at), _carbs.Object, TestTenantCache.Create(), NullLogger<IobCobReader>.Instance);

    private IBolusRepository Boluses(long reading)
    {
        var boluses = new Mock<IBolusRepository>();
        boluses
            .Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(),
                It.IsAny<BolusKind?>(), It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(_noTreatments ? [] : [BolusBefore(reading)]);
        return boluses.Object;
    }

    private ITempBasalRepository TempBasals(long reading)
    {
        var temps = new Mock<ITempBasalRepository>();
        temps
            .Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(_noTreatments ? [] : [SuspendBefore(reading)]);
        return temps.Object;
    }

    [Fact]
    public async Task EveryReader_ReportsTheSameTotal_BelowTheBolusIob()
    {
        var (v1, v1Hourly) = await V1Async();
        var (retrospective, retrospectiveTimeline) = await RetrospectiveAsync();

        (await ChartAsync()).Should().BeApproximately(Total, 1e-9);
        (await AlertsAsync()).Should().Be((decimal)Total);
        (await WidgetAsync()).Should().Be(-0.02);
        (await PropertiesAsync()).Should().Be(Total);
        (await PebbleAsync()).Should().Be("-0.02");
        v1.Iob.Should().Be(Total);
        v1Hourly.TotalIOB.Should().BeApproximately(Total, 1e-9);
        retrospective.Total.Should().Be(Total);
        retrospectiveTimeline.Iob.Should().Be(Total);
    }

    [Fact]
    public async Task TheParts_AddUpToTheTotal()
    {
        var (v1, v1Hourly) = await V1Async();
        var (retrospective, retrospectiveTimeline) = await RetrospectiveAsync();

        v1.BolusIob.Should().Be(BolusPart);
        v1.BasalIob.Should().Be(BasalPart);
        v1Hourly.BolusIOB.Should().Be(BolusPart);
        v1Hourly.BasalIOB.Should().Be(BasalPart);
        retrospective.Bolus.Should().Be(BolusPart);
        retrospective.Basal.Should().Be(BasalPart);
        (retrospective.Bolus!.Value + retrospective.Basal!.Value).Should().BeApproximately(retrospective.Total, 1e-9);
        retrospectiveTimeline.BolusIob.Should().Be(BolusPart);
        retrospectiveTimeline.BasalIob.Should().Be(BasalPart);
        (retrospectiveTimeline.BolusIob + retrospectiveTimeline.BasalIob).Should().BeApproximately(retrospectiveTimeline.Iob, 1e-9);
    }

    // The device path: beside the same bolus and suspend, a recent device snapshot holds instead of
    // the calculation, the APS snapshot before the pump's. Each is placed before the fixed reading and
    // before the wall clock, which the widget and /properties read at.

    private void ApsReports(double iob, double cob, long minutesBefore)
    {
        foreach (var anchor in new[] { ReadingMills, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() })
            _apsRows.Add(new ApsSnapshot
            {
                Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(anchor - minutesBefore * Minute).UtcDateTime,
                Iob = iob,
                Cob = cob,
                AidAlgorithm = AidAlgorithm.OpenAps,
            });
    }

    private void PumpReports(double iob, long minutesBefore)
    {
        foreach (var anchor in new[] { ReadingMills, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() })
            _pumpRows.Add(new PumpSnapshot
            {
                Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(anchor - minutesBefore * Minute).UtcDateTime,
                Iob = iob,
            });
    }

    private async Task EveryReaderReports(double total, double twoDecimals, string display)
    {
        var (v1, v1Hourly) = await V1Async();
        var (retrospective, retrospectiveTimeline) = await RetrospectiveAsync();

        (await ChartAsync()).Should().Be(total);
        (await AlertsAsync()).Should().Be((decimal)total);
        (await WidgetAsync()).Should().Be(twoDecimals);
        (await PropertiesAsync()).Should().Be(total);
        (await PebbleAsync()).Should().Be(display);
        v1.Iob.Should().Be(total);
        v1Hourly.TotalIOB.Should().Be(total);
        retrospective.Total.Should().Be(total);
        retrospectiveTimeline.Iob.Should().Be(total);
    }

    [Fact]
    public async Task AnApsCoveredInstant_EveryReaderReportsTheApsTotal()
    {
        ApsReports(iob: 1.234, cob: 12, minutesBefore: 2);
        PumpReports(iob: 9.876, minutesBefore: 1);

        await EveryReaderReports(1.234, 1.23, "1.23");
    }

    [Fact]
    public async Task ADeviceSnapshotWithNoTreatments_EveryReaderReportsTheDeviceTotal()
    {
        _noTreatments = true;
        ApsReports(iob: 1.234, cob: 12, minutesBefore: 2);

        await EveryReaderReports(1.234, 1.23, "1.23");
    }

    [Fact]
    public async Task APumpOnlyInstant_EveryReaderReportsThePumpTotal()
    {
        PumpReports(iob: 0.777, minutesBefore: 3);

        await EveryReaderReports(0.777, 0.78, "0.78");
    }

    private async Task<double> ChartAsync()
    {
        var basalSeries = new Mock<IBasalSeriesBuilder>();
        basalSeries
            .Setup(b => b.BuildAsync(It.IsAny<List<TempBasal>>(), It.IsAny<long>(), It.IsAny<long>(), It.IsAny<double>(), It.IsAny<IReadOnlyList<ScaleFactorInterval>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        var stage = new IobCobComputeStage(
            _calculator, new Mock<ICobCalculator>(MockBehavior.Strict).Object, basalSeries.Object,
            _timelines.Object, TestTenantCache.Create(new MemoryCache(new MemoryCacheOptions())),
            NullLogger<IobCobComputeStage>.Instance);

        var result = await stage.ExecuteAsync(new ChartDataContext
        {
            StartTime = ReadingMills,
            EndTime = ReadingMills,
            BufferStartTime = ReadingMills - 8 * 60 * Minute,
            IntervalMinutes = 5,
            DefaultBasalRate = 1.0,
            BolusList = [BolusBefore(ReadingMills)],
            TempBasalList = [SuspendBefore(ReadingMills)],
            ApsSnapshotList = ApsPoints(DateTime.MinValue, DateTime.MaxValue),
            PumpIobPointList = PumpPoints(DateTime.MinValue, DateTime.MaxValue),
        }, CancellationToken.None);

        return result.IobSeries.Single().Value;
    }

    private async Task<decimal?> AlertsAsync()
    {
        var deps = new SensorContextEnricherDependencies(
            Reader(ReadingMills),
            _calculator,
            _cob.Object,
            Mock.Of<ITreatmentService>(),
            Mock.Of<IDeviceEventRepository>(),
            _pump.Object,
            _aps.Object,
            TempBasals(ReadingMills),
            Mock.Of<IUploaderSnapshotRepository>(),
            Mock.Of<IStateSpanService>(),
            Mock.Of<IAlertRepository>(),
            Mock.Of<ITargetRangeScheduleRepository>(),
            Mock.Of<IActiveProfileResolver>(),
            Mock.Of<IProfileActivationResolver>(),
            Mock.Of<ITherapySettingsResolver>(),
            Mock.Of<ISleepService>(),
            Mock.Of<ITrackerRepository>(),
            Mock.Of<IReservoirEstimationService>(),
            Options.Create(new AlertEvaluationOptions()));
        var enricher = new SensorContextEnricher(
            deps, new ServiceCollection().BuildServiceProvider(), new FakeTimeProvider(Reading),
            NullLogger<SensorContextEnricher>.Instance);
        var rule = new AlertRuleSnapshot(
            Id: Guid.NewGuid(), TenantId: Guid.NewGuid(), Name: "iob", ConditionType: AlertConditionType.Iob,
            ConditionParams: """{"operator":"<","value":0}""", Severity: AlertRuleSeverity.Warning,
            ClientConfiguration: "{}", SortOrder: 0, AutoResolveEnabled: false, AutoResolveParams: null);
        var asOf = Reading.UtcDateTime;

        var enriched = await enricher.EnrichAsOfAsync(
            new SensorContext { LatestValue = 110m, LatestTimestamp = asOf, TrendRate = 0m, LastReadingAt = asOf },
            [rule], asOf, CancellationToken.None);

        return enriched.IobUnits;
    }

    private async Task<double> WidgetAsync()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var entries = new Mock<IEntryService>();
        entries.Setup(e => e.GetEntriesAsync(It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        var trackers = new Mock<ITrackerRepository>();
        trackers.Setup(t => t.GetActiveInstancesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        var service = new WidgetSummaryService(
            entries.Object, Reader(now),
            _aps.Object, trackers.Object, Mock.Of<INotificationV1Service>(),
            Mock.Of<IGlucoseStatusClassifier>(), Mock.Of<IAlertRepository>(),
            Mock.Of<ICategoryReadContext>(),
            TimeProvider.System, NullLogger<WidgetSummaryService>.Instance);

        var summary = await service.GetSummaryAsync();

        return summary.Iob;
    }

    private async Task<string?> PebbleAsync()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var entries = new Mock<IEntryService>();
        entries.Setup(e => e.GetEntriesAsync("sgv", It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new Entry { Mills = now, Sgv = 120, Mgdl = 120, Type = "sgv" }]);
        entries.Setup(e => e.GetEntriesAsync("cal", It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        var controller = new PebbleController(
            entries.Object, Reader(now), Mock.Of<IUploaderSnapshotRepository>(),
            Mock.Of<ITenantGlucoseUnits>(), NullLogger<PebbleController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

        var response = (await controller.GetPebbleData(units: "mg/dl")).Result
            .Should().BeOfType<OkObjectResult>().Which.Value.Should().BeOfType<PebbleResponse>().Subject;

        return response.Bgs.Single().Iob;
    }

    [Fact]
    public async Task TheWidgetsCob_ReadsTheTreatmentsItsIobWasCalculatedFrom()
    {
        await WidgetAsync();

        _cob.Verify(c => c.CalculateTotalAsync(
            It.IsAny<List<CarbIntake>>(),
            It.Is<List<Bolus>?>(b => b!.Single().Insulin == 0.5),
            It.Is<List<TempBasal>?>(t => t!.Single().Origin == TempBasalOrigin.Suspended),
            It.IsAny<long>(), true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ANegativeTotal_IsDisplayedTheSameWayEverywhere()
    {
        var (v1, _) = await V1Async();
        var properties = await PropertiesIobAsync();

        v1.Display.Should().Be("-0.02");
        v1.DisplayLine.Should().Be("IOB: -0.02U");
        properties["display"].Should().Be("-0.02");
        properties["displayLine"].Should().Be("IOB: -0.02U");
    }

    [Fact]
    public async Task ADeviceTotal_IsRoundedOnce_SoPropertiesSummaryAndPebbleShowTheSameValue()
    {
        _noTreatments = true;
        ApsReports(iob: 0.1246, cob: 0, minutesBefore: 2);

        var properties = await PropertiesIobAsync();

        properties["iob"].Should().Be(0.125);
        properties["display"].Should().Be("0.13");
        properties["displayLine"].Should().Be("IOB: 0.13U");
        (await SummaryIobAsync()).Should().Be(0.13);
        (await PebbleAsync()).Should().Be("0.13");
    }

    private async Task<double?> SummaryIobAsync()
    {
        var properties = new Mock<IPropertiesService>();
        properties.Setup(p => p.GetAllPropertiesAsync(It.IsAny<DData>(), It.IsAny<IobCobReading?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, object>());
        var therapySettings = new Mock<ITherapySettingsRepository>();
        therapySettings.Setup(t => t.GetDefaultsAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        therapySettings.Setup(t => t.GetStoreIndexAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        var ddata = new Mock<IDDataService>();
        ddata.Setup(d => d.GetDDataAsync(It.IsAny<long>(), It.IsAny<DDataCollections>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DData());
        var service = new SummaryService(
            ddata.Object, properties.Object, Reader(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
            ActiveProfileResolverMock.WithDefault().Object, therapySettings.Object,
            Mock.Of<IProfileProjectionService>(), TimeProvider.System, NullLogger<SummaryService>.Instance);

        return (await service.GetSummaryAsync()).State.Iob;
    }

    private async Task<object?> PropertiesAsync() => (await PropertiesIobAsync())["iob"];

    private async Task<Dictionary<string, object?>> PropertiesIobAsync()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var ddata = new Mock<IDDataService>();
        ddata.Setup(d => d.GetDDataAsync(It.IsAny<long>(), It.IsAny<DDataCollections>(), It.IsAny<CancellationToken>())).ReturnsAsync(new DData());
        var service = new PropertiesService(
            ddata.Object, NullLogger<PropertiesService>.Instance, Reader(now),
            Mock.Of<IAr2Service>(), Mock.Of<IDeviceAgeService>(), Mock.Of<ITherapySettingsResolver>(),
            Mock.Of<ISensitivityResolver>(), Mock.Of<ITargetRangeResolver>(), new BolusWizardService(), new OpenApsService(), TimeProvider.System,
            Options.Create(new DbSizeOptions()));

        var properties = await service.GetPropertiesAsync(["iob"], CancellationToken.None);

        return (Dictionary<string, object?>)properties["iob"];
    }

    [Fact]
    public async Task V1Iob_TakesASnapshotStampedAfterTheInstant_OnlyWithoutARequestedTime()
    {
        // A device clock two minutes ahead: live, the snapshot is the present; at a requested
        // time it is later data.
        _apsRows.Add(new ApsSnapshot
        {
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(ReadingMills + 2 * Minute).UtcDateTime,
            Iob = 1.5,
            AidAlgorithm = AidAlgorithm.OpenAps,
        });
        var controller = new IobController(
            _calculator, _cob.Object, _timelines.Object, _aps.Object, _pump.Object, Boluses(ReadingMills), TempBasals(ReadingMills),
            MockStateSpans.Empty().Object, Reader(ReadingMills), new FakeTimeProvider(Reading), NullLogger<IobController>.Instance);

        var atTime = (await controller.GetCurrentIob(time: ReadingMills)).Result
            .Should().BeOfType<OkObjectResult>().Which.Value.Should().BeOfType<IobResult>().Subject;
        var live = (await controller.GetCurrentIob()).Result
            .Should().BeOfType<OkObjectResult>().Which.Value.Should().BeOfType<IobResult>().Subject;

        atTime.Iob.Should().Be(Total);
        live.Iob.Should().Be(1.5);
    }

    [Fact]
    public async Task V1Treatments_IsTheCalculatedTotal()
    {
        var controller = new IobController(
            _calculator, _cob.Object, _timelines.Object, _aps.Object, _pump.Object, Boluses(ReadingMills), TempBasals(ReadingMills),
            MockStateSpans.Empty().Object, Reader(ReadingMills), new FakeTimeProvider(Reading), NullLogger<IobController>.Instance);

        var result = (await controller.GetIobFromTreatments(time: ReadingMills)).Result
            .Should().BeOfType<OkObjectResult>().Which.Value.Should().BeOfType<IobResult>().Subject;

        result.Iob.Should().Be(Total);
        result.BolusIob.Should().Be(BolusPart);
        result.BasalIob.Should().Be(BasalPart);
    }

    [Fact]
    public async Task V1Hourly_ReadsTempBasalsUpToTheChartsLimit()
    {
        var temps = new Mock<ITempBasalRepository>();
        temps
            .Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        var controller = new IobController(
            _calculator, _cob.Object, _timelines.Object, _aps.Object, _pump.Object, Boluses(ReadingMills), temps.Object, MockStateSpans.Empty().Object,
            Reader(ReadingMills), new FakeTimeProvider(Reading), NullLogger<IobController>.Instance);

        await controller.GetHourlyIob(intervalMinutes: 5, hours: 168, startTime: ReadingMills - 168 * 60 * Minute);

        temps.Verify(r => r.GetAsync(
            It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(),
            ChartDataService.TempBasalQueryLimit, It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task V1Hourly_ReadsNothingStartingAfterTheLastSlot()
    {
        DateTime? bolusTo = null, tempTo = null;
        var boluses = new Mock<IBolusRepository>();
        boluses
            .Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(),
                It.IsAny<BolusKind?>(), It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .Callback((DateTime? _, DateTime? to, string? _, string? _, int _, int _, bool _, BolusKind? _, DateTime? _, Guid? _, CancellationToken _) => bolusTo = to)
            .ReturnsAsync([]);
        var temps = new Mock<ITempBasalRepository>();
        temps
            .Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Callback((DateTime? _, DateTime? to, string? _, string? _, int _, int _, bool _, CancellationToken _) => tempTo = to)
            .ReturnsAsync([]);
        var controller = new IobController(
            _calculator, _cob.Object, _timelines.Object, _aps.Object, _pump.Object, boluses.Object, temps.Object,
            MockStateSpans.Empty().Object, Reader(ReadingMills), new FakeTimeProvider(Reading), NullLogger<IobController>.Instance);

        await controller.GetHourlyIob(intervalMinutes: 5, hours: 1, startTime: ReadingMills - 55 * Minute);

        var lastSlot = DateTimeOffset.FromUnixTimeMilliseconds(ReadingMills).UtcDateTime;
        bolusTo.Should().Be(lastSlot);
        tempTo.Should().Be(lastSlot);
    }

    private async Task<(IobResult Current, HourlyIobData Reading)> V1Async()
    {
        var controller = new IobController(
            _calculator, _cob.Object, _timelines.Object, _aps.Object, _pump.Object, Boluses(ReadingMills), TempBasals(ReadingMills),
            MockStateSpans.Empty().Object, Reader(ReadingMills), new FakeTimeProvider(Reading), NullLogger<IobController>.Instance);

        var current = (await controller.GetCurrentIob(time: ReadingMills)).Result
            .Should().BeOfType<OkObjectResult>().Which.Value.Should().BeOfType<IobResult>().Subject;
        var hourly = (await controller.GetHourlyIob(intervalMinutes: 5, hours: 1, startTime: ReadingMills - 55 * Minute)).Result
            .Should().BeOfType<OkObjectResult>().Which.Value.Should().BeOfType<HourlyIobResponse>().Subject;

        return (current, hourly.Data.Single(d => d.TimeSlot == ReadingMills));
    }

    private async Task<(IobData Point, RetrospectiveDataPoint Timeline)> RetrospectiveAsync()
    {
        var entries = new Mock<IEntryService>();
        entries.Setup(e => e.QueryEntriesAsync(It.IsAny<EntryQuery>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        var deviceStatuses = Mock.Of<IDeviceStatusService>(s => s.GetDeviceStatusesAsync(
                It.IsAny<FindQuery>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>(),
                It.IsAny<bool>(), It.IsAny<ReadSortField>())
            == Task.FromResult<IReadOnlyList<DeviceStatus>>(Array.Empty<DeviceStatus>()));
        var controller = new RetrospectiveController(
            _calculator, _cob.Object, entries.Object, Boluses(ReadingMills), _carbs.Object, TempBasals(ReadingMills),
            deviceStatuses, _timelines.Object, MockStateSpans.Empty().Object, _aps.Object, _pump.Object, Reader(ReadingMills),
            NullLogger<RetrospectiveController>.Instance);
        var http = new DefaultHttpContext();
        http.Items["GrantedScopes"] = new HashSet<string> { Scope.GlucoseRead, Scope.TreatmentsRead };
        controller.ControllerContext = new ControllerContext { HttpContext = http };

        var point = (await controller.GetRetrospectiveData(ReadingMills)).Result
            .Should().BeOfType<OkObjectResult>().Which.Value.Should().BeOfType<RetrospectiveDataResponse>().Subject;
        var timeline = (await controller.GetRetrospectiveTimeline(Reading.ToString("yyyy-MM-dd"))).Result
            .Should().BeOfType<OkObjectResult>().Which.Value.Should().BeOfType<RetrospectiveTimelineResponse>().Subject;

        return (point.Iob!, timeline.Data!.Single(d => d.Time == ReadingMills));
    }
}
