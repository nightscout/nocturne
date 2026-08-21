using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Attributes;
using Nocturne.API.Authorization;
using Nocturne.API.Controllers.V4.Analytics;
using Nocturne.API.Services.Glucose;
using Nocturne.Core.Contracts.Analytics;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.V4;
using Nocturne.Core.Models.Widget;
using Xunit;

namespace Nocturne.API.Tests.Authorization;

/// <summary>
/// Admission and redaction for the V4 analytics controllers whose responses merge several data
/// categories. Modelled on <see cref="ActogramReadScopeGuardTests"/>: each guard is exercised
/// directly, each controller attribute is pinned to its guard's admission list, and each handler is
/// driven once to prove it actually calls the guard.
/// </summary>
[Trait("Category", "Unit")]
public class AnalyticsReadScopeGuardTests
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
        DefaultBasalRate = 0.9,
        MaxBasalRate = 1.2,
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
        var data = ChartDataReadScopeGuard.Redact(OneRecordPerCategory(), Granted(OAuthScopes.GlucoseRead));

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
        var data = ChartDataReadScopeGuard.Redact(OneRecordPerCategory(), Granted(OAuthScopes.GlucoseRead));

        data.DefaultBasalRate.Should().Be(0);
        data.MaxBasalRate.Should().Be(0);
        data.MaxIob.Should().Be(0);
        data.MaxCob.Should().Be(0);
    }

    [Fact]
    public void ChartData_WithoutGlucose_ClearsTheThresholds()
    {
        var data = ChartDataReadScopeGuard.Redact(OneRecordPerCategory(), Granted(OAuthScopes.DevicesRead));

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
            OneRecordPerCategory(), Granted(OAuthScopes.TreatmentsRead));
        treatments.ActivitySpans.Should().ContainSingle().Which.Id.Should().Be("ex");

        var sleep = ChartDataReadScopeGuard.Redact(
            OneRecordPerCategory(), Granted(OAuthScopes.SleepRead));
        sleep.ActivitySpans.Should().ContainSingle().Which.Id.Should().Be("sl");
    }

    [Fact]
    public void ChartData_TherapyOnlyGrant_KeepsOnlyTheProfileSwitches()
    {
        var data = ChartDataReadScopeGuard.Redact(OneRecordPerCategory(), Granted(OAuthScopes.TherapyRead));

        data.ProfileSpans.Should().HaveCount(1);
        data.GlucoseData.Should().BeEmpty();
        data.PumpModeSpans.Should().BeEmpty();
        data.OverrideSpans.Should().BeEmpty();
    }

    [Fact]
    public void ChartData_FullAccess_KeepsEveryCategory()
    {
        var data = ChartDataReadScopeGuard.Redact(OneRecordPerCategory(), Granted(OAuthScopes.FullAccess));

        data.GlucoseData.Should().HaveCount(1);
        data.IobSeries.Should().HaveCount(1);
        data.DeviceEventMarkers.Should().HaveCount(1);
        data.ProfileSpans.Should().HaveCount(1);
        data.HeartRateSeries.Should().HaveCount(1);
        data.StepSeries.Should().HaveCount(1);
        data.ActivitySpans.Should().HaveCount(2);
        data.MaxIob.Should().Be(4);
    }

    [Fact]
    public void ChartData_ReadWriteGrant_SatisfiesTheReadCategory()
    {
        var data = ChartDataReadScopeGuard.Redact(OneRecordPerCategory(), Granted(OAuthScopes.HeartRateReadWrite));

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
            OAuthScopes.GlucoseRead,
            OAuthScopes.TreatmentsRead,
            OAuthScopes.DevicesRead,
            OAuthScopes.TherapyRead,
            OAuthScopes.HeartRateRead,
            OAuthScopes.StepCountRead,
            OAuthScopes.SleepRead,
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
            .RequiredScopes.Should().Equal(OAuthScopes.TreatmentsRead);
    }

    [Fact]
    public async Task ChartData_Handler_RedactsTheCategoriesTheCallerLacks()
    {
        var service = new Mock<IChartDataService>();
        service
            .Setup(s => s.GetDashboardChartDataAsync(
                It.IsAny<long>(), It.IsAny<long>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(OneRecordPerCategory());

        var controller = new ChartDataController(service.Object, NullLogger<ChartDataController>.Instance)
        {
            ControllerContext = ContextWith(Granted(OAuthScopes.GlucoseRead)),
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
        var data = RetrospectiveReadScopeGuard.Redact(BothCategories(), Granted(OAuthScopes.GlucoseRead));

        data.Glucose.Should().NotBeNull();
        data.Iob.Should().BeNull();
        data.Cob.Should().BeNull();
        data.Basal.Should().BeNull();
        data.RecentTreatments.Should().BeEmpty();
    }

    [Fact]
    public void Retrospective_TreatmentsOnlyGrant_DropsTheGlucose()
    {
        var data = RetrospectiveReadScopeGuard.Redact(BothCategories(), Granted(OAuthScopes.TreatmentsRead));

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
            .Redact(timeline, Granted(OAuthScopes.GlucoseRead)).Data!.Single();

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
            .Redact(timeline, Granted(OAuthScopes.TreatmentsRead)).Data!.Single();

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
            .RequiredScopes.Should().Equal(OAuthScopes.TreatmentsRead);
    }

    [Fact]
    public async Task Retrospective_Handler_RedactsTheCategoriesTheCallerLacks()
    {
        var entries = new Mock<IEntryService>();
        entries
            .Setup(s => s.GetEntriesWithAdvancedFilterAsync(
                It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new Entry { Mills = 1_000, Sgv = 120, Direction = "Flat" }]);

        var boluses = new Mock<IBolusRepository>();
        boluses
            .Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(),
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
                It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IobResult());

        var cob = new Mock<ICobCalculator>();
        cob
            .Setup(c => c.CalculateTotalAsync(
                It.IsAny<List<CarbIntake>>(), It.IsAny<List<Bolus>?>(),
                It.IsAny<List<TempBasal>?>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CobResult());

        var basal = new Mock<IBasalRateResolver>();
        basal.Setup(r => r.GetBasalRateAsync(It.IsAny<long>())).ReturnsAsync(0.8);

        var controller = new RetrospectiveController(
            iob.Object, cob.Object, entries.Object, boluses.Object, carbs.Object,
            tempBasals.Object, projectionService: null!, basal.Object,
            NullLogger<RetrospectiveController>.Instance)
        {
            ControllerContext = ContextWith(Granted(OAuthScopes.GlucoseRead)),
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
        var data = PredictionReadScopeGuard.Redact(Forecast(), Granted(OAuthScopes.GlucoseRead));

        data.CurrentBg.Should().Be(120);
        data.Predictions.Default.Should().HaveCount(2);
        data.Iob.Should().Be(0);
        data.Cob.Should().Be(0);
        data.SensitivityRatio.Should().BeNull();
    }

    [Fact]
    public void Prediction_TreatmentsOnlyGrant_DropsTheCurves()
    {
        var data = PredictionReadScopeGuard.Redact(Forecast(), Granted(OAuthScopes.TreatmentsRead));

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
            .RequiredScopes.Should().Equal(OAuthScopes.TherapyRead);
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
            ControllerContext = ContextWith(Granted(OAuthScopes.GlucoseRead)),
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
            ControllerContext = ContextWith(Granted(OAuthScopes.GlucoseRead)),
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
        var data = WidgetSummaryReadScopeGuard.Redact(Summary(), Granted(OAuthScopes.GlucoseRead));

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
        var data = WidgetSummaryReadScopeGuard.Redact(Summary(), Granted(OAuthScopes.AlertsRead));

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
                It.IsAny<string>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Summary());

        var context = ContextWith(Granted(OAuthScopes.GlucoseRead));
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
    };

    [Fact]
    public void CurrentTherapyState_DevicesOnlyGrant_DropsTheSensitivity()
    {
        var data = CurrentTherapyStateReadScopeGuard.Redact(TherapyState(), Granted(OAuthScopes.DevicesRead));

        data.CurrentPumpMode.Should().Be(PumpModeState.Manual);
        data.Reservoir.Should().Be(42);
        data.PumpBatteryPercent.Should().Be(80);
        data.PumpBatteryVoltage.Should().Be(1.4);
        data.SensitivityPercent.Should().BeNull();
    }

    [Fact]
    public void CurrentTherapyState_TherapyOnlyGrant_DropsThePumpReadings()
    {
        var data = CurrentTherapyStateReadScopeGuard.Redact(TherapyState(), Granted(OAuthScopes.TherapyRead));

        data.SensitivityPercent.Should().Be(90);
        data.CurrentPumpMode.Should().BeNull();
        data.Reservoir.Should().BeNull();
        data.PumpBatteryPercent.Should().BeNull();
        data.PumpBatteryVoltage.Should().BeNull();
    }

    /// <summary>
    /// The Viewer role holds glucose and reports only, so it reaches neither category here.
    /// </summary>
    [Fact]
    public void CurrentTherapyState_ViewerScopes_SeeNothing()
    {
        var viewer = OAuthScopes.NormalizeMemberPermissions(
            TenantPermissions.SeedRolePermissions[TenantPermissions.SeedRoles.Viewer]);

        CurrentTherapyStateReadScopeGuard.AdmissionScopes
            .Should().NotContain(s => OAuthScopes.SatisfiesScope(viewer, s));
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
            stateSpans.Object, sensitivity.Object, pumps.Object)
        {
            ControllerContext = ContextWith(Granted(OAuthScopes.TherapyRead)),
        };

        var result = await controller.GetCurrentTherapyState();

        var data = result.Result.Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeOfType<CurrentTherapyStateResponse>().Subject;
        data.SensitivityPercent.Should().Be(90);
        data.CurrentPumpMode.Should().BeNull();
        data.Reservoir.Should().BeNull();
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
        gate!.RequiredScopes.Should().Equal(TenantPermissions.TenantSettings);

        foreach (var role in new[]
                 {
                     TenantPermissions.SeedRoles.Viewer,
                     TenantPermissions.SeedRoles.Clinician,
                     TenantPermissions.SeedRoles.Caretaker,
                 })
        {
            OAuthScopes.SatisfiesScope(
                OAuthScopes.NormalizeMemberPermissions(TenantPermissions.SeedRolePermissions[role]),
                TenantPermissions.TenantSettings)
                .Should().BeFalse($"{role} does not administer the tenant");
        }

        OAuthScopes.SatisfiesScope(
            OAuthScopes.NormalizeMemberPermissions(
                TenantPermissions.SeedRolePermissions[TenantPermissions.SeedRoles.Admin]),
            TenantPermissions.TenantSettings)
            .Should().BeTrue("an administrator must keep managing analytics collection");
    }
}
