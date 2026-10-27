using Nocturne.API.Tests.Infrastructure;
using Nocturne.API.Services.Treatments;
using Nocturne.Core.Clinical.Treatments;
using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Attributes;
using Nocturne.API.Authorization;
using Nocturne.API.Controllers.V4.Analytics;
using Nocturne.API.Services.Analytics;
using Nocturne.API.Services.Glucose;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Contracts.Analytics;
using Nocturne.Core.Contracts.Entries;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.LoopAnalysis;
using Nocturne.Core.Models.V4;
using Nocturne.Core.Models.Widget;
using Nocturne.Tests.Shared.Mocks;
using Xunit;

namespace Nocturne.API.Tests.Authorization;

/// <summary>
/// Admission and redaction for the V4 analytics controllers whose responses merge several data
/// categories. Modelled on <see cref="ActogramReadScopeGuardTests"/>: each guard is exercised
/// directly, each controller attribute is pinned to its guard's admission list, and each handler is
/// driven once to prove it actually calls the guard.
/// </summary>
[Trait("Category", "Unit")]
public partial class AnalyticsReadScopeGuardTests
{
    private static IReadOnlySet<string> Granted(params string[] scopes) => new HashSet<string>(scopes);

    private static ControllerContext ContextWith(IReadOnlySet<string> scopes)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Items["GrantedScopes"] = scopes;
        return new ControllerContext { HttpContext = httpContext };
    }

    private static RequireScopeAttribute? GateOn<TController>(string action) =>
        typeof(TController).GetMethod(action)!.GetCustomAttribute<RequireScopeAttribute>();

    // ── Chart data ──────────────────────────────────────────────────────────────────────────

    private static DashboardChartData OneRecordPerCategory() => new()
    {
        GlucoseData = [new GlucosePointDto { Time = 1, Sgv = 120 }],
        Thresholds = new ChartThresholdsDto { Low = 70, High = 180 },
        BgCheckMarkers = [new BgCheckMarkerDto { Time = 1, Glucose = 110 }],
        IobSeries = [new TimeSeriesPoint { Timestamp = 1, Value = 2 }],
        CobSeries = [new TimeSeriesPoint { Timestamp = 1, Value = 3 }],
        BasalSeries = [new BasalPoint { Timestamp = 1, Rate = 0.5 }],
        BolusMarkers = [new BolusMarkerDto { Time = 1, Insulin = 1 }],
        CarbMarkers = [new CarbMarkerDto { Time = 1, Carbs = 30 }],
        BasalInjectionMarkers = [new BasalInjectionMarkerDto { Id = "i", Time = 1 }],
        TempBasalSpans = [new ChartStateSpanDto { Id = "tb" }],
        BasalDeliverySpans = [new BasalDeliverySpanDto { Id = "bd" }],
        LastPumpSyncMills = 1,
        DefaultBasalRate = 0.9,
        MaxBasalRate = 1.2,
        MinIob = -0.5,
        MaxIob = 4,
        MaxCob = 40,
        DeviceEventMarkers = [new DeviceEventMarkerDto { Time = 1 }],
        SystemEventMarkers = [new SystemEventMarkerDto { Id = "se", Time = 1 }],
        PumpModeSpans = [new ChartStateSpanDto { Id = "pm", Category = StateSpanCategory.PumpMode }],
        ProfileSpans = [new ChartStateSpanDto { Id = "p", Category = StateSpanCategory.Profile }],
        OverrideSpans = [new ChartStateSpanDto { Id = "o", Category = StateSpanCategory.Override }],
        ActivitySpans =
        [
            new ChartStateSpanDto { Id = "ex", Kind = ChartSpanKind.StateSpan, Category = StateSpanCategory.Exercise },
            new ChartStateSpanDto { Id = "sl", Kind = ChartSpanKind.Sleep },
        ],
        HeartRateSeries = [new HeartRatePointDto { Time = 1, Bpm = 60 }],
        StepSeries = [new StepBubbleDto { Time = 1, Steps = 500 }],
    };

    [Fact]
    public void ChartData_GlucoseOnlyGrant_KeepsGlucoseAndItsBand()
    {
        var data = ChartDataReadScopeGuard.Redact(OneRecordPerCategory(), Granted(Scope.GlucoseRead));

        data.GlucoseData.Should().HaveCount(1);
        data.BgCheckMarkers.Should().HaveCount(1);
        data.Thresholds.Low.Should().Be(70);
        data.IobSeries.Should().BeEmpty();
        data.CobSeries.Should().BeEmpty();
        data.BasalSeries.Should().BeEmpty();
        data.BolusMarkers.Should().BeEmpty();
        data.CarbMarkers.Should().BeEmpty();
        data.BasalInjectionMarkers.Should().BeEmpty();
        data.TempBasalSpans.Should().BeEmpty();
        data.BasalDeliverySpans.Should().BeEmpty();
        data.OverrideSpans.Should().BeEmpty();
        data.DeviceEventMarkers.Should().BeEmpty();
        data.SystemEventMarkers.Should().BeEmpty();
        data.PumpModeSpans.Should().BeEmpty();
        data.ProfileSpans.Should().BeEmpty();
        data.HeartRateSeries.Should().BeEmpty();
        data.StepSeries.Should().BeEmpty();
        data.ActivitySpans.Should().BeEmpty();
    }

    [Fact]
    public void ChartData_WithoutTreatments_ClearsTheAxisMaximaDerivedFromThem()
    {
        // The scaling maxima are computed from the series they scale, so leaving them behind would
        // publish the peak IOB, COB and basal rate of a window the caller may not read.
        var data = ChartDataReadScopeGuard.Redact(OneRecordPerCategory(), Granted(Scope.GlucoseRead));

        data.DefaultBasalRate.Should().Be(0);
        data.MaxBasalRate.Should().Be(0);
        data.MinIob.Should().Be(0);
        data.MaxIob.Should().Be(0);
        data.MaxCob.Should().Be(0);
    }

    [Fact]
    public void ChartData_WithoutGlucose_ClearsTheThresholds()
    {
        var data = ChartDataReadScopeGuard.Redact(OneRecordPerCategory(), Granted(Scope.DevicesRead));

        data.Thresholds.Should().BeEquivalentTo(new ChartThresholdsDto());
        data.DeviceEventMarkers.Should().HaveCount(1);
        data.SystemEventMarkers.Should().HaveCount(1);
        data.PumpModeSpans.Should().HaveCount(1);
    }

    /// <summary>
    /// The activity track is the one field carrying two categories, so it is filtered per span:
    /// a treatments grant keeps the exercise/illness/travel annotations and drops the sleep
    /// sessions projected in beside them, and a sleep grant does the reverse.
    /// </summary>
    [Fact]
    public void ChartData_ActivitySpans_AreFilteredPerSpanNotPerField()
    {
        var treatments = ChartDataReadScopeGuard.Redact(
            OneRecordPerCategory(), Granted(Scope.TreatmentsRead));
        treatments.ActivitySpans.Should().ContainSingle().Which.Id.Should().Be("ex");

        var sleep = ChartDataReadScopeGuard.Redact(
            OneRecordPerCategory(), Granted(Scope.SleepRead));
        sleep.ActivitySpans.Should().ContainSingle().Which.Id.Should().Be("sl");
    }

    [Fact]
    public void ChartData_LastPumpSync_NeedsBothTheBasalAndThePumpStatusScopes()
    {
        ChartDataReadScopeGuard.Redact(OneRecordPerCategory(), Granted(Scope.TreatmentsRead))
            .LastPumpSyncMills.Should().BeNull();
        ChartDataReadScopeGuard.Redact(OneRecordPerCategory(), Granted(Scope.DevicesRead))
            .LastPumpSyncMills.Should().BeNull();
        ChartDataReadScopeGuard.Redact(OneRecordPerCategory(), Granted(Scope.TreatmentsRead, Scope.DevicesRead))
            .LastPumpSyncMills.Should().Be(1);
    }

    [Fact]
    public void ChartData_TherapyOnlyGrant_KeepsOnlyTheProfileSwitches()
    {
        var data = ChartDataReadScopeGuard.Redact(OneRecordPerCategory(), Granted(Scope.TherapyRead));

        data.ProfileSpans.Should().HaveCount(1);
        data.GlucoseData.Should().BeEmpty();
        data.PumpModeSpans.Should().BeEmpty();
        data.OverrideSpans.Should().BeEmpty();
    }

    [Fact]
    public void ChartData_FullAccess_KeepsEveryCategory()
    {
        var data = ChartDataReadScopeGuard.Redact(OneRecordPerCategory(), Granted(Scope.FullAccess));

        data.GlucoseData.Should().HaveCount(1);
        data.IobSeries.Should().HaveCount(1);
        data.DeviceEventMarkers.Should().HaveCount(1);
        data.ProfileSpans.Should().HaveCount(1);
        data.HeartRateSeries.Should().HaveCount(1);
        data.StepSeries.Should().HaveCount(1);
        data.ActivitySpans.Should().HaveCount(2);
        data.MinIob.Should().Be(-0.5);
        data.MaxIob.Should().Be(4);
    }

    [Fact]
    public void ChartData_ReadWriteGrant_SatisfiesTheReadCategory()
    {
        var data = ChartDataReadScopeGuard.Redact(OneRecordPerCategory(), Granted(Scope.HeartRateReadWrite));

        data.HeartRateSeries.Should().HaveCount(1);
        data.GlucoseData.Should().BeEmpty();
    }

    [Fact]
    public void ChartData_NoScopes_EmptiesEveryCategory()
    {
        var data = ChartDataReadScopeGuard.Redact(OneRecordPerCategory(), Granted());

        data.GlucoseData.Should().BeEmpty();
        data.IobSeries.Should().BeEmpty();
        data.DeviceEventMarkers.Should().BeEmpty();
        data.ProfileSpans.Should().BeEmpty();
        data.HeartRateSeries.Should().BeEmpty();
        data.StepSeries.Should().BeEmpty();
        data.ActivitySpans.Should().BeEmpty();
    }

    [Fact]
    public void ChartData_AdmissionScopes_CoverEveryMergedCategory()
    {
        ChartDataReadScopeGuard.AdmissionScopes.Should().BeEquivalentTo(new[]
        {
            Scope.GlucoseRead,
            Scope.TreatmentsRead,
            Scope.DevicesRead,
            Scope.TherapyRead,
            Scope.HeartRateRead,
            Scope.StepCountRead,
            Scope.SleepRead,
        });
    }

    [Fact]
    public void ChartData_GateMatchesItsGuard()
    {
        var gate = GateOn<ChartDataController>(nameof(ChartDataController.GetDashboardChartData));

        gate.Should().NotBeNull();
        gate!.RequiresAll.Should().BeFalse("holding one category must admit the caller");
        gate.RequiredScopes.Should().BeEquivalentTo(ChartDataReadScopeGuard.AdmissionScopes);

        GateOn<ChartDataController>(nameof(ChartDataController.GetBasalSeries))!
            .RequiredScopes.Should().Equal(Scope.TreatmentsRead);
    }

    [Fact]
    public async Task ChartData_Handler_RedactsTheCategoriesTheCallerLacks()
    {
        var service = new Mock<IChartDataService>();
        service
            .Setup(s => s.GetDashboardChartDataAsync(
                It.IsAny<long>(), It.IsAny<long>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(OneRecordPerCategory());

        var controller = new ChartDataController(service.Object, NullLogger<ChartDataController>.Instance)
        {
            ControllerContext = ContextWith(Granted(Scope.GlucoseRead)),
        };

        var result = await controller.GetDashboardChartData(startTime: 0, endTime: 1);

        var data = result.Result.Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeOfType<DashboardChartData>().Subject;
        data.GlucoseData.Should().HaveCount(1);
        data.IobSeries.Should().BeEmpty();
        data.HeartRateSeries.Should().BeEmpty();
    }

    // ── Retrospective ───────────────────────────────────────────────────────────────────────

    private static RetrospectiveDataResponse BothCategories() => new()
    {
        Time = 1,
        Glucose = new GlucoseData { Value = 120 },
        Iob = new IobData { Total = 2 },
        Cob = new CobData { Total = 20 },
        Basal = new BasalData { Rate = 0.8 },
        RecentTreatments = [new TreatmentSummaryData { Id = "b", Mills = 1 }],
    };

    [Fact]
    public void Retrospective_GlucoseOnlyGrant_DropsTheDosingState()
    {
        var data = RetrospectiveReadScopeGuard.Redact(BothCategories(), Granted(Scope.GlucoseRead));

        data.Glucose.Should().NotBeNull();
        data.Iob.Should().BeNull();
        data.Cob.Should().BeNull();
        data.Basal.Should().BeNull();
        data.RecentTreatments.Should().BeEmpty();
    }

    [Fact]
    public void Retrospective_TreatmentsOnlyGrant_DropsTheGlucose()
    {
        var data = RetrospectiveReadScopeGuard.Redact(BothCategories(), Granted(Scope.TreatmentsRead));

        data.Glucose.Should().BeNull();
        data.Iob.Should().NotBeNull();
        data.RecentTreatments.Should().HaveCount(1);
    }

    /// <summary>
    /// The timeline interleaves both categories in every point, so redaction has to reach inside
    /// the points rather than empty a collection.
    /// </summary>
    [Fact]
    public void Retrospective_Timeline_RedactsPerFieldWithinEachPoint()
    {
        var timeline = new RetrospectiveTimelineResponse
        {
            Data = [new RetrospectiveDataPoint
            {
                Glucose = 120, GlucoseDirection = "Flat",
                Iob = 2, BolusIob = 1, BasalIob = 1, Cob = 20, BasalRate = 0.8, IsTemp = true,
            }],
        };

        var point = RetrospectiveReadScopeGuard
            .Redact(timeline, Granted(Scope.GlucoseRead)).Data!.Single();

        point.Glucose.Should().Be(120);
        point.GlucoseDirection.Should().Be("Flat");
        point.Iob.Should().Be(0);
        point.BolusIob.Should().Be(0);
        point.BasalIob.Should().Be(0);
        point.Cob.Should().Be(0);
        point.BasalRate.Should().Be(0);
        point.IsTemp.Should().BeFalse();
    }

    [Fact]
    public void Retrospective_Timeline_TreatmentsOnlyGrant_DropsTheGlucose()
    {
        var timeline = new RetrospectiveTimelineResponse
        {
            Data = [new RetrospectiveDataPoint { Glucose = 120, GlucoseDirection = "Flat", Iob = 2 }],
        };

        var point = RetrospectiveReadScopeGuard
            .Redact(timeline, Granted(Scope.TreatmentsRead)).Data!.Single();

        point.Glucose.Should().BeNull();
        point.GlucoseDirection.Should().BeNull();
        point.Iob.Should().Be(2);
    }

    [Fact]
    public void Retrospective_GatesMatchTheirGuard()
    {
        foreach (var action in new[]
                 {
                     nameof(RetrospectiveController.GetRetrospectiveData),
                     nameof(RetrospectiveController.GetRetrospectiveTimeline),
                 })
        {
            var gate = GateOn<RetrospectiveController>(action)!;

            gate.RequiredScopes.Should().BeEquivalentTo(RetrospectiveReadScopeGuard.AdmissionScopes);
            gate.RequiresAll.Should().BeFalse("holding one category must admit the caller");
        }

        // The basal timeline is wholly the treatment category, so it is not broadened to the OR.
        GateOn<RetrospectiveController>(nameof(RetrospectiveController.GetBasalTimeline))!
            .RequiredScopes.Should().Equal(Scope.TreatmentsRead);
    }

    [Fact]
    public async Task Retrospective_Handler_RedactsTheCategoriesTheCallerLacks()
    {
        var entries = new Mock<IEntryService>();
        entries
            .Setup(s => s.QueryEntriesAsync(It.IsAny<EntryQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new Entry { Mills = 1_000, Sgv = 120, Direction = "Flat" }]);

        var boluses = new Mock<IBolusRepository>();
        boluses
            .Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(),
                It.IsAny<BolusKind?>(), It.IsAny<DateTime?>(), It.IsAny<Guid?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var carbs = new Mock<ICarbIntakeRepository>();
        carbs
            .Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var tempBasals = new Mock<ITempBasalRepository>();
        tempBasals
            .Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var iob = new Mock<IIobCalculator>();
        iob
            .Setup(c => c.CalculateTotalAsync(
                It.IsAny<List<Bolus>>(), It.IsAny<List<TempBasal>?>(),
                It.IsAny<long>(), It.IsAny<long?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IobResult());

        var cob = new Mock<ICobCalculator>();
        cob
            .Setup(c => c.CalculateTotalAsync(
                It.IsAny<List<CarbIntake>>(), It.IsAny<List<Bolus>?>(),
                It.IsAny<List<TempBasal>?>(), It.IsAny<long>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CobResult());

        var timelines = new Mock<ITherapyTimelineResolver>();
        timelines
            .Setup(r => r.BuildAsync(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((long from, long to, string? _, CancellationToken _) =>
                new TherapyTimeline([new TherapySegment(from, to, new TherapySnapshot(3.0, 30.0, null, null, 0, null, null, null))]));

        var controller = new RetrospectiveController(
            iob.Object, cob.Object, entries.Object, boluses.Object, carbs.Object,
            tempBasals.Object, projectionService: null!, timelines.Object, MockStateSpans.Empty().Object, Mock.Of<IApsSnapshotRepository>(),
            Mock.Of<IPumpSnapshotRepository>(), new IobCobReader(iob.Object, cob.Object, boluses.Object, tempBasals.Object, carbs.Object, TestTenantCache.Create(partition: null), NullLogger<IobCobReader>.Instance),
            NullLogger<RetrospectiveController>.Instance)
        {
            ControllerContext = ContextWith(Granted(Scope.GlucoseRead)),
        };

        var result = await controller.GetRetrospectiveData(time: 1_000);

        var data = result.Result.Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeOfType<RetrospectiveDataResponse>().Subject;
        data.Glucose.Should().NotBeNull();
        data.Iob.Should().BeNull();
        data.Cob.Should().BeNull();
        data.Basal.Should().BeNull();
    }

    // ── Predictions ─────────────────────────────────────────────────────────────────────────

    private static GlucosePredictionResponse Forecast() => new()
    {
        CurrentBg = 120,
        Delta = 3,
        EventualBg = 140,
        Iob = 2,
        Cob = 20,
        SensitivityRatio = 1.1,
        Predictions = new PredictionCurves { Default = [120, 125] },
    };

    [Fact]
    public void Prediction_GlucoseOnlyGrant_DropsTheDosingScalars()
    {
        var data = PredictionReadScopeGuard.Redact(Forecast(), Granted(Scope.GlucoseRead));

        data.CurrentBg.Should().Be(120);
        data.Predictions.Default.Should().HaveCount(2);
        data.Iob.Should().Be(0);
        data.Cob.Should().Be(0);
        data.SensitivityRatio.Should().BeNull();
    }

    [Fact]
    public void Prediction_TreatmentsOnlyGrant_DropsTheCurves()
    {
        var data = PredictionReadScopeGuard.Redact(Forecast(), Granted(Scope.TreatmentsRead));

        data.CurrentBg.Should().Be(0);
        data.Delta.Should().Be(0);
        data.EventualBg.Should().Be(0);
        data.Predictions.Default.Should().BeNull();
        data.Iob.Should().Be(2);
    }

    /// <summary>
    /// The profile snapshot is the resolved therapy profile — basal schedule, ISF, carb ratio,
    /// targets — so it is the therapy category alone, not the forecast's OR.
    /// </summary>
    [Fact]
    public void Prediction_GatesMatchTheirCategories()
    {
        foreach (var action in new[]
                 {
                     nameof(PredictionController.GetPredictions),
                     nameof(PredictionController.GetStatus),
                 })
        {
            var gate = GateOn<PredictionController>(action)!;

            gate.RequiredScopes.Should().BeEquivalentTo(PredictionReadScopeGuard.AdmissionScopes);
            gate.RequiresAll.Should().BeFalse("holding one category must admit the caller");
        }

        GateOn<PredictionController>(nameof(PredictionController.GetProfileSnapshot))!
            .RequiredScopes.Should().Equal(Scope.TherapyRead);
    }

    [Fact]
    public async Task Prediction_Handler_RedactsTheCategoriesTheCallerLacks()
    {
        var predictions = new Mock<IPredictionService>();
        predictions
            .Setup(s => s.GetPredictionsAsync(
                It.IsAny<string?>(), It.IsAny<DateTimeOffset?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Forecast());

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Predictions:Source"] = "DeviceStatus" })
            .Build();

        var controller = new PredictionController(
            NullLogger<PredictionController>.Instance,
            configuration,
            Mock.Of<IProfileSnapshotService>(),
            predictions.Object)
        {
            ControllerContext = ContextWith(Granted(Scope.GlucoseRead)),
        };

        var result = await controller.GetPredictions();

        var data = result.Result.Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeOfType<GlucosePredictionResponse>().Subject;
        data.CurrentBg.Should().Be(120);
        data.Iob.Should().Be(0);
        data.SensitivityRatio.Should().BeNull();
    }

    // ── Correlation ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Correlation_GateMatchesItsGuard()
    {
        var gate = GateOn<CorrelationController>(nameof(CorrelationController.GetCorrelated))!;

        gate.RequiredScopes.Should().BeEquivalentTo(CorrelationReadScopeGuard.AdmissionScopes);
        gate.RequiresAll.Should().BeFalse("holding one category must admit the caller");
    }

    /// <summary>
    /// A glucose-only caller must not learn that a bolus, carb intake, note or calculation shares
    /// the correlation id, so the treatment repositories are not queried at all.
    /// </summary>
    [Fact]
    public async Task Correlation_Handler_SkipsTheRepositoriesTheCallerCannotRead()
    {
        var sensor = new Mock<ISensorGlucoseRepository>();
        var bolus = new Mock<IBolusRepository>();
        sensor.Setup(r => r.GetByCorrelationIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var controller = new CorrelationController(
            sensor.Object,
            Mock.Of<IMeterGlucoseRepository>(),
            Mock.Of<ICalibrationRepository>(),
            bolus.Object,
            Mock.Of<IBolusCalculationRepository>(),
            Mock.Of<ICarbIntakeRepository>(),
            Mock.Of<IBGCheckRepository>(),
            Mock.Of<INoteRepository>())
        {
            ControllerContext = ContextWith(Granted(Scope.GlucoseRead)),
        };

        await controller.GetCorrelated(Guid.NewGuid());

        sensor.Verify(r => r.GetByCorrelationIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Once);
        bolus.Verify(r => r.GetByCorrelationIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── Widget summary ──────────────────────────────────────────────────────────────────────

    private static V4SummaryResponse Summary() => new()
    {
        Current = new V4GlucoseReading { Sgv = 120 },
        History = [new V4GlucoseReading { Sgv = 118 }],
        Iob = 2,
        Cob = 20,
        Alarm = new V4AlarmState(),
        Predictions = new V4Predictions(),
        Trackers = [new V4TrackerStatus()],
    };

    [Fact]
    public void WidgetSummary_GlucoseOnlyGrant_DropsDosingAndAlarm()
    {
        var data = WidgetSummaryReadScopeGuard.Redact(Summary(), Granted(Scope.GlucoseRead));

        data.Current.Should().NotBeNull();
        data.History.Should().HaveCount(1);
        data.Predictions.Should().NotBeNull();
        data.Iob.Should().Be(0);
        data.Cob.Should().Be(0);
        data.Alarm.Should().BeNull();
    }

    [Fact]
    public void WidgetSummary_AlertsOnlyGrant_KeepsOnlyTheAlarm()
    {
        var data = WidgetSummaryReadScopeGuard.Redact(Summary(), Granted(Scope.AlertsRead));

        data.Alarm.Should().NotBeNull();
        data.Current.Should().BeNull();
        data.History.Should().BeEmpty();
        data.Predictions.Should().BeNull();
        data.Iob.Should().Be(0);
    }

    /// <summary>
    /// Tracker statuses carry no share data category and are served by the fallback policy
    /// everywhere else, so they are not part of the redaction.
    /// </summary>
    [Fact]
    public void WidgetSummary_NoScopes_LeavesTheTrackersAlone()
    {
        var data = WidgetSummaryReadScopeGuard.Redact(Summary(), Granted());

        data.Trackers.Should().HaveCount(1);
        data.Current.Should().BeNull();
        data.Alarm.Should().BeNull();
    }

    [Fact]
    public void WidgetSummary_GateMatchesItsGuard()
    {
        var gate = GateOn<SummaryController>(nameof(SummaryController.GetSummary));

        gate!.RequiresAll.Should().BeFalse("holding one category must admit the caller");
        gate.RequiredScopes.Should().BeEquivalentTo(WidgetSummaryReadScopeGuard.AdmissionScopes);
    }

    [Fact]
    public async Task WidgetSummary_Handler_RedactsTheCategoriesTheCallerLacks()
    {
        var service = new Mock<IWidgetSummaryService>();
        service
            .Setup(s => s.GetSummaryAsync(
                It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Summary());

        var context = ContextWith(Granted(Scope.GlucoseRead));
        context.HttpContext.Items["AuthContext"] = new AuthContext
        {
            IsAuthenticated = true,
            SubjectId = Guid.NewGuid(),
        };

        var controller = new SummaryController(service.Object, NullLogger<SummaryController>.Instance)
        {
            ControllerContext = context,
        };

        var result = await controller.GetSummary();

        var data = result.Result.Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeOfType<V4SummaryResponse>().Subject;
        data.Current.Should().NotBeNull();
        data.Iob.Should().Be(0);
        data.Alarm.Should().BeNull();
    }

    // ── Current therapy state ───────────────────────────────────────────────────────────────

    private static CurrentTherapyStateResponse TherapyState() => new()
    {
        CurrentPumpMode = PumpModeState.Manual,
        SensitivityPercent = 90,
        Reservoir = 42,
        PumpBatteryPercent = 80,
        PumpBatteryVoltage = 1.4,
        Thresholds = new ChartThresholdsDto { Low = 70, High = 180, TargetLow = 90, TargetHigh = 140 },
    };

    [Fact]
    public void CurrentTherapyState_DevicesOnlyGrant_DropsTheSensitivity()
    {
        var data = CurrentTherapyStateReadScopeGuard.Redact(TherapyState(), Granted(Scope.DevicesRead));

        data.CurrentPumpMode.Should().Be(PumpModeState.Manual);
        data.Reservoir.Should().Be(42);
        data.PumpBatteryPercent.Should().Be(80);
        data.PumpBatteryVoltage.Should().Be(1.4);
        data.SensitivityPercent.Should().BeNull();
    }

    [Fact]
    public void CurrentTherapyState_TherapyOnlyGrant_DropsThePumpReadings()
    {
        var data = CurrentTherapyStateReadScopeGuard.Redact(TherapyState(), Granted(Scope.TherapyRead));

        data.SensitivityPercent.Should().Be(90);
        data.CurrentPumpMode.Should().BeNull();
        data.Reservoir.Should().BeNull();
        data.PumpBatteryPercent.Should().BeNull();
        data.PumpBatteryVoltage.Should().BeNull();
    }

    [Fact]
    public void CurrentTherapyState_Thresholds_FollowGlucose()
    {
        var withGlucose = CurrentTherapyStateReadScopeGuard.Redact(
            TherapyState(), Granted(Scope.GlucoseRead));
        withGlucose.Thresholds.Should().Be(TherapyState().Thresholds);

        var withoutGlucose = CurrentTherapyStateReadScopeGuard.Redact(
            TherapyState(), Granted(Scope.TherapyRead, Scope.DevicesRead));
        withoutGlucose.Thresholds.Should().Be(new ChartThresholdsDto());
    }

    [Fact]
    public void CurrentTherapyState_GlucoseOnlyGrant_KeepsOnlyTheThresholds()
    {
        var data = CurrentTherapyStateReadScopeGuard.Redact(TherapyState(), Granted(Scope.GlucoseRead));

        data.Thresholds.Should().Be(TherapyState().Thresholds);
        data.CurrentPumpMode.Should().BeNull();
        data.Reservoir.Should().BeNull();
        data.PumpBatteryPercent.Should().BeNull();
        data.PumpBatteryVoltage.Should().BeNull();
        data.SensitivityPercent.Should().BeNull();
    }

    /// <summary>
    /// The sidebar sparkline takes its thresholds from this endpoint, so a caller who can read the
    /// glucose series (a share link, a guest, the Viewer role) must be admitted to read them.
    /// </summary>
    [Fact]
    public void CurrentTherapyState_AdmitsAGlucoseOnlyCaller()
    {
        CurrentTherapyStateReadScopeGuard.AdmissionScopes.Should().BeEquivalentTo(new[]
        {
            Scope.GlucoseRead,
            Scope.DevicesRead,
            Scope.TherapyRead,
        });
    }

    /// <summary>
    /// The Viewer role holds glucose and reports only, so it is admitted for the thresholds and
    /// reaches neither the device nor the therapy fields.
    /// </summary>
    [Fact]
    public void CurrentTherapyState_ViewerScopes_SeeOnlyTheThresholds()
    {
        var viewer = Scope.NormalizeMemberPermissions(
            RoleSeeds.Permissions[RoleSeeds.Viewer]);

        CurrentTherapyStateReadScopeGuard.AdmissionScopes
            .Should().Contain(s => Scope.Satisfies(viewer, s));

        var data = CurrentTherapyStateReadScopeGuard.Redact(TherapyState(), viewer);
        data.Thresholds.Should().Be(TherapyState().Thresholds);
        data.CurrentPumpMode.Should().BeNull();
        data.Reservoir.Should().BeNull();
        data.SensitivityPercent.Should().BeNull();
    }

    [Fact]
    public void CurrentTherapyState_GateMatchesItsGuard()
    {
        var gate = GateOn<CurrentTherapyStateController>(
            nameof(CurrentTherapyStateController.GetCurrentTherapyState));

        gate!.RequiresAll.Should().BeFalse("holding one category must admit the caller");
        gate.RequiredScopes.Should().BeEquivalentTo(CurrentTherapyStateReadScopeGuard.AdmissionScopes);
    }

    [Fact]
    public async Task CurrentTherapyState_Handler_RedactsTheCategoriesTheCallerLacks()
    {
        var stateSpans = new Mock<IStateSpanService>();
        stateSpans.Setup(s => s.GetCurrentPumpModeAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(PumpModeState.Manual);

        var sensitivity = new Mock<ISensitivityResolver>();
        sensitivity.Setup(s => s.GetCurrentSensitivityPercentAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(90);

        var pumps = new Mock<IPumpSnapshotRepository>();
        pumps.Setup(r => r.GetLatestAsync(It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PumpSnapshot { Reservoir = 42 });

        var controller = new CurrentTherapyStateController(
            stateSpans.Object, sensitivity.Object, pumps.Object,
            Mock.Of<ITherapySettingsResolver>(), Mock.Of<ITargetRangeResolver>())
        {
            ControllerContext = ContextWith(Granted(Scope.TherapyRead)),
        };

        var result = await controller.GetCurrentTherapyState();

        var data = result.Result.Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeOfType<CurrentTherapyStateResponse>().Subject;
        data.SensitivityPercent.Should().Be(90);
        data.CurrentPumpMode.Should().BeNull();
        data.Reservoir.Should().BeNull();
    }

    [Fact]
    public async Task CurrentTherapyState_Handler_GlucoseOnlyCaller_GetsTheThresholdsAlone()
    {
        var stateSpans = new Mock<IStateSpanService>();
        stateSpans.Setup(s => s.GetCurrentPumpModeAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(PumpModeState.Manual);

        var sensitivity = new Mock<ISensitivityResolver>();
        sensitivity.Setup(s => s.GetCurrentSensitivityPercentAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(90);

        var pumps = new Mock<IPumpSnapshotRepository>();
        pumps.Setup(r => r.GetLatestAsync(It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PumpSnapshot { Reservoir = 42, BatteryPercent = 80, BatteryVoltage = 1.4 });

        var controller = new CurrentTherapyStateController(
            stateSpans.Object, sensitivity.Object, pumps.Object,
            Mock.Of<ITherapySettingsResolver>(), Mock.Of<ITargetRangeResolver>())
        {
            ControllerContext = ContextWith(Granted(Scope.GlucoseRead)),
        };

        var result = await controller.GetCurrentTherapyState();

        var data = result.Result.Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeOfType<CurrentTherapyStateResponse>().Subject;
        data.Thresholds.GlucoseYMax.Should().BeGreaterThan(0);
        data.Thresholds.Should().NotBe(new ChartThresholdsDto());
        data.CurrentPumpMode.Should().BeNull();
        data.SensitivityPercent.Should().BeNull();
        data.Reservoir.Should().BeNull();
        data.PumpBatteryPercent.Should().BeNull();
        data.PumpBatteryVoltage.Should().BeNull();
    }

    // ── Usage analytics ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The usage-analytics controller carries no health data: its reads are the collection switch,
    /// system info and usage counters, and its writes reconfigure or wipe them. So it is gated on
    /// the tenant-settings permission rather than any data category, and a member holding only a
    /// health-read scope — the whole Viewer, Clinician and Caretaker surface — is refused.
    /// </summary>
    [Fact]
    public void UsageAnalytics_IsGatedOnTheTenantSettingsPermission()
    {
        var gate = typeof(AnalyticsController).GetCustomAttribute<RequireScopeAttribute>();

        gate.Should().NotBeNull();
        gate!.RequiredScopes.Should().Equal(Scope.TenantSettings);

        foreach (var role in new[]
                 {
                     RoleSeeds.Viewer,
                     RoleSeeds.Clinician,
                     RoleSeeds.Caretaker,
                 })
        {
            Scope.Satisfies(
                Scope.NormalizeMemberPermissions(RoleSeeds.Permissions[role]),
                Scope.TenantSettings)
                .Should().BeFalse($"{role} does not administer the tenant");
        }

        Scope.Satisfies(
            Scope.NormalizeMemberPermissions(
                RoleSeeds.Permissions[RoleSeeds.Admin]),
            Scope.TenantSettings)
            .Should().BeTrue("an administrator must keep managing analytics collection");
    }

    // ── Multi-period statistics ─────────────────────────────────────────────────────────────

    private static PeriodStatistics PeriodWithTreatments(int days) => new()
    {
        PeriodDays = days,
        Analytics = new GlucoseAnalytics
        {
            BasicStats = new BasicGlucoseStats { Count = 500, Mean = 7.4, Median = 7.1 },
            TimeInRange = new TimeInRangeMetrics { Percentages = new TimeInRangePercentages { Low = 4.5 } },
            GlycemicVariability = new GlycemicVariability { CoefficientOfVariation = 31.5, StandardDeviation = 2.3 },
            DataQuality = new DataQuality
            {
                TotalReadings = 500,
                MissingReadings = 12,
                DataCompleteness = 97.5,
                CgmActivePercent = 95.5,
                GapAnalysis = new GapAnalysis { LongestGap = 45, AverageGap = 20 },
                NoiseLevel = 0.25,
                CalibrationEvents = 4,
                SensorWarmups = 3,
            },
            Reliability = new StatisticReliability { MeetsReliabilityCriteria = true, DaysOfData = days, ReadingCount = 500 },
            Time = new AnalysisTime { Start = 1_000, End = 2_000, TimeOfAnalysis = 3_000 },
            ClinicalAssessment = new ClinicalTargetAssessment { TargetsMet = 2, TotalTargets = 5 },
        },
        TreatmentSummary = new TreatmentSummary(),
        InsulinDelivery = new InsulinDeliveryStatistics(),
        HasSufficientData = true,
        EntryCount = 12,
        TreatmentCount = 2,
    };

    private static MultiPeriodStatistics AllPeriodsWithTreatments() => new()
    {
        LastDay = PeriodWithTreatments(1),
        Last3Days = PeriodWithTreatments(3),
        LastWeek = PeriodWithTreatments(7),
        LastMonth = PeriodWithTreatments(30),
        Last90Days = PeriodWithTreatments(90),
    };

    [Fact]
    public void MultiPeriod_GlucoseOnlyGrant_DropsTreatmentFieldsFromACopy()
    {
        var cached = AllPeriodsWithTreatments();

        var redacted = StatisticsReadScopeGuard.Redact(cached, Granted(Scope.GlucoseRead));

        var periods = new[] { redacted.LastDay, redacted.Last3Days, redacted.LastWeek, redacted.LastMonth, redacted.Last90Days };
        periods.Should().OnlyContain(p =>
            p.TreatmentSummary == null && p.InsulinDelivery == null && p.TreatmentCount == 0
            && p.Analytics != null && p.EntryCount == 12 && p.HasSufficientData);
        cached.LastDay.TreatmentSummary.Should().NotBeNull("the cached instance is shared by every reader");
        cached.Last90Days.InsulinDelivery.Should().NotBeNull();
        cached.LastWeek.TreatmentCount.Should().Be(2);
    }

    [Fact]
    public void MultiPeriod_TreatmentsGrant_KeepsTreatmentFields()
    {
        var cached = AllPeriodsWithTreatments();

        var result = StatisticsReadScopeGuard.Redact(cached, Granted(Scope.GlucoseRead, Scope.TreatmentsReadWrite));

        result.LastDay.TreatmentSummary.Should().NotBeNull();
        result.LastDay.InsulinDelivery.Should().NotBeNull();
        result.LastDay.TreatmentCount.Should().Be(2);
    }

    [Fact]
    public void MultiPeriod_GlucoseOnlyGrant_KeepsTheTimeZoneReasonOfEachPeriod()
    {
        var cached = AllPeriodsWithTreatments();
        foreach (var period in Periods(cached))
            period.Analytics!.TimeZoneUnavailableReason = TimeZoneUnavailableReason.NotConfigured;

        var redacted = StatisticsReadScopeGuard.Redact(cached, Granted(Scope.GlucoseRead));

        Periods(redacted).Should().OnlyContain(p =>
            p.Analytics!.TimeZoneUnavailableReason == TimeZoneUnavailableReason.NotConfigured);
    }

    [Fact]
    public void MultiPeriod_ReportsReadGrant_KeepsTreatmentAggregates()
    {
        var cached = AllPeriodsWithTreatments();

        var result = StatisticsReadScopeGuard.Redact(cached, Granted(Scope.ReportsRead));

        Periods(result).Should().OnlyContain(p =>
            p.TreatmentSummary != null && p.InsulinDelivery != null && p.TreatmentCount == 2);
    }

    [Theory]
    [InlineData(Scope.GlucoseRead, false)]
    [InlineData(Scope.ReportsRead, true)]
    [InlineData(Scope.TreatmentsRead, true)]
    [InlineData(Scope.TreatmentsReadWrite, true)]
    public void AllowsTreatmentAggregates_NeedsReportsOrTreatmentsRead(string scope, bool expected)
    {
        StatisticsReadScopeGuard.AllowsTreatmentAggregates(Granted(scope)).Should().Be(expected);
    }

    [Fact]
    public void Statistics_PeriodsAndPunchCardAdmitGlucoseOrReportsRead()
    {
        StatisticsReadScopeGuard.AdmissionScopes.Should().BeEquivalentTo([Scope.GlucoseRead, Scope.ReportsRead]);

        foreach (var action in new[] { nameof(StatisticsController.GetMultiPeriodStatistics), nameof(StatisticsController.GetPunchCardData) })
        {
            var gate = GateOn<StatisticsController>(action)!;
            gate.RequiredScopes.Should().BeEquivalentTo(StatisticsReadScopeGuard.AdmissionScopes);
            gate.RequiresAll.Should().BeFalse("holding either scope must admit the caller");
        }
    }

    private static PeriodStatistics[] Periods(MultiPeriodStatistics data) =>
        [data.LastDay, data.Last3Days, data.LastWeek, data.LastMonth, data.Last90Days];

    [Fact]
    public void MultiPeriod_GlucoseOnlyGrant_NarrowsTreatmentsAndWarmupsTogetherFromACopy()
    {
        var cached = AllPeriodsWithTreatments();

        var redacted = StatisticsReadScopeGuard.Redact(cached, Granted(Scope.GlucoseRead));

        Periods(redacted).Should().OnlyContain(p =>
            p.Analytics!.DataQuality.SensorWarmups == 0
            && p.TreatmentSummary == null && p.InsulinDelivery == null && p.TreatmentCount == 0);
        foreach (var (narrowed, original) in Periods(redacted).Zip(Periods(cached)))
        {
            narrowed.Analytics.Should().BeEquivalentTo(
                original.Analytics, o => o.Excluding(a => a!.DataQuality.SensorWarmups));
        }
        Periods(cached).Should().OnlyContain(p =>
            p.Analytics!.DataQuality.SensorWarmups == 3 && p.TreatmentSummary != null && p.TreatmentCount == 2);
    }

    [Fact]
    public void GlucoseAnalytics_CarriesOnlyThePropertiesTheGuardCopies()
    {
        PropertyNames<GlucoseAnalytics>().Should().BeEquivalentTo(
        [
            nameof(GlucoseAnalytics.BasicStats),
            nameof(GlucoseAnalytics.TimeInRange),
            nameof(GlucoseAnalytics.GlycemicVariability),
            nameof(GlucoseAnalytics.DataQuality),
            nameof(GlucoseAnalytics.Reliability),
            nameof(GlucoseAnalytics.Time),
            nameof(GlucoseAnalytics.ClinicalAssessment),
            nameof(GlucoseAnalytics.TimeZoneUnavailableReason),
        ]);
    }

    [Fact]
    public void DataQuality_CarriesOnlyThePropertiesTheGuardCopies()
    {
        PropertyNames<DataQuality>().Should().BeEquivalentTo(
        [
            nameof(DataQuality.TotalReadings),
            nameof(DataQuality.MissingReadings),
            nameof(DataQuality.DataCompleteness),
            nameof(DataQuality.CgmActivePercent),
            nameof(DataQuality.GapAnalysis),
            nameof(DataQuality.NoiseLevel),
            nameof(DataQuality.CalibrationEvents),
            nameof(DataQuality.SensorWarmups),
        ]);
    }

    [Fact]
    public void PeriodStatistics_CarriesOnlyThePropertiesTheGuardCopies()
    {
        PropertyNames<PeriodStatistics>().Should().BeEquivalentTo(
        [
            nameof(PeriodStatistics.PeriodDays),
            nameof(PeriodStatistics.StartDate),
            nameof(PeriodStatistics.EndDate),
            nameof(PeriodStatistics.Analytics),
            nameof(PeriodStatistics.TreatmentSummary),
            nameof(PeriodStatistics.InsulinDelivery),
            nameof(PeriodStatistics.HasSufficientData),
            nameof(PeriodStatistics.Gmi),
            nameof(PeriodStatistics.Reliability),
            nameof(PeriodStatistics.EntryCount),
            nameof(PeriodStatistics.TreatmentCount),
        ]);
    }

    private static IEnumerable<string> PropertyNames<T>() => typeof(T).GetProperties().Select(p => p.Name);

    [Fact]
    public void MultiPeriod_DevicesGrant_KeepsSensorWarmupsAndNarrowsTreatments()
    {
        var cached = AllPeriodsWithTreatments();

        var result = StatisticsReadScopeGuard.Redact(cached, Granted(Scope.GlucoseRead, Scope.DevicesRead));

        Periods(result).Should().OnlyContain(p =>
            p.Analytics!.DataQuality.SensorWarmups == 3
            && p.TreatmentSummary == null && p.InsulinDelivery == null && p.TreatmentCount == 0);
    }

    [Fact]
    public void MultiPeriod_TreatmentsGrantWithoutDevices_WithholdsOnlySensorWarmups()
    {
        var cached = AllPeriodsWithTreatments();

        var result = StatisticsReadScopeGuard.Redact(cached, Granted(Scope.GlucoseRead, Scope.TreatmentsRead));

        Periods(result).Should().OnlyContain(p =>
            p.Analytics!.DataQuality.SensorWarmups == 0
            && p.TreatmentSummary != null && p.InsulinDelivery != null && p.TreatmentCount == 2);
    }

    [Fact]
    public void MultiPeriod_BothGrants_ReturnsTheCachedInstance()
    {
        var cached = AllPeriodsWithTreatments();

        var result = StatisticsReadScopeGuard.Redact(
            cached, Granted(Scope.GlucoseRead, Scope.TreatmentsRead, Scope.DevicesRead));

        result.Should().BeSameAs(cached);
    }

    [Fact]
    public void MultiPeriod_APeriodWithoutAnalytics_StaysWithoutAnalytics()
    {
        var cached = new MultiPeriodStatistics
        {
            LastDay = new PeriodStatistics { PeriodDays = 1, Analytics = null },
            Last3Days = new PeriodStatistics { PeriodDays = 3 },
            LastWeek = new PeriodStatistics { PeriodDays = 7 },
            LastMonth = new PeriodStatistics { PeriodDays = 30 },
            Last90Days = new PeriodStatistics { PeriodDays = 90 },
        };

        var redacted = StatisticsReadScopeGuard.Redact(cached, Granted(Scope.GlucoseRead));

        redacted.LastDay.Analytics.Should().BeNull();
    }

    // ── AID system metrics ──────────────────────────────────────────────────────────────────

    private static AidSystemMetrics EveryAidFigure() => new()
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
        TimeZoneUnavailableReason = TimeZoneUnavailableReason.NotConfigured,
    };

    [Fact]
    public void AidSystemMetrics_EveryCategory_KeepsEveryFigure()
    {
        var redacted = AidSystemMetricsReadScopeGuard.Redact(
            EveryAidFigure(),
            Granted(Scope.GlucoseRead, Scope.TreatmentsRead, Scope.DevicesRead, Scope.TherapyRead));

        redacted.Should().BeEquivalentTo(EveryAidFigure());
    }

    [Fact]
    public void AidSystemMetrics_WithholdingEveryCategory_LeavesNothingButTheAllowlist()
    {
        RedactionCoverage.AssertWithheld(
            AidSystemMetricsReadScopeGuard.Redact(RedactionCoverage.Populated<AidSystemMetrics>(), Granted()),
            [typeof(AidSystemMetrics)],
            ["AidSystemMetrics.TimeZoneUnavailableReason"]);
    }

    [Theory]
    [InlineData(Scope.GlucoseRead)]
    [InlineData(Scope.TreatmentsRead)]
    [InlineData(Scope.DevicesRead)]
    [InlineData(Scope.TherapyRead)]
    public void AidSystemMetrics_WithoutOneCategory_WithholdsOnlyTheFiguresDrawnFromIt(string missing)
    {
        string[] every = [Scope.GlucoseRead, Scope.TreatmentsRead, Scope.DevicesRead, Scope.TherapyRead];

        var redacted = AidSystemMetricsReadScopeGuard.Redact(
            EveryAidFigure(), Granted([.. every.Where(s => s != missing)]));

        var loopActivity = missing is Scope.TreatmentsRead or Scope.DevicesRead;
        (redacted.CgmActivePercent is null).Should().Be(missing == Scope.GlucoseRead);
        (redacted.AidActivePercent is null).Should().Be(loopActivity);
        (redacted.PumpUsePercent is null).Should().Be(loopActivity);
        (redacted.Segments.Count == 0).Should().Be(loopActivity);
        (redacted.TargetLow is null && redacted.TargetHigh is null).Should().Be(missing == Scope.TherapyRead);
        (redacted.SiteChangeCount is null && redacted.CgmDeviceNames is null && redacted.PumpDeviceNames is null)
            .Should().Be(missing == Scope.DevicesRead);
    }

    // ── Loop Analysis ───────────────────────────────────────────────────────────────────────

    private static readonly string[] AllLoopAnalysisCategories =
        [Scope.GlucoseRead, Scope.TreatmentsRead, Scope.TherapyRead, Scope.DevicesRead];

    private static LoopAnalysisAggregate Aggregate(double value) => new() { P50 = value, Mean = value, N = 1 };

    private static DailyProfileRange DailyProfileRange() => new()
    {
        Slots =
        [
            new DailyProfileSlot
            {
                Glucose = Aggregate(120),
                ScheduledBasal = Aggregate(1),
                DeliveredBasal = Aggregate(1.5),
                NetLoopCorrection = new NetLoopCorrectionAggregate { Mean = 0.5, Rate = Aggregate(0.5) },
                ManualTemp = Aggregate(0),
                Suspend = Aggregate(0),
                Inferred = Aggregate(0),
                Iob = Aggregate(2),
                IobSource = IobCobSource.Device,
                Cob = Aggregate(10),
                CobSource = IobCobSource.Calculated,
                ManualBoluses = new DailyProfileEventSeries { MeanPerDay = 1, N = 1 },
                Carbs = new DailyProfileEventSeries { MeanPerDay = 10, N = 1 },
                TargetLow = 90,
                TargetHigh = 140,
            },
        ],
        HourlyNetLoopCorrection = [new LoopAnalysisHourlyValue { Hour = 0, Mean = 0.5, N = 1 }],
        Days =
        [
            new DailyProfileDay
            {
                Date = "2024-06-03",
                Coverage = new DailyProfileDayCoverage
                {
                    Analysed = "a", Glucose = "g", ScheduledBasal = "s", DeliveredBasal = "d",
                    NetLoopCorrection = "n", IobCob = "i",
                },
                NetLoopCorrection = 12,
                TotalInsulin = 40,
                DeliveredBasal = 20,
                AutomaticBolus = 5,
                ManualBolus = 15,
            },
        ],
        Summary = new DailyProfileSummary
        {
            DaysWithData = 1,
            DeliveredToScheduledRatio = 1.2,
            NetLoopCorrectionPerDay = 12,
            NetLoopCorrectionTddPercent = 30,
            NetLoopCorrectionDays = 1,
            Insulin = new DailyInsulinSummary { Total = Aggregate(40), DeliveredBasal = 20 },
        },
    };

    private static DailyProfileResponse DailyProfile() => new()
    {
        Primary = DailyProfileRange(),
        Comparison = DailyProfileRange(),
        ProfileNames = ["Default"],
    };

    private static readonly LoopAnalysisSeries[] TreatmentSeries =
    [
        LoopAnalysisSeries.DeliveredBasal, LoopAnalysisSeries.NetLoopCorrection,
        LoopAnalysisSeries.NetLoopCorrectionComponents, LoopAnalysisSeries.HourlyNetLoopCorrection,
        LoopAnalysisSeries.Iob, LoopAnalysisSeries.Cob, LoopAnalysisSeries.ManualBoluses, LoopAnalysisSeries.Carbs,
        LoopAnalysisSeries.InsulinSummary, LoopAnalysisSeries.NetLoopCorrectionSummary,
    ];

    private static readonly LoopAnalysisSeries[] ScheduleSeries =
        [LoopAnalysisSeries.ScheduledBasal, LoopAnalysisSeries.Target, LoopAnalysisSeries.DeliveredToScheduledRatio];

    public static TheoryData<string[], LoopAnalysisSeries[]> LoopAnalysisScopeCombinations => new()
    {
        { AllLoopAnalysisCategories, [] },
        { [Scope.GlucoseRead], [.. TreatmentSeries, .. ScheduleSeries, LoopAnalysisSeries.ProfileNames] },
        { [Scope.GlucoseRead, Scope.TreatmentsRead], [.. ScheduleSeries, LoopAnalysisSeries.ProfileNames] },
        { [Scope.GlucoseRead, Scope.TherapyRead], [.. TreatmentSeries, .. ScheduleSeries] },
        { [Scope.TreatmentsRead, Scope.TherapyRead], [LoopAnalysisSeries.Glucose] },
        { [], [LoopAnalysisSeries.Glucose, .. TreatmentSeries, .. ScheduleSeries, LoopAnalysisSeries.ProfileNames] },
    };

    [Theory]
    [MemberData(nameof(LoopAnalysisScopeCombinations))]
    public void LoopAnalysis_WithheldSeries_ListExactlyWhatIsEmptied(string[] scopes, LoopAnalysisSeries[] expected)
    {
        var data = LoopAnalysisReadScopeGuard.Redact(DailyProfile(), Granted(scopes));

        data.WithheldSeries.Should().BeEquivalentTo(expected);
        (data.ProfileNames.Count == 0).Should().Be(expected.Contains(LoopAnalysisSeries.ProfileNames));
        foreach (var range in new[] { data.Primary, data.Comparison! })
        {
            var slot = range.Slots.Single();
            var day = range.Days.Single();
            bool Withheld(LoopAnalysisSeries series) => expected.Contains(series);

            (slot.Glucose is null).Should().Be(Withheld(LoopAnalysisSeries.Glucose));
            (day.Coverage.Glucose is null).Should().Be(Withheld(LoopAnalysisSeries.Glucose));
            (slot.ScheduledBasal is null).Should().Be(Withheld(LoopAnalysisSeries.ScheduledBasal));
            (day.Coverage.ScheduledBasal is null).Should().Be(Withheld(LoopAnalysisSeries.ScheduledBasal));
            (slot.DeliveredBasal is null).Should().Be(Withheld(LoopAnalysisSeries.DeliveredBasal));
            (slot.NetLoopCorrection is null).Should().Be(Withheld(LoopAnalysisSeries.NetLoopCorrection));
            (slot.Inferred is null).Should().Be(Withheld(LoopAnalysisSeries.NetLoopCorrectionComponents));
            (range.HourlyNetLoopCorrection.Count == 0).Should().Be(Withheld(LoopAnalysisSeries.HourlyNetLoopCorrection));
            (slot.Iob is null).Should().Be(Withheld(LoopAnalysisSeries.Iob));
            (slot.Cob is null).Should().Be(Withheld(LoopAnalysisSeries.Cob));
            (slot.ManualBoluses is null).Should().Be(Withheld(LoopAnalysisSeries.ManualBoluses));
            (slot.Carbs is null).Should().Be(Withheld(LoopAnalysisSeries.Carbs));
            (slot.TargetLow is null).Should().Be(Withheld(LoopAnalysisSeries.Target));
            (range.Summary.Insulin is null).Should().Be(Withheld(LoopAnalysisSeries.InsulinSummary));
            (day.TotalInsulin is null).Should().Be(Withheld(LoopAnalysisSeries.InsulinSummary));
            (range.Summary.NetLoopCorrectionPerDay is null).Should().Be(Withheld(LoopAnalysisSeries.NetLoopCorrectionSummary));
            (range.Summary.DeliveredToScheduledRatio is null).Should().Be(Withheld(LoopAnalysisSeries.DeliveredToScheduledRatio));
            day.Coverage.Analysed.Should().Be("a", "the masks a caller cannot read are dropped from its filter instead");
        }
    }

    [Fact]
    public void LoopAnalysis_WithholdingEveryCategory_LeavesNothingButTheAllowlist()
    {
        var data = LoopAnalysisReadScopeGuard.Redact(RedactionCoverage.Populated<DailyProfileResponse>(), Granted());

        RedactionCoverage.AssertWithheld(
            data,
            [
                typeof(DailyProfileRange), typeof(DailyProfileSlot), typeof(DailyProfileDay),
                typeof(DailyProfileDayCoverage), typeof(DailyProfileSummary),
            ],
            [
                "DailyProfileRange.From", "DailyProfileRange.To", "DailyProfileRange.SlotMinutes",
                "DailyProfileRange.Slots", "DailyProfileRange.Days", "DailyProfileRange.Summary",
                "DailyProfileSlot.Slot", "DailyProfileSlot.MinuteOfDay",
                "DailyProfileDay.Date", "DailyProfileDay.DayOfWeek", "DailyProfileDay.Start", "DailyProfileDay.Complete",
                "DailyProfileDay.Coverage",
                "DailyProfileDayCoverage.Analysed",
                "DailyProfileSummary.DaysWithData",
            ]);
    }

    [Theory]
    [InlineData(new[] { Scope.GlucoseRead }, 1)]
    [InlineData(new[] { Scope.GlucoseRead, Scope.TreatmentsRead }, 2)]
    [InlineData(new[] { Scope.TreatmentsRead }, 1)]
    public void LoopAnalysis_DaysWithData_CountsOnlyTheDataTheCallerCanRead(string[] scopes, int expected)
    {
        var data = DailyProfile();
        var glucoseDay = data.Primary.Days.Single();
        glucoseDay.HasGlucose = true;
        data.Primary.Days.Add(new DailyProfileDay { Date = "2024-06-04", HasTreatments = true });
        data.Primary.Summary.DaysWithData = 2;

        LoopAnalysisReadScopeGuard.Redact(data, Granted([.. scopes, Scope.TherapyRead]))
            .Primary.Summary.DaysWithData.Should().Be(expected);
    }

    [Fact]
    public void LoopAnalysis_EverySeriesCanBeWithheld_ByExactlyOneTabOrTheEnvelope()
    {
        var byOwner = LoopAnalysisReadScopeGuard.PageResponses
            .ToDictionary(t => t.Name, t => LoopAnalysisReadScopeGuard.WithheldFrom(t, Granted()));
        byOwner["envelope"] = LoopAnalysisReadScopeGuard.WithheldEnvelope(Granted());

        foreach (var series in Enum.GetValues<LoopAnalysisSeries>())
            byOwner.Where(t => t.Value.Contains(series)).Select(t => t.Key)
                .Should().ContainSingle($"{series} is withheld by exactly one page or the envelope");
    }

    public static TheoryData<Type> LoopAnalysisPageResponses => [.. LoopAnalysisReadScopeGuard.PageResponses];

    [Theory]
    [MemberData(nameof(LoopAnalysisPageResponses))]
    public void LoopAnalysis_WithholdingEveryCategory_LeavesTheEnvelopeOnlyWhatItSaysOfTheRequest(Type response)
    {
        var populated = typeof(RedactionCoverage).GetMethod(nameof(RedactionCoverage.Populated))!
            .MakeGenericMethod(response).Invoke(null, null)!;
        var redacted = typeof(LoopAnalysisReadScopeGuard).GetMethod(nameof(LoopAnalysisReadScopeGuard.Redact))!
            .MakeGenericMethod(response).Invoke(null, [populated, Granted()])!;

        RedactionCoverage.AssertWithheld(
            redacted,
            [response],
            [
                .. new[]
                {
                    "Primary", "Comparison", "HistoryClamped", "TimeZoneUnavailableReason", "PrimaryRange",
                    "ComparisonRange", "AppliedFilters", "IgnoredFilters", "WithheldSeries", "UnavailableFilters",
                    "Definition", "SlotMinutes", "BackToStartMgdl", "UnavailableAnchors", "WithheldReason", "Rules",
                    "HistoryWindowHours", "Limits", "LoopGapMinutes", "ContextLookbackMs",
                }.Select(p => $"{response.Name}.{p}"),
            ]);
    }

    [Fact]
    public void LoopAnalysis_EveryModelEnumGoesOutByName()
    {
        var enums = typeof(LoopAnalysisResponse).Assembly.GetTypes()
            .Where(t => t.IsEnum && t.Namespace == typeof(LoopAnalysisResponse).Namespace)
            .ToList();

        enums.Should().NotBeEmpty();
        foreach (var type in enums)
            type.GetCustomAttribute<System.Text.Json.Serialization.JsonConverterAttribute>()?.ConverterType
                .Should().Be(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<>).MakeGenericType(type),
                    $"{type.Name} is read by name on the wire");
    }

    [Fact]
    public void LoopAnalysis_EveryRouteIsARegisteredTab()
    {
        var responses = typeof(LoopAnalysisController).GetMethods()
            .Where(m => m.GetCustomAttribute<HttpGetAttribute>() is not null)
            .Select(m => m.ReturnType.GetGenericArguments().Single().GetGenericArguments().Single());

        LoopAnalysisReadScopeGuard.PageResponses.Should().BeEquivalentTo(responses);
    }

    [Fact]
    public void LoopAnalysis_FullAccess_KeepsEverything()
    {
        var original = DailyProfile();
        var data = LoopAnalysisReadScopeGuard.Redact(original, Granted(AllLoopAnalysisCategories));

        data.Should().BeSameAs(original);
        data.WithheldSeries.Should().BeEmpty();
        data.Primary.Slots.Single().ScheduledBasal.Should().NotBeNull();
    }

    private static LoopAnalysisMasks EveryMask() => new()
    {
        ProfileName = "Default",
        ExcludeOverrides = true,
        ExcludeTemporaryTargets = true,
        ExcludeExercise = true,
        ExcludeIllness = true,
        ExcludeOpenLoop = true,
    };

    [Fact]
    public void LoopAnalysis_Filter_WithoutTherapy_IgnoresTheProfileName()
    {
        var masks = EveryMask();

        var ignored = LoopAnalysisReadScopeGuard.NarrowFilter(
            masks, Granted(Scope.GlucoseRead, Scope.TreatmentsRead, Scope.DevicesRead));

        ignored.Should().Equal("profileName");
        masks.ProfileName.Should().BeNull();
        masks.ExcludeOverrides.Should().BeTrue();
    }

    [Fact]
    public void LoopAnalysis_Filter_WithoutTreatments_IgnoresTheTreatmentMasks()
    {
        var masks = EveryMask();

        var ignored = LoopAnalysisReadScopeGuard.NarrowFilter(
            masks, Granted(Scope.GlucoseRead, Scope.TherapyRead, Scope.DevicesRead));

        ignored.Should().Equal("excludeOverrides", "excludeTemporaryTargets", "excludeExercise", "excludeIllness");
        masks.ExcludeOverrides.Should().BeFalse();
        masks.ExcludeIllness.Should().BeFalse();
        masks.ExcludeOpenLoop.Should().BeTrue();
        masks.ProfileName.Should().Be("Default");
    }

    [Fact]
    public void LoopAnalysis_Filter_WithoutDevices_IgnoresTheOpenLoopMask()
    {
        var masks = EveryMask();

        var ignored = LoopAnalysisReadScopeGuard.NarrowFilter(
            masks, Granted(Scope.GlucoseRead, Scope.TreatmentsRead, Scope.TherapyRead));

        ignored.Should().Equal("excludeOpenLoop");
        masks.ExcludeOpenLoop.Should().BeFalse();
    }

    [Fact]
    public void LoopAnalysis_Filter_WithEveryCategory_IgnoresNothing()
    {
        var masks = EveryMask();

        LoopAnalysisReadScopeGuard.NarrowFilter(masks, Granted(AllLoopAnalysisCategories)).Should().BeEmpty();
        masks.ProfileName.Should().Be("Default");
    }

    public static TheoryData<string[], string[]> LoopAnalysisUnavailableCombinations => new()
    {
        { AllLoopAnalysisCategories, [] },
        { [Scope.GlucoseRead, Scope.TreatmentsRead, Scope.DevicesRead], ["profileName"] },
        { [Scope.GlucoseRead, Scope.TreatmentsRead, Scope.TherapyRead], ["excludeOpenLoop"] },
        {
            [Scope.GlucoseRead],
            ["profileName", "excludeOverrides", "excludeTemporaryTargets", "excludeExercise", "excludeIllness", "excludeOpenLoop"]
        },
    };

    [Theory]
    [MemberData(nameof(LoopAnalysisUnavailableCombinations))]
    public void LoopAnalysis_UnavailableFilters_AreExactlyTheOnesNarrowingDrops(string[] scopes, string[] expected)
    {
        var granted = Granted(scopes);

        var unavailable = LoopAnalysisReadScopeGuard.UnavailableFilters(granted);

        unavailable.Select(f => f.Filter).Should().Equal(expected);
        LoopAnalysisReadScopeGuard.NarrowFilter(EveryMask(), granted).Should().Equal(expected);
    }

    [Fact]
    public void LoopAnalysis_Filter_WithNoCategory_LeavesNoMaskSet()
    {
        var masks = RedactionCoverage.Populated<LoopAnalysisMasks>();

        LoopAnalysisReadScopeGuard.NarrowFilter(masks, Granted());

        RedactionCoverage.AssertWithheld(masks, [typeof(LoopAnalysisMasks)], ["LoopAnalysisMasks.Days"]);
    }

    [Fact]
    public void LoopAnalysis_FilterNames_AreTheQueryParametersTheBinderReads()
    {
        var parameters = typeof(LoopAnalysisQuery).GetProperties()
            .Select(p => p.GetCustomAttribute<FromQueryAttribute>()?.Name)
            .OfType<string>()
            .ToHashSet();

        LoopAnalysisReadScopeGuard.FilterNames.Should().OnlyContain(
            name => parameters.Contains(name), "the response names filters by the parameter a client sends");
    }

    [Fact]
    public void LoopAnalysis_UnavailableFilters_NameTheScopeEachNeeds()
    {
        LoopAnalysisReadScopeGuard.UnavailableFilters(Granted())
            .ToDictionary(f => f.Filter, f => f.RequiredScope)
            .Should().BeEquivalentTo(new Dictionary<string, string>
            {
                ["profileName"] = Scope.TherapyRead,
                ["excludeOverrides"] = Scope.TreatmentsRead,
                ["excludeTemporaryTargets"] = Scope.TreatmentsRead,
                ["excludeExercise"] = Scope.TreatmentsRead,
                ["excludeIllness"] = Scope.TreatmentsRead,
                ["excludeOpenLoop"] = Scope.DevicesRead,
            });
    }

    [Fact]
    public void LoopAnalysis_GateIsReportsRead()
    {
        var gate = GateOn<LoopAnalysisController>(nameof(LoopAnalysisController.GetDailyProfile));

        gate!.RequiredScopes.Should().BeEquivalentTo([LoopAnalysisReadScopeGuard.AdmissionScope]);
        LoopAnalysisReadScopeGuard.AdmissionScope.Should().Be(Scope.ReportsRead);
    }

    // Noon in Sydney (UTC+10, no DST in June).
    private static readonly DateTime LoopAnalysisNow = new(2024, 6, 8, 2, 0, 0, DateTimeKind.Utc);

    private static (LoopAnalysisController Controller, Mock<ILoopAnalysisService> Service) LoopAnalysisHandler(
        IReadOnlySet<string> scopes, bool historyClamped = false, string query = "", string? timezone = "Australia/Sydney")
    {
        var service = new Mock<ILoopAnalysisService>();
        service
            .Setup(s => s.GetDailyProfileAsync(It.IsAny<LoopAnalysisFilter>(), It.IsAny<TimeZoneInfo>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DailyProfile);
        service
            .Setup(s => s.GetPredictionAccuracyAsync(It.IsAny<LoopAnalysisFilter>(), It.IsAny<TimeZoneInfo>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(PredictionAccuracy);
        service
            .Setup(s => s.GetLoopHealthAsync(It.IsAny<LoopAnalysisFilter>(), It.IsAny<TimeZoneInfo>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(LoopHealth);

        var context = ContextWith(scopes);
        context.HttpContext.Request.QueryString = new QueryString(query);
        var readContext = new Nocturne.Infrastructure.Data.Services.CategoryReadContext();
        if (historyClamped)
            readContext.ClampMemberHistory();
        context.HttpContext.RequestServices = new ServiceCollection()
            .AddSingleton<ICategoryReadContext>(readContext)
            .AddMvcCore().Services
            .BuildServiceProvider();

        var settings = new Mock<ITherapySettingsResolver>();
        settings.Setup(s => s.GetTimezoneAsync(null, It.IsAny<CancellationToken>())).ReturnsAsync(timezone);
        var clock = new TenantClockResolver(settings.Object, readContext, NullLogger<TenantClockResolver>.Instance);

        var controller = new LoopAnalysisController(
            service.Object, clock, new Microsoft.Extensions.Time.Testing.FakeTimeProvider(new DateTimeOffset(LoopAnalysisNow)))
        {
            ControllerContext = context,
        };
        return (controller, service);
    }

    private static LoopAnalysisQuery Week(Action<LoopAnalysisQuery>? configure = null)
    {
        var query = new LoopAnalysisQuery { StartDate = new DateOnly(2024, 6, 1), EndDate = new DateOnly(2024, 6, 7) };
        configure?.Invoke(query);
        return query;
    }

    private static DailyProfileResponse Ok(ActionResult<DailyProfileResponse> result) =>
        result.Result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeOfType<DailyProfileResponse>().Subject;

    [Fact]
    public async Task LoopAnalysis_Handler_RedactsAndReportsWhatItIgnoredAndApplied()
    {
        var (controller, service) = LoopAnalysisHandler(
            Granted(Scope.ReportsRead, Scope.GlucoseRead), query: "?profileName=Default&ExcludeOverrides=true");

        var data = Ok(await controller.GetDailyProfile(
            Week(q => { q.ProfileName = "Default"; q.ExcludeOverrides = true; })));

        data.Primary.Slots.Single().Glucose.Should().NotBeNull();
        data.Primary.Slots.Single().Iob.Should().BeNull();
        data.WithheldSeries.Should().Contain(LoopAnalysisSeries.Iob).And.NotContain(LoopAnalysisSeries.Glucose);
        data.HistoryClamped.Should().BeFalse();
        data.IgnoredFilters.Should().BeEquivalentTo(
            ["profileName", "excludeOverrides"], "the masks on by default are dropped too, but the caller never sent them");
        data.UnavailableFilters.Select(f => f.Filter).Should().BeEquivalentTo(
            ["profileName", "excludeOverrides", "excludeTemporaryTargets", "excludeExercise", "excludeIllness", "excludeOpenLoop"],
            "unavailability does not depend on what was sent");
        data.AppliedFilters.Should().BeEquivalentTo(new LoopAnalysisMasks
        {
            Days = [.. Enum.GetValues<DayOfWeek>()],
            ExcludeOverrides = false,
            ExcludeIllness = false,
            ExcludeOpenLoop = false,
            ProfileName = null,
        });
        data.PrimaryRange!.StartDate.Should().Be(new DateOnly(2024, 6, 1));
        data.PrimaryRange.From.Should().Be(new DateTime(2024, 5, 31, 14, 0, 0, DateTimeKind.Utc));
        data.PrimaryRange.To.Should().Be(new DateTime(2024, 6, 7, 14, 0, 0, DateTimeKind.Utc));
        service.Verify(s => s.GetDailyProfileAsync(
            It.Is<LoopAnalysisFilter>(f => f.Masks.ProfileName == null && !f.Masks.ExcludeOverrides && f.CompareFrom == null),
            It.Is<TimeZoneInfo>(z => z.Id == "Australia/Sydney"),
            It.IsAny<CancellationToken>()));
    }

    [Theory]
    [InlineData(nameof(LoopAnalysisController.GetDailyProfile), true)]
    [InlineData(nameof(LoopAnalysisController.GetDailyProfile), false)]
    [InlineData(nameof(LoopAnalysisController.GetPredictionAccuracy), true)]
    [InlineData(nameof(LoopAnalysisController.GetPredictionAccuracy), false)]
    [InlineData(nameof(LoopAnalysisController.GetLoopHealth), true)]
    [InlineData(nameof(LoopAnalysisController.GetLoopHealth), false)]
    public async Task LoopAnalysis_EveryTab_CarriesTheProfileNames_OnlyWithTherapy(string action, bool therapy)
    {
        var scopes = therapy ? AllLoopAnalysisCategories : AllLoopAnalysisCategories.Except([Scope.TherapyRead]);
        var (controller, service) = LoopAnalysisHandler(Granted([Scope.ReportsRead, .. scopes]));

        var data = action switch
        {
            nameof(LoopAnalysisController.GetDailyProfile) =>
                (LoopAnalysisResponse)Ok(await controller.GetDailyProfile(Week())),
            nameof(LoopAnalysisController.GetPredictionAccuracy) => Ok(await controller.GetPredictionAccuracy(Week())),
            _ => (await controller.GetLoopHealth(Week())).Result.Should().BeOfType<OkObjectResult>()
                .Which.Value.Should().BeAssignableTo<LoopAnalysisResponse>().Subject,
        };

        data.ProfileNames.Should().Equal(therapy ? ["Default"] : []);
        data.WithheldSeries.Contains(LoopAnalysisSeries.ProfileNames).Should().Be(!therapy);
        var asked = service.Invocations.Single().Arguments.OfType<LoopAnalysisFilter>().Single();
        asked.ReadProfileNames.Should().Be(therapy, "a page that does not load the profiles reads them only for a caller who may see them");
    }

    [Fact]
    public async Task LoopAnalysis_Handler_EchoesTheDefaultsItApplied()
    {
        var (controller, _) = LoopAnalysisHandler(Granted([Scope.ReportsRead, .. AllLoopAnalysisCategories]));

        var data = Ok(await controller.GetDailyProfile(Week(q => q.Days = [DayOfWeek.Friday, DayOfWeek.Monday])));

        data.AppliedFilters.Days.Should().Equal(DayOfWeek.Monday, DayOfWeek.Friday);
        data.AppliedFilters.ExcludeIllness.Should().BeTrue();
        data.AppliedFilters.ExcludeOpenLoop.Should().BeTrue();
        data.AppliedFilters.ExcludeOverrides.Should().BeFalse();
        data.IgnoredFilters.Should().BeEmpty();
        data.UnavailableFilters.Should().BeEmpty();
    }

    [Fact]
    public async Task LoopAnalysis_Handler_TrimsALongRangeAndComparesWithThePeriodBeforeIt()
    {
        var (controller, service) = LoopAnalysisHandler(Granted([Scope.ReportsRead, .. AllLoopAnalysisCategories]));

        var data = Ok(await controller.GetDailyProfile(new LoopAnalysisQuery
        {
            StartDate = new DateOnly(2024, 1, 1), EndDate = new DateOnly(2024, 5, 31), Compare = LoopAnalysisComparison.Previous,
        }));

        data.PrimaryRange!.Shortened.Should().BeTrue();
        data.PrimaryRange.StartDate.Should().Be(new DateOnly(2024, 3, 3));
        data.ComparisonRange!.StartDate.Should().Be(new DateOnly(2023, 12, 4));
        data.ComparisonRange.EndDate.Should().Be(new DateOnly(2024, 3, 2));
        service.Verify(s => s.GetDailyProfileAsync(
            It.Is<LoopAnalysisFilter>(f => f.From == data.PrimaryRange.From && f.CompareTo == data.PrimaryRange.From),
            It.IsAny<TimeZoneInfo>(),
            It.IsAny<CancellationToken>()));
    }

    [Fact]
    public async Task LoopAnalysis_Handler_AnalysesAClampedCallersLastDayWhateverDatesItAskedFor()
    {
        var (controller, service) = LoopAnalysisHandler(
            Granted([Scope.ReportsRead, .. AllLoopAnalysisCategories]), historyClamped: true,
            query: "?startDate=2024-01-01&endDate=2024-01-07&compare=previous");

        var data = Ok(await controller.GetDailyProfile(new LoopAnalysisQuery
        {
            StartDate = new DateOnly(2024, 1, 1), EndDate = new DateOnly(2024, 1, 7), Compare = LoopAnalysisComparison.Previous,
        }));

        data.HistoryClamped.Should().BeTrue();
        data.ComparisonRange.Should().BeNull();
        data.IgnoredFilters.Should().BeEquivalentTo("startDate", "endDate", "compare");
        data.PrimaryRange!.From.Should().Be(LoopAnalysisNow.AddHours(-24));
        data.PrimaryRange.To.Should().Be(LoopAnalysisNow);
        service.Verify(s => s.GetDailyProfileAsync(
            It.Is<LoopAnalysisFilter>(f =>
                f.From == LoopAnalysisNow.AddHours(-24) && f.To == LoopAnalysisNow && f.CompareFrom == null && f.CompareTo == null),
            It.IsAny<TimeZoneInfo>(),
            It.IsAny<CancellationToken>()));
    }

    [Fact]
    public async Task LoopAnalysis_Handler_WithoutATimezone_ResolvesNothingAndSaysWhy()
    {
        var (controller, service) = LoopAnalysisHandler(
            Granted([Scope.ReportsRead, .. AllLoopAnalysisCategories]), timezone: null);

        var data = Ok(await controller.GetDailyProfile(Week()));

        data.TimeZoneUnavailableReason.Should().Be(TimeZoneUnavailableReason.NotConfigured);
        data.PrimaryRange.Should().BeNull();
        data.Primary.Slots.Should().BeEmpty();
        service.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("2024-06-07", "2024-06-01", LoopAnalysisComparison.None, null, null)]
    [InlineData("2024-06-01", "2024-06-07", LoopAnalysisComparison.Custom, null, null)]
    [InlineData("2024-06-01", "2024-06-07", LoopAnalysisComparison.Previous, "2024-05-01", "2024-05-07")]
    [InlineData("2024-06-01", "2024-06-07", LoopAnalysisComparison.Custom, "2024-05-07", "2024-05-01")]
    public async Task LoopAnalysis_Handler_RejectsDatesThatDoNotMakeARange(
        string start, string end, LoopAnalysisComparison compare, string? compareStart, string? compareEnd)
    {
        var (controller, service) = LoopAnalysisHandler(Granted([Scope.ReportsRead, .. AllLoopAnalysisCategories]));

        var result = await controller.GetDailyProfile(new LoopAnalysisQuery
        {
            StartDate = DateOnly.Parse(start),
            EndDate = DateOnly.Parse(end),
            Compare = compare,
            CompareStartDate = compareStart is null ? null : DateOnly.Parse(compareStart),
            CompareEndDate = compareEnd is null ? null : DateOnly.Parse(compareEnd),
        });

        result.Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(400);
        service.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task LoopAnalysis_Handler_ReadsDatesAfterTodayAsTodaySoFar()
    {
        var (controller, service) = LoopAnalysisHandler(Granted([Scope.ReportsRead, .. AllLoopAnalysisCategories]));

        var data = Ok(await controller.GetDailyProfile(new LoopAnalysisQuery
        {
            StartDate = new DateOnly(2024, 6, 9),
            EndDate = new DateOnly(2024, 6, 15),
            Compare = LoopAnalysisComparison.Custom,
            CompareStartDate = new DateOnly(2024, 6, 9),
            CompareEndDate = new DateOnly(2024, 6, 15),
        }));

        var startOfToday = new DateTime(2024, 6, 7, 14, 0, 0, DateTimeKind.Utc);
        foreach (var range in new[] { data.PrimaryRange!, data.ComparisonRange! })
        {
            range.StartDate.Should().Be(new DateOnly(2024, 6, 8));
            range.EndDate.Should().Be(new DateOnly(2024, 6, 8));
            range.From.Should().Be(startOfToday);
            range.To.Should().Be(LoopAnalysisNow);
        }
        service.Verify(s => s.GetDailyProfileAsync(
            It.Is<LoopAnalysisFilter>(f => f.From == startOfToday && f.To == LoopAnalysisNow
                && f.CompareFrom == startOfToday && f.CompareTo == LoopAnalysisNow),
            It.IsAny<TimeZoneInfo>(),
            It.IsAny<CancellationToken>()));
    }

    [Fact]
    public async Task LoopAnalysis_Handler_EndsTheRangeAtNow_AndComparesTheSameTimeOfTheEarlierDays()
    {
        var (controller, service) = LoopAnalysisHandler(Granted([Scope.ReportsRead, .. AllLoopAnalysisCategories]));

        var data = Ok(await controller.GetDailyProfile(new LoopAnalysisQuery
        {
            StartDate = new DateOnly(2024, 6, 2),
            EndDate = new DateOnly(2024, 6, 14),
            Compare = LoopAnalysisComparison.Previous,
        }));

        data.PrimaryRange!.EndDate.Should().Be(new DateOnly(2024, 6, 8));
        data.PrimaryRange.From.Should().Be(new DateTime(2024, 6, 1, 14, 0, 0, DateTimeKind.Utc));
        data.PrimaryRange.To.Should().Be(LoopAnalysisNow);
        data.ComparisonRange!.StartDate.Should().Be(new DateOnly(2024, 5, 26));
        data.ComparisonRange.EndDate.Should().Be(new DateOnly(2024, 6, 1));
        data.ComparisonRange.To.Should().Be(new DateTime(2024, 6, 1, 2, 0, 0, DateTimeKind.Utc), "noon on 1 June, local");
        (data.ComparisonRange.To - data.ComparisonRange.From).Should().Be(data.PrimaryRange.To - data.PrimaryRange.From);
        service.Verify(s => s.GetDailyProfileAsync(
            It.Is<LoopAnalysisFilter>(f => f.To == LoopAnalysisNow && f.CompareTo == data.ComparisonRange.To),
            It.IsAny<TimeZoneInfo>(),
            It.IsAny<CancellationToken>()));
    }

    // ── Loop Analysis predictions ───────────────────────────────────────────────────────────

    private static PredictionHorizonAccuracy[] Horizons() =>
        [new PredictionHorizonAccuracy { Minutes = 30, Error = Aggregate(5), Mae = 5, Mard = 4, SettingsSignal = true }];

    private static PredictionCurveAccuracy Curve(PredictionCurve curve, params PredictionContext[] contexts) => new()
    {
        Curve = curve,
        Contexts = [.. contexts],
        Horizons = [.. Horizons()],
        ByHour = [new PredictionHourAccuracy { Hour = 0, Horizons = [.. Horizons()] }],
        ByContext = [.. contexts.Select(c => new PredictionContextAccuracy { Context = c, Horizons = [.. Horizons()] })],
    };

    private static PredictionAccuracyRange PredictionAccuracyRange() => new()
    {
        Predictions = 2,
        Algorithms = [AidAlgorithm.Loop, AidAlgorithm.AndroidAps],
        Curves =
        [
            Curve(PredictionCurve.Default, Enum.GetValues<PredictionContext>()),
            Curve(PredictionCurve.Iob, PredictionContext.PostCorrection, PredictionContext.Fasting),
            Curve(PredictionCurve.Cob, PredictionContext.PostMeal),
            Curve(PredictionCurve.Uam, Enum.GetValues<PredictionContext>()),
        ],
    };

    private static PredictionAccuracyResponse PredictionAccuracy() => new()
    {
        Primary = PredictionAccuracyRange(),
        Comparison = PredictionAccuracyRange(),
        ProfileNames = ["Default"],
    };

    [Fact]
    public void LoopAnalysis_PredictionAccuracy_IsGatedOnReportsRead()
    {
        var gate = GateOn<LoopAnalysisController>(nameof(LoopAnalysisController.GetPredictionAccuracy));

        gate!.RequiredScopes.Should().BeEquivalentTo([LoopAnalysisReadScopeGuard.AdmissionScope]);
    }

    [Fact]
    public void LoopAnalysis_Predictions_WithNoCategory_WithholdEveryField()
    {
        var data = LoopAnalysisReadScopeGuard.Redact(RedactionCoverage.Populated<PredictionAccuracyResponse>(), Granted());

        RedactionCoverage.AssertWithheld(
            data,
            [typeof(PredictionAccuracyRange)],
            ["PredictionAccuracyRange.From", "PredictionAccuracyRange.To"]);
    }

    [Fact]
    public void LoopAnalysis_Predictions_WithoutTreatments_WithholdEveryContextField()
    {
        var data = RedactionCoverage.Populated<PredictionAccuracyResponse>();
        foreach (var curve in data.Primary.Curves.Concat(data.Comparison!.Curves))
            curve.Contexts = [.. Enum.GetValues<PredictionContext>()];

        RedactionCoverage.AssertWithheld(
            LoopAnalysisReadScopeGuard.Redact(data, Granted(Scope.GlucoseRead, Scope.DevicesRead)),
            [typeof(PredictionCurveAccuracy), typeof(PredictionHourAccuracy), typeof(PredictionHorizonAccuracy)],
            [
                "PredictionCurveAccuracy.Curve", "PredictionCurveAccuracy.Contexts",
                "PredictionCurveAccuracy.Horizons", "PredictionCurveAccuracy.ByHour",
                "PredictionHourAccuracy.Hour", "PredictionHourAccuracy.Horizons",
                "PredictionHorizonAccuracy.Minutes", "PredictionHorizonAccuracy.Error",
                "PredictionHorizonAccuracy.Mae", "PredictionHorizonAccuracy.Mard",
                "PredictionHorizonAccuracy.Unmatched", "PredictionHorizonAccuracy.Excluded",
                "PredictionHorizonAccuracy.SettingsSignal", "PredictionHorizonAccuracy.BiasScale",
            ]);
    }

    [Fact]
    public void LoopAnalysis_Predictions_WithEveryCategory_AreUntouched()
    {
        var data = LoopAnalysisReadScopeGuard.Redact(PredictionAccuracy(), Granted(AllLoopAnalysisCategories));

        data.WithheldSeries.Should().BeEmpty();
        data.Primary.Curves.Should().HaveCount(4);
        data.Primary.Curves.Should().OnlyContain(c => c.ByContext.Count > 0);
    }

    [Theory]
    [InlineData(Scope.TreatmentsRead, Scope.DevicesRead)]
    [InlineData(Scope.GlucoseRead, Scope.TreatmentsRead)]
    public void LoopAnalysis_Predictions_NeedBothTheReadingsAndTheSnapshots(params string[] scopes)
    {
        var data = LoopAnalysisReadScopeGuard.Redact(PredictionAccuracy(), Granted(scopes));

        var devices = scopes.Contains(Scope.DevicesRead);
        data.WithheldSeries.Should().Equal(devices
            ? [LoopAnalysisSeries.ProfileNames, LoopAnalysisSeries.PredictionCurves]
            : [LoopAnalysisSeries.ProfileNames, LoopAnalysisSeries.PredictionCurves, LoopAnalysisSeries.PredictionCount]);
        foreach (var range in new[] { data.Primary, data.Comparison! })
        {
            range.Curves.Should().BeEmpty();
            (range.Predictions == 0).Should().Be(!devices, "the count is of APS snapshots");
            (range.Algorithms.Count == 0).Should().Be(!devices);
        }
    }

    [Fact]
    public void LoopAnalysis_Predictions_WithoutTreatments_NameTheHiddenCurvesWhetherOrNotAnySnapshotCarriedThem()
    {
        // No COB curve was carried: oref uploads it only while carbs are absorbing.
        var data = PredictionAccuracy();
        data.Primary.Curves.RemoveAll(c => c.Curve == PredictionCurve.Cob);

        var redacted = LoopAnalysisReadScopeGuard.Redact(data, Granted(Scope.GlucoseRead, Scope.DevicesRead, Scope.TherapyRead));

        redacted.Primary.HiddenCurves.Should().Equal(
            [PredictionCurve.Iob, PredictionCurve.ZeroTemp, PredictionCurve.Cob], "the list follows the algorithms, not the snapshots");
        redacted.Primary.HiddenCurves.Should().Equal(redacted.Comparison!.HiddenCurves);
    }

    [Fact]
    public void LoopAnalysis_Predictions_WithDevicesAlone_NameNoHiddenCurve()
    {
        var data = LoopAnalysisReadScopeGuard.Redact(PredictionAccuracy(), Granted(Scope.DevicesRead));

        foreach (var range in new[] { data.Primary, data.Comparison! })
        {
            range.Curves.Should().BeEmpty();
            range.HiddenCurves.Should().BeEmpty("every curve is withheld for want of glucose, not only some for treatments");
            range.Algorithms.Should().NotBeEmpty();
        }
    }

    [Fact]
    public void LoopAnalysis_Predictions_ForLoopAlone_HideNoCurveWithoutTreatments()
    {
        var data = PredictionAccuracy();
        data.Primary.Algorithms = [AidAlgorithm.Loop];

        LoopAnalysisReadScopeGuard.Redact(data, Granted(Scope.GlucoseRead, Scope.DevicesRead, Scope.TherapyRead))
            .Primary.HiddenCurves.Should().BeEmpty();
    }

    [Fact]
    public void LoopAnalysis_Predictions_WithoutTreatments_DropTheContextsAndTheCurvesReportedInSome()
    {
        var data = LoopAnalysisReadScopeGuard.Redact(
            PredictionAccuracy(), Granted(Scope.GlucoseRead, Scope.DevicesRead, Scope.TherapyRead));

        data.WithheldSeries.Should().Equal(LoopAnalysisSeries.PredictionContexts);
        foreach (var range in new[] { data.Primary, data.Comparison! })
        {
            range.Curves.Select(c => c.Curve).Should().Equal(
                [PredictionCurve.Default, PredictionCurve.Uam], "a curve reported in every context reads no treatment");
            range.HiddenCurves.Should().Equal(PredictionCurve.Iob, PredictionCurve.ZeroTemp, PredictionCurve.Cob);
            foreach (var curve in range.Curves)
            {
                curve.Horizons.Should().NotBeEmpty();
                curve.ByHour.Should().NotBeEmpty();
                curve.ByContext.Should().BeEmpty();
            }
        }
    }

    private static PredictionAccuracyResponse Ok(ActionResult<PredictionAccuracyResponse> result) =>
        result.Result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeOfType<PredictionAccuracyResponse>().Subject;

    [Fact]
    public async Task LoopAnalysis_PredictionHandler_ResolvesRedactsAndEchoes()
    {
        var (controller, service) = LoopAnalysisHandler(
            Granted(Scope.ReportsRead, Scope.GlucoseRead, Scope.DevicesRead), query: "?excludeExercise=true");

        var data = Ok(await controller.GetPredictionAccuracy(
            Week(q => { q.Compare = LoopAnalysisComparison.Previous; q.ExcludeExercise = true; })));

        data.Primary.Curves.Should().HaveCount(2).And.OnlyContain(c => c.ByContext.Count == 0);
        data.WithheldSeries.Should().Equal(LoopAnalysisSeries.ProfileNames, LoopAnalysisSeries.PredictionContexts);
        data.IgnoredFilters.Should().Equal("excludeExercise");
        data.UnavailableFilters.Should().BeEquivalentTo(new[]
        {
            new LoopAnalysisUnavailableFilter { Filter = "profileName", RequiredScope = Scope.TherapyRead },
            new LoopAnalysisUnavailableFilter { Filter = "excludeOverrides", RequiredScope = Scope.TreatmentsRead },
            new LoopAnalysisUnavailableFilter { Filter = "excludeTemporaryTargets", RequiredScope = Scope.TreatmentsRead },
            new LoopAnalysisUnavailableFilter { Filter = "excludeExercise", RequiredScope = Scope.TreatmentsRead },
            new LoopAnalysisUnavailableFilter { Filter = "excludeIllness", RequiredScope = Scope.TreatmentsRead },
        }, "every filter the caller's scopes would drop, set or not");
        data.AppliedFilters.ExcludeExercise.Should().BeFalse();
        data.ComparisonRange!.EndDate.Should().Be(new DateOnly(2024, 5, 31));
        service.Verify(s => s.GetPredictionAccuracyAsync(
            It.Is<LoopAnalysisFilter>(f => !f.Masks.ExcludeExercise && f.CompareTo == data.PrimaryRange!.From),
            It.Is<TimeZoneInfo>(z => z.Id == "Australia/Sydney"),
            It.IsAny<CancellationToken>()));
    }

    [Fact]
    public async Task LoopAnalysis_PredictionHandler_AnalysesAClampedCallersLastDay()
    {
        var (controller, service) = LoopAnalysisHandler(
            Granted([Scope.ReportsRead, .. AllLoopAnalysisCategories]), historyClamped: true);

        var data = Ok(await controller.GetPredictionAccuracy(Week()));

        data.HistoryClamped.Should().BeTrue();
        service.Verify(s => s.GetPredictionAccuracyAsync(
            It.Is<LoopAnalysisFilter>(f => f.From == LoopAnalysisNow.AddHours(-24) && f.To == LoopAnalysisNow),
            It.IsAny<TimeZoneInfo>(),
            It.IsAny<CancellationToken>()));
    }

    [Fact]
    public async Task LoopAnalysis_PredictionHandler_WithoutATimezone_ComputesNothing()
    {
        var (controller, service) = LoopAnalysisHandler(
            Granted([Scope.ReportsRead, .. AllLoopAnalysisCategories]), timezone: null);

        var data = Ok(await controller.GetPredictionAccuracy(Week()));

        data.TimeZoneUnavailableReason.Should().Be(TimeZoneUnavailableReason.NotConfigured);
        data.Primary.Curves.Should().BeEmpty();
        service.VerifyNoOtherCalls();
    }

    // ── Loop health ─────────────────────────────────────────────────────────────────────────

    private static LoopHealthShare Share(double percent) => new() { Percent = percent, Daily = Aggregate(percent) };

    private static LoopHealthRange LoopHealthRange() => new()
    {
        Days = [new LoopHealthDay { Date = "2024-06-03" }],
        Activity = new LoopHealthActivity
        {
            AidActivePercent = 90, PumpUsePercent = 95, CgmActivePercent = 98,
            ClosedLoop = Share(90), Pump = Share(95), Cgm = Share(98),
        },
        Cadence = new LoopHealthCadence { LoopGaps = new LoopHealthGaps { Count = 1 }, UnrecordedDelivery = new LoopHealthGaps() },
        ZeroDelivery = new LoopHealthZeroDelivery(),
        MaxBasal = new LoopHealthMaxBasal { AtCap = Share(5) },
        AtMaxIob = Share(1),
        AutomaticBoluses = new LoopHealthAutomaticBoluses(),
        SuggestedNotEnacted = Share(0.1),
        SensitivityRatio = new LoopHealthDistribution { Overall = Aggregate(1) },
        Limits = new LoopHealthLimits { LoopMaximumBasalRate = 3, OrefMaxIob = 4 },
    };

    private static LoopHealthResponse LoopHealth() =>
        new() { Primary = LoopHealthRange(), Comparison = LoopHealthRange(), ProfileNames = ["Default"] };

    [Fact]
    public void LoopHealth_WithoutDevices_DropsWhatTheLoopCyclesReport()
    {
        var data = LoopAnalysisReadScopeGuard.Redact(
            LoopHealth(), Granted(Scope.GlucoseRead, Scope.TreatmentsRead, Scope.TherapyRead));

        data.WithheldSeries.Should().BeEquivalentTo([
            LoopAnalysisSeries.LoopActivity, LoopAnalysisSeries.UnrecordedDelivery, LoopAnalysisSeries.LoopGaps,
            LoopAnalysisSeries.MaxBasal, LoopAnalysisSeries.AtMaxIob, LoopAnalysisSeries.SuggestedNotEnacted,
            LoopAnalysisSeries.SensitivityRatio, LoopAnalysisSeries.LoopLimits, LoopAnalysisSeries.OrefMaxIob,
        ]);
        foreach (var range in new[] { data.Primary, data.Comparison! })
        {
            range.Cadence.LoopGaps.Should().BeNull();
            range.Cadence.UnrecordedDelivery.Should().BeNull();
            range.Activity.ClosedLoop.Should().BeNull();
            range.Activity.Pump.Should().BeNull();
            range.Activity.AidActivePercent.Should().BeNull();
            range.Activity.PumpUsePercent.Should().BeNull();
            range.MaxBasal.Should().BeNull();
            range.AtMaxIob.Should().BeNull();
            range.SuggestedNotEnacted.Should().BeNull();
            range.SensitivityRatio.Should().BeNull();
            range.Limits.OrefMaxIob.Should().BeNull();
            range.Limits.LoopMaximumBasalRate.Should().BeNull("a Loop setting shows the system was Loop, which the cycles say");
            range.ZeroDelivery.Should().NotBeNull();
            range.AutomaticBoluses.Should().NotBeNull();
            range.Activity.Cgm.Should().NotBeNull();
        }
    }

    [Fact]
    public void LoopHealth_WithoutTreatments_DropsDeliveryAndBoluses()
    {
        var data = LoopAnalysisReadScopeGuard.Redact(
            LoopHealth(), Granted(Scope.GlucoseRead, Scope.DevicesRead, Scope.TherapyRead));

        data.WithheldSeries.Should().BeEquivalentTo([
            LoopAnalysisSeries.LoopActivity, LoopAnalysisSeries.UnrecordedDelivery, LoopAnalysisSeries.MaxBasal,
            LoopAnalysisSeries.ZeroDelivery, LoopAnalysisSeries.AutomaticBoluses,
        ]);
        data.Primary.ZeroDelivery.Should().BeNull();
        data.Primary.AutomaticBoluses.Should().BeNull();
        data.Primary.Activity.ClosedLoop.Should().BeNull("without cycles it is read off the algorithm's temps");
        data.Primary.Cadence.UnrecordedDelivery.Should().BeNull();
        data.Primary.Cadence.LoopGaps.Should().NotBeNull();
        data.Primary.MaxBasal.Should().BeNull("time at the cap is measured on delivered basal");
        data.Primary.Limits.OrefMaxIob.Should().Be(4);
    }

    [Fact]
    public void LoopHealth_WithoutTherapyOrGlucose_DropsTheLimitsAndTheCgm()
    {
        var data = LoopAnalysisReadScopeGuard.Redact(
            LoopHealth(), Granted(Scope.TreatmentsRead, Scope.DevicesRead));

        data.WithheldSeries.Should().BeEquivalentTo([
            LoopAnalysisSeries.ProfileNames, LoopAnalysisSeries.CgmActivity, LoopAnalysisSeries.LoopLimits, LoopAnalysisSeries.OrefMaxIob,
            LoopAnalysisSeries.MaxBasal,
        ]);
        data.Primary.Limits.Should().BeEquivalentTo(new LoopHealthLimits());
        data.Primary.Activity.Cgm.Should().BeNull();
        data.Primary.Activity.CgmActivePercent.Should().BeNull();
        data.Primary.MaxBasal.Should().BeNull("the hours at Loop's cap show where its setting sits");
    }

    [Fact]
    public void LoopHealth_FullAccess_KeepsEverything()
    {
        var original = LoopHealth();

        var data = LoopAnalysisReadScopeGuard.Redact(original, Granted(AllLoopAnalysisCategories));

        data.Should().BeSameAs(original);
        data.WithheldSeries.Should().BeEmpty();
        data.Primary.MaxBasal.Should().NotBeNull();
    }

    [Fact]
    public void LoopHealth_WithholdingEveryCategory_LeavesNothingButTheDays()
    {
        var data = LoopAnalysisReadScopeGuard.Redact(RedactionCoverage.Populated<LoopHealthResponse>(), Granted());

        RedactionCoverage.AssertWithheld(
            data,
            [
                typeof(LoopHealthRange), typeof(LoopHealthActivity), typeof(LoopHealthCadence), typeof(LoopHealthLimits),
                typeof(LoopHealthUnknown),
            ],
            [
                "LoopHealthRange.From", "LoopHealthRange.To", "LoopHealthRange.Days",
                "LoopHealthRange.Activity", "LoopHealthRange.Cadence", "LoopHealthRange.Limits", "LoopHealthRange.Unknown",
            ]);
    }

    [Fact]
    public void LoopHealth_WithoutDevices_DropsEveryReasonReadOffTheCycles()
    {
        var data = RedactionCoverage.Populated<LoopHealthResponse>();

        var redacted = LoopAnalysisReadScopeGuard.Redact(
            data, Granted(Scope.GlucoseRead, Scope.TreatmentsRead, Scope.TherapyRead));

        redacted.Primary.Unknown.Should().BeEquivalentTo(new LoopHealthUnknown(),
            "which system looped, and whether it looped at all, are read off the APS cycles");
    }

    [Fact]
    public void LoopHealth_WithEveryCategory_KeepsTheReasons()
    {
        var data = LoopAnalysisReadScopeGuard.Redact(
            RedactionCoverage.Populated<LoopHealthResponse>(), Granted(AllLoopAnalysisCategories));

        data.Primary.Unknown.AtMaxIob.Should().NotBeNull();
        data.Primary.Unknown.LoopMaximumBasalRate.Should().NotBeNull();
    }

    [Fact]
    public void LoopHealth_GateIsReportsRead() =>
        GateOn<LoopAnalysisController>(nameof(LoopAnalysisController.GetLoopHealth))!
            .RequiredScopes.Should().BeEquivalentTo([LoopAnalysisReadScopeGuard.AdmissionScope]);

    [Fact]
    public async Task LoopHealth_Handler_RedactsClampsAndReportsTheIgnoredFilters()
    {
        var (controller, service) = LoopAnalysisHandler(
            Granted(Scope.ReportsRead, Scope.GlucoseRead), historyClamped: true, query: "?excludeOpenLoop=true");
        service
            .Setup(s => s.GetLoopHealthAsync(It.IsAny<LoopAnalysisFilter>(), It.IsAny<TimeZoneInfo>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(LoopHealth);

        var result = await controller.GetLoopHealth(Week(q => q.ExcludeOpenLoop = true));

        var data = result.Result.Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeOfType<LoopHealthResponse>().Subject;
        data.HistoryClamped.Should().BeTrue();
        data.IgnoredFilters.Should().Equal("excludeOpenLoop");
        data.WithheldSeries.Should().Contain(LoopAnalysisSeries.LoopGaps);
        data.Primary.Cadence.LoopGaps.Should().BeNull();
        data.Primary.Activity.Cgm.Should().NotBeNull();
        data.PrimaryRange!.From.Should().Be(LoopAnalysisNow.AddHours(-24));
        service.Verify(s => s.GetLoopHealthAsync(
            It.Is<LoopAnalysisFilter>(f => f.From == LoopAnalysisNow.AddHours(-24) && !f.Masks.ExcludeOpenLoop),
            It.IsAny<TimeZoneInfo>(),
            It.IsAny<CancellationToken>()));
    }
}
