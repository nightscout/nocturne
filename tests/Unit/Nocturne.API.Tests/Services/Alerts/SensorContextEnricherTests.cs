using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Nocturne.API.Configuration;
using Nocturne.API.Controllers.V4.Analytics;
using Nocturne.API.Services.Alerts;
using Nocturne.API.Services.Glucose;
using Nocturne.API.Services.Treatments;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts;

[Trait("Category", "Unit")]
public class SensorContextEnricherTests
{
    private readonly Mock<IIobService> _iobService = new();
    private readonly Mock<ICobService> _cobService = new();
    private readonly Mock<ITreatmentService> _treatmentService = new();
    private readonly Mock<IDeviceEventRepository> _deviceEventRepository = new();
    private readonly Mock<IPumpSnapshotRepository> _pumpSnapshotRepository = new();
    private readonly Mock<IApsSnapshotRepository> _apsSnapshotRepository = new();
    private readonly Mock<ITempBasalRepository> _tempBasalRepository = new();
    private readonly Mock<IUploaderSnapshotRepository> _uploaderSnapshotRepository = new();
    private readonly Mock<IStateSpanService> _stateSpanService = new();
    private readonly Mock<IAlertRepository> _alertRepository = new();
    private readonly Mock<ITargetRangeScheduleRepository> _targetRangeScheduleRepository = new();
    private readonly Mock<IActiveProfileResolver> _activeProfileResolver = new();
    private readonly Mock<ITherapySettingsResolver> _therapySettingsResolver = new();
    private readonly Mock<IPredictionService> _predictionService = new();
    private readonly FakeTimeProvider _timeProvider = new(new DateTimeOffset(2026, 3, 22, 12, 0, 0, TimeSpan.Zero));
    private readonly Guid _tenantId = Guid.NewGuid();

    [Fact]
    public async Task BgAndTrend_only_rule_triggers_no_external_fetches()
    {
        var enricher = BuildEnricher();
        var rule = MakeRule(AlertConditionType.Threshold, """{"direction":"above","value":180}""");

        await enricher.EnrichAsync(BaseContext(trendRate: 1.5m), new[] { rule }, _tenantId, CancellationToken.None);

        _treatmentService.Verify(s => s.GetTreatmentsByRangeAsync(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
        _iobService.Verify(s => s.CalculateTotalAsync(It.IsAny<List<Treatment>>(), It.IsAny<long?>(), It.IsAny<string?>(), It.IsAny<List<TempBasal>?>(), It.IsAny<CancellationToken>()), Times.Never);
        _cobService.Verify(s => s.CobTotalAsync(It.IsAny<List<Treatment>>(), It.IsAny<long?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
        _predictionService.Verify(s => s.GetPredictionsAsync(It.IsAny<string?>(), It.IsAny<DateTimeOffset?>(), It.IsAny<CancellationToken>()), Times.Never);
        _pumpSnapshotRepository.Verify(s => s.GetAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        _deviceEventRepository.Verify(s => s.GetLatestByEventTypeAsync(It.IsAny<DeviceEventType>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Never);
        _alertRepository.Verify(s => s.GetActiveAlertSnapshotsAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task TrendLeaf_derives_bucket_from_trend_rate_without_external_fetches()
    {
        var enricher = BuildEnricher();
        var rule = MakeRule(AlertConditionType.Trend, """{"bucket":"rising_fast"}""");

        var enriched = await enricher.EnrichAsync(BaseContext(trendRate: 4.5m), new[] { rule }, _tenantId, CancellationToken.None);

        enriched.TrendBucket.Should().Be(TrendBucket.RisingFast);
        _treatmentService.Verify(s => s.GetTreatmentsByRangeAsync(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
        _predictionService.Verify(s => s.GetPredictionsAsync(It.IsAny<string?>(), It.IsAny<DateTimeOffset?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task IobAndCob_share_one_treatment_fetch()
    {
        var enricher = BuildEnricher();
        _treatmentService.Setup(s => s.GetTreatmentsByRangeAsync(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<Treatment>());
        _iobService.Setup(s => s.CalculateTotalAsync(It.IsAny<List<Treatment>>(), It.IsAny<long?>(), It.IsAny<string?>(), It.IsAny<List<TempBasal>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IobResult { Iob = 1.5 });
        _cobService.Setup(s => s.CobTotalAsync(It.IsAny<List<Treatment>>(), It.IsAny<long?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CobResult { Cob = 24.0 });

        var json = """
        {
          "operator": "and",
          "conditions": [
            { "type": "iob", "iob": { "operator": ">", "value": 1 } },
            { "type": "cob", "cob": { "operator": ">", "value": 10 } }
          ]
        }
        """;
        var rule = MakeRule(AlertConditionType.Composite, json);

        var enriched = await enricher.EnrichAsync(BaseContext(), new[] { rule }, _tenantId, CancellationToken.None);

        enriched.IobUnits.Should().Be(1.5m);
        enriched.CobGrams.Should().Be(24.0m);
        _treatmentService.Verify(s => s.GetTreatmentsByRangeAsync(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task IobAndCob_anchor_at_replay_tick_not_wall_clock()
    {
        // Regression: ComputeIobAsync / ComputeCobAsync used to call CalculateTotalAsync
        // and CobTotalAsync without the `time:` argument, falling back to wall-clock UtcNow
        // inside the calculator. In replay this silently anchored the decay model at today
        // while feeding it treatments from the tick's 24h window — every bolus had decayed
        // past DIA by the time it was measured, so `iob <= 1u` (and similar) read true at
        // every tick. The fix threads the tick into the calculator via `time:`.
        var enricher = BuildEnricher();
        _treatmentService.Setup(s => s.GetTreatmentsByRangeAsync(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<Treatment>());

        long? capturedIobTime = null;
        long? capturedCobTime = null;
        _iobService.Setup(s => s.CalculateTotalAsync(
                It.IsAny<List<Treatment>>(), It.IsAny<long?>(), It.IsAny<string?>(),
                It.IsAny<List<TempBasal>?>(), It.IsAny<CancellationToken>()))
            .Callback<List<Treatment>, long?, string?, List<TempBasal>?, CancellationToken>(
                (_, time, _, _, _) => capturedIobTime = time)
            .ReturnsAsync(new IobResult { Iob = 0 });
        _cobService.Setup(s => s.CobTotalAsync(
                It.IsAny<List<Treatment>>(), It.IsAny<long?>(), It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .Callback<List<Treatment>, long?, string?, CancellationToken>(
                (_, time, _, _) => capturedCobTime = time)
            .ReturnsAsync(new CobResult { Cob = 0 });

        var json = """
        {
          "operator": "and",
          "conditions": [
            { "type": "iob", "iob": { "operator": "<=", "value": 1 } },
            { "type": "cob", "cob": { "operator": "<=", "value": 10 } }
          ]
        }
        """;
        var rule = MakeRule(AlertConditionType.Composite, json);
        var tick = new DateTime(2026, 3, 22, 10, 0, 0, DateTimeKind.Utc);
        var expectedMills = new DateTimeOffset(tick).ToUnixTimeMilliseconds();

        await enricher.EnrichAsOfAsync(
            BaseContext(), new[] { rule }, _tenantId, tick, CancellationToken.None);

        capturedIobTime.Should().Be(expectedMills);
        capturedCobTime.Should().Be(expectedMills);
    }

    [Fact]
    public async Task Predictions_returns_empty_when_service_unregistered()
    {
        var enricher = BuildEnricher(includePredictionService: false);
        var rule = MakeRule(AlertConditionType.Predicted, """{"operator":"<","value":70,"within_minutes":30}""");

        var enriched = await enricher.EnrichAsync(BaseContext(), new[] { rule }, _tenantId, CancellationToken.None);

        enriched.Predictions.Should().BeEmpty();
    }

    [Fact]
    public async Task Predictions_swallows_invalid_operation_and_returns_empty()
    {
        var enricher = BuildEnricher();
        _predictionService.Setup(p => p.GetPredictionsAsync(It.IsAny<string?>(), It.IsAny<DateTimeOffset?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("no readings available"));
        var rule = MakeRule(AlertConditionType.Predicted, """{"operator":"<","value":70,"within_minutes":30}""");

        var enriched = await enricher.EnrichAsync(BaseContext(), new[] { rule }, _tenantId, CancellationToken.None);

        enriched.Predictions.Should().BeEmpty();
    }

    [Fact]
    public async Task Predictions_maps_curve_to_offset_minutes_using_response_interval()
    {
        var enricher = BuildEnricher();
        _predictionService.Setup(p => p.GetPredictionsAsync(It.IsAny<string?>(), It.IsAny<DateTimeOffset?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GlucosePredictionResponse
            {
                IntervalMinutes = 5,
                Predictions = new PredictionCurves { Default = new List<double> { 110, 120, 130 } }
            });
        var rule = MakeRule(AlertConditionType.Predicted, """{"operator":"<","value":70,"within_minutes":30}""");

        var enriched = await enricher.EnrichAsync(BaseContext(), new[] { rule }, _tenantId, CancellationToken.None);

        enriched.Predictions.Should().HaveCount(3);
        enriched.Predictions[0].OffsetMinutes.Should().Be(5);
        enriched.Predictions[0].Mgdl.Should().Be(110m);
        enriched.Predictions[2].OffsetMinutes.Should().Be(15);
    }

    [Fact]
    public async Task Predictions_threads_asOf_when_enriching_replay_tick()
    {
        // Replay path: the tick instant must reach the prediction service so it can re-run
        // the pipeline anchored at that historical moment. EnrichAsync (live) leaves asOf
        // null; EnrichAsOfAsync passes the tick as a non-null UTC DateTimeOffset.
        var enricher = BuildEnricher();
        DateTimeOffset? capturedAsOf = null;
        _predictionService.Setup(p => p.GetPredictionsAsync(
                It.IsAny<string?>(),
                It.IsAny<DateTimeOffset?>(),
                It.IsAny<CancellationToken>()))
            .Callback<string?, DateTimeOffset?, CancellationToken>((_, asOf, _) => capturedAsOf = asOf)
            .ReturnsAsync(new GlucosePredictionResponse
            {
                IntervalMinutes = 5,
                Predictions = new PredictionCurves { Default = new List<double> { 100 } },
            });
        var rule = MakeRule(AlertConditionType.Predicted, """{"operator":"<","value":70,"within_minutes":30}""");
        var tick = new DateTime(2026, 3, 22, 10, 0, 0, DateTimeKind.Utc);

        var enriched = await enricher.EnrichAsOfAsync(
            BaseContext(), new[] { rule }, _tenantId, tick, CancellationToken.None);

        enriched.Predictions.Should().HaveCount(1);
        capturedAsOf.Should().NotBeNull();
        capturedAsOf!.Value.UtcDateTime.Should().Be(tick);
    }

    [Fact]
    public async Task Reservoir_pulls_latest_pump_snapshot()
    {
        var enricher = BuildEnricher();
        _pumpSnapshotRepository.Setup(r => r.GetAsync(null, null, null, null, 1, 0, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new PumpSnapshot { Reservoir = 42.5 } });
        var rule = MakeRule(AlertConditionType.Reservoir, """{"operator":"<","value":50}""");

        var enriched = await enricher.EnrichAsync(BaseContext(), new[] { rule }, _tenantId, CancellationToken.None);

        enriched.ReservoirUnits.Should().Be(42.5m);
    }

    [Fact]
    public async Task SiteAge_pulls_latest_site_change_event()
    {
        var enricher = BuildEnricher();
        var siteChangeAt = new DateTime(2026, 3, 20, 8, 0, 0, DateTimeKind.Utc);
        _deviceEventRepository.Setup(r => r.GetLatestByEventTypeAsync(DeviceEventType.SiteChange, It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeviceEvent { Timestamp = siteChangeAt, EventType = DeviceEventType.SiteChange });
        var rule = MakeRule(AlertConditionType.SiteAge, """{"operator":">","value":72}""");

        var enriched = await enricher.EnrichAsync(BaseContext(), new[] { rule }, _tenantId, CancellationToken.None);

        enriched.LastSiteChangeAt.Should().Be(siteChangeAt);
        _deviceEventRepository.Verify(r => r.GetLatestByEventTypeAsync(DeviceEventType.SensorStart, It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ActiveAlerts_pulled_only_when_alert_state_referenced()
    {
        var enricher = BuildEnricher();
        var snapshot = new ActiveAlertSnapshot("firing", new DateTime(2026, 3, 22, 11, 50, 0, DateTimeKind.Utc), null);
        var dict = new Dictionary<Guid, ActiveAlertSnapshot> { [Guid.NewGuid()] = snapshot };
        _alertRepository.Setup(r => r.GetActiveAlertSnapshotsAsync(_tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(dict);

        var json = $$"""{"alert_id":"{{Guid.NewGuid()}}","state":"firing"}""";
        var rule = MakeRule(AlertConditionType.AlertState, json);

        var enriched = await enricher.EnrichAsync(BaseContext(), new[] { rule }, _tenantId, CancellationToken.None);

        enriched.ActiveAlerts.Should().BeSameAs(dict);
        _alertRepository.Verify(r => r.GetActiveAlertSnapshotsAsync(_tenantId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(3.5, TrendBucket.RisingFast)]
    [InlineData(3.0, TrendBucket.RisingFast)]
    [InlineData(1.0, TrendBucket.Rising)]
    [InlineData(0.0, TrendBucket.Flat)]
    [InlineData(-1.0, TrendBucket.Flat)]
    [InlineData(-1.5, TrendBucket.Falling)]
    [InlineData(-3.0, TrendBucket.Falling)]
    [InlineData(-3.5, TrendBucket.FallingFast)]
    public async Task TrendBucket_derivation_boundaries(double? rateInput, TrendBucket? expected)
    {
        var enricher = BuildEnricher();
        var rule = MakeRule(AlertConditionType.Trend, """{"bucket":"flat"}""");
        decimal? rate = rateInput is null ? null : (decimal)rateInput.Value;

        var enriched = await enricher.EnrichAsync(BaseContext(trendRate: rate), new[] { rule }, _tenantId, CancellationToken.None);

        enriched.TrendBucket.Should().Be(expected);
    }

    // ----- Looping facts -----

    [Fact]
    public async Task LoopStale_need_fetches_only_aps_timestamp()
    {
        var enricher = BuildEnricher();
        var lastCycle = _timeProvider.GetUtcNow().UtcDateTime.AddMinutes(-7);
        _apsSnapshotRepository.Setup(r => r.GetLatestTimestampAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(lastCycle);
        var rule = MakeRule(AlertConditionType.LoopStale, """{"operator":">","minutes":15}""");

        var enriched = await enricher.EnrichAsync(BaseContext(), new[] { rule }, _tenantId, CancellationToken.None);

        enriched.LastApsCycleAt.Should().Be(lastCycle);
        enriched.HasEverApsCycled.Should().BeTrue();
        _apsSnapshotRepository.Verify(r => r.GetLatestTimestampAsync(null, It.IsAny<CancellationToken>()), Times.Once);
        _apsSnapshotRepository.Verify(r => r.GetLatestEnactedTimestampAsync(It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Never);
        _apsSnapshotRepository.Verify(r => r.GetLatestSensitivityRatioAsync(It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Never);
        _pumpSnapshotRepository.Verify(r => r.GetLatestAsync(It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task LoopEnactionStale_only_rule_populates_HasEverApsCycled_when_snapshot_exists()
    {
        // Regression: LoopEnactionStaleEvaluator's cold-start guard reads HasEverApsCycled
        // (there is no separate HasEverApsEnacted flag). RuleDataNeeds must therefore
        // co-fetch LastApsCycle for LoopEnactionStale rules, otherwise a tenant whose
        // only enabled looping rule is LoopEnactionStale would never fire on a healthy loop.
        var enricher = BuildEnricher();
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        _apsSnapshotRepository.Setup(r => r.GetLatestTimestampAsync(It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(now.AddMinutes(-3));
        _apsSnapshotRepository.Setup(r => r.GetLatestEnactedTimestampAsync(It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(now.AddMinutes(-3));
        var rule = MakeRule(AlertConditionType.LoopEnactionStale, """{"operator":">","minutes":15}""");

        var enriched = await enricher.EnrichAsync(BaseContext(), new[] { rule }, _tenantId, CancellationToken.None);

        enriched.HasEverApsCycled.Should().BeTrue("the enricher must populate the cold-start guard for LoopEnactionStale's evaluator");
        enriched.LastApsEnactedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task PumpStatus_need_fetches_pump_snapshot_and_state_span()
    {
        var enricher = BuildEnricher();
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        _pumpSnapshotRepository.Setup(r => r.GetLatestAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PumpSnapshot { Timestamp = now.AddMinutes(-2), BatteryPercent = 65 });
        _stateSpanService.Setup(s => s.GetActiveAtAsync(
                StateSpanCategory.PumpMode, PumpModeState.Suspended.ToString(),
                It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StateSpan
            {
                Category = StateSpanCategory.PumpMode,
                State = PumpModeState.Suspended.ToString(),
                StartTimestamp = now.AddMinutes(-3),
            });
        var rule = MakeRule(AlertConditionType.PumpSuspended, """{"is_active":true}""");

        var enriched = await enricher.EnrichAsync(BaseContext(), new[] { rule }, _tenantId, CancellationToken.None);

        enriched.HasEverPumpSnapshot.Should().BeTrue();
        enriched.PumpBatteryPercent.Should().Be(65m);
        enriched.ActivePumpSuspension.Should().NotBeNull();
        enriched.ActivePumpSuspension!.StartedAt.Should().Be(now.AddMinutes(-3));
        // Bidirectional contract: a fresh snapshot MUST drive a state-span lookup.
        _stateSpanService.Verify(s => s.GetActiveAtAsync(
            StateSpanCategory.PumpMode, It.IsAny<string>(),
            It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Stale_pump_snapshot_nulls_active_suspension_projection()
    {
        var enricher = BuildEnricher();
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        // 11 minutes is past the configured PumpFreshnessThreshold (default 10 minutes).
        _pumpSnapshotRepository.Setup(r => r.GetLatestAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PumpSnapshot { Timestamp = now.AddMinutes(-11), BatteryPercent = 50 });
        _stateSpanService.Setup(s => s.GetActiveAtAsync(
                StateSpanCategory.PumpMode, PumpModeState.Suspended.ToString(),
                It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StateSpan
            {
                Category = StateSpanCategory.PumpMode,
                State = PumpModeState.Suspended.ToString(),
                StartTimestamp = now.AddMinutes(-30),
            });
        var rule = MakeRule(AlertConditionType.PumpSuspended, """{"is_active":true}""");

        var enriched = await enricher.EnrichAsync(BaseContext(), new[] { rule }, _tenantId, CancellationToken.None);

        enriched.HasEverPumpSnapshot.Should().BeTrue();
        enriched.ActivePumpSuspension.Should().BeNull();
        // State span should not even be queried when the pump snapshot is stale.
        _stateSpanService.Verify(s => s.GetActiveAtAsync(
            StateSpanCategory.PumpMode, It.IsAny<string>(),
            It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task OverrideActive_need_projects_metadata_correctly()
    {
        var enricher = BuildEnricher();
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        _stateSpanService.Setup(s => s.GetActiveAtAsync(
                StateSpanCategory.Override, null, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StateSpan
            {
                Category = StateSpanCategory.Override,
                StartTimestamp = now.AddMinutes(-15),
                EndTimestamp = now.AddMinutes(45),
                Metadata = new Dictionary<string, object>
                {
                    ["insulinNeedsScaleFactor"] = 1.5,
                    ["reasonDisplay"] = "Eating Soon",
                },
            });
        var rule = MakeRule(AlertConditionType.OverrideActive, """{"is_active":true}""");

        var enriched = await enricher.EnrichAsync(BaseContext(), new[] { rule }, _tenantId, CancellationToken.None);

        enriched.ActiveOverride.Should().NotBeNull();
        enriched.ActiveOverride!.Multiplier.Should().Be(1.5m);
        enriched.ActiveOverride.Name.Should().Be("Eating Soon");
        enriched.ActiveOverride.StartedAt.Should().Be(now.AddMinutes(-15));
        enriched.ActiveOverride.EndsAt.Should().Be(now.AddMinutes(45));
    }

    [Fact]
    public async Task TempBasal_need_projects_rate_and_scheduled_when_both_present()
    {
        var enricher = BuildEnricher();
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        _tempBasalRepository.Setup(r => r.GetActiveAtAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TempBasal
            {
                StartTimestamp = now.AddMinutes(-10),
                EndTimestamp = now.AddMinutes(20),
                Rate = 1.2,
                ScheduledRate = 0.8,
            });
        var rule = MakeRule(AlertConditionType.TempBasal,
            """{"metric":"rate","operator":">","value":1.0}""");

        var enriched = await enricher.EnrichAsync(BaseContext(), new[] { rule }, _tenantId, CancellationToken.None);

        enriched.ActiveTempBasal.Should().NotBeNull();
        enriched.ActiveTempBasal!.Rate.Should().Be(1.2m);
        enriched.ActiveTempBasal.ScheduledRate.Should().Be(0.8m);
        enriched.ActiveTempBasal.StartedAt.Should().Be(now.AddMinutes(-10));
    }

    [Fact]
    public async Task Sensitivity_need_sets_HasEver_flag_correctly_on_null_vs_value()
    {
        // Case 1: repo returns a value → HasEverApsSensitivity = true.
        var enricher1 = BuildEnricher();
        _apsSnapshotRepository.Setup(r => r.GetLatestSensitivityRatioAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(0.85m);
        var rule = MakeRule(AlertConditionType.SensitivityRatio, """{"operator":"<","value":0.9}""");

        var enriched1 = await enricher1.EnrichAsync(BaseContext(), new[] { rule }, _tenantId, CancellationToken.None);

        enriched1.SensitivityRatio.Should().Be(0.85m);
        enriched1.HasEverApsSensitivity.Should().BeTrue();

        // Case 2: repo returns null → HasEverApsSensitivity stays false.
        var apsRepo2 = new Mock<IApsSnapshotRepository>();
        apsRepo2.Setup(r => r.GetLatestSensitivityRatioAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync((decimal?)null);
        var deps2 = new SensorContextEnricherDependencies(
            _iobService.Object, _cobService.Object, _treatmentService.Object,
            _deviceEventRepository.Object, _pumpSnapshotRepository.Object,
            apsRepo2.Object, _tempBasalRepository.Object, _uploaderSnapshotRepository.Object,
            _stateSpanService.Object, _alertRepository.Object,
            _targetRangeScheduleRepository.Object,
            _activeProfileResolver.Object,
            _therapySettingsResolver.Object,
            Options.Create(new AlertEvaluationOptions()));
        var enricher2 = new SensorContextEnricher(
            deps2,
            new ServiceCollection().BuildServiceProvider(),
            _timeProvider, new NullLogger<SensorContextEnricher>());

        var enriched2 = await enricher2.EnrichAsync(BaseContext(), new[] { rule }, _tenantId, CancellationToken.None);

        enriched2.SensitivityRatio.Should().BeNull();
        enriched2.HasEverApsSensitivity.Should().BeFalse();
    }

    [Fact]
    public async Task Cold_start_HasEver_flags_all_false_when_repos_empty()
    {
        var enricher = BuildEnricher();
        // All repositories return null/empty by default — Moq returns default(T) without setup.
        var json = """
        {
          "operator": "and",
          "conditions": [
            { "type": "loop_stale", "loop_stale": { "operator": ">", "minutes": 15 } },
            { "type": "pump_battery", "pump_battery": { "operator": "<", "value": 20 } },
            { "type": "uploader_battery", "uploader_battery": { "operator": "<", "value": 20 } },
            { "type": "sensitivity_ratio", "sensitivity_ratio": { "operator": "<", "value": 0.9 } }
          ]
        }
        """;
        var rule = MakeRule(AlertConditionType.Composite, json);

        var enriched = await enricher.EnrichAsync(BaseContext(), new[] { rule }, _tenantId, CancellationToken.None);

        enriched.HasEverApsCycled.Should().BeFalse();
        enriched.HasEverPumpSnapshot.Should().BeFalse();
        enriched.HasEverUploaderSnapshot.Should().BeFalse();
        enriched.HasEverApsSensitivity.Should().BeFalse();
    }

    [Fact]
    public async Task Lazy_no_fetches_when_no_rule_needs_loop_data()
    {
        var enricher = BuildEnricher();
        var rule = MakeRule(AlertConditionType.Threshold, """{"direction":"above","value":180}""");

        await enricher.EnrichAsync(BaseContext(), new[] { rule }, _tenantId, CancellationToken.None);

        _apsSnapshotRepository.Verify(r => r.GetLatestTimestampAsync(It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Never);
        _apsSnapshotRepository.Verify(r => r.GetLatestEnactedTimestampAsync(It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Never);
        _apsSnapshotRepository.Verify(r => r.GetLatestSensitivityRatioAsync(It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Never);
        _pumpSnapshotRepository.Verify(r => r.GetLatestAsync(It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Never);
        _tempBasalRepository.Verify(r => r.GetActiveAtAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
        _uploaderSnapshotRepository.Verify(r => r.GetLatestAsync(It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Never);
        _stateSpanService.Verify(s => s.GetActiveAtAsync(
            It.IsAny<StateSpanCategory>(), It.IsAny<string?>(),
            It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task PumpFreshnessThreshold_option_drives_suspension_projection()
    {
        // Configure a 30-minute threshold; an 11-minute-old snapshot is still fresh under it.
        var enricher = BuildEnricher(
            options: new AlertEvaluationOptions { PumpFreshnessThreshold = TimeSpan.FromMinutes(30) });
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        _pumpSnapshotRepository.Setup(r => r.GetLatestAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PumpSnapshot { Timestamp = now.AddMinutes(-11), BatteryPercent = 50 });
        _stateSpanService.Setup(s => s.GetActiveAtAsync(
                StateSpanCategory.PumpMode, PumpModeState.Suspended.ToString(),
                It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StateSpan
            {
                Category = StateSpanCategory.PumpMode,
                State = PumpModeState.Suspended.ToString(),
                StartTimestamp = now.AddMinutes(-15),
            });
        var rule = MakeRule(AlertConditionType.PumpSuspended, """{"is_active":true}""");

        var enriched = await enricher.EnrichAsync(BaseContext(), new[] { rule }, _tenantId, CancellationToken.None);

        enriched.ActivePumpSuspension.Should().NotBeNull();
        enriched.ActivePumpSuspension!.StartedAt.Should().Be(now.AddMinutes(-15));
    }

    [Fact]
    public async Task TempBasal_need_projects_with_null_scheduled_rate_when_not_present()
    {
        var enricher = BuildEnricher();
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        _tempBasalRepository.Setup(r => r.GetActiveAtAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TempBasal
            {
                StartTimestamp = now.AddMinutes(-5),
                EndTimestamp = now.AddMinutes(25),
                Rate = 1.0,
                ScheduledRate = null,
            });
        var rule = MakeRule(AlertConditionType.TempBasal,
            """{"metric":"rate","operator":">","value":0.5}""");

        var enriched = await enricher.EnrichAsync(BaseContext(), new[] { rule }, _tenantId, CancellationToken.None);

        enriched.ActiveTempBasal.Should().NotBeNull();
        enriched.ActiveTempBasal!.Rate.Should().Be(1.0m);
        enriched.ActiveTempBasal.ScheduledRate.Should().BeNull();
    }

    private SensorContextEnricher BuildEnricher(
        bool includePredictionService = true,
        AlertEvaluationOptions? options = null)
    {
        var services = new ServiceCollection();
        if (includePredictionService)
        {
            services.AddSingleton(_predictionService.Object);
        }
        var provider = services.BuildServiceProvider();

        var deps = new SensorContextEnricherDependencies(
            _iobService.Object,
            _cobService.Object,
            _treatmentService.Object,
            _deviceEventRepository.Object,
            _pumpSnapshotRepository.Object,
            _apsSnapshotRepository.Object,
            _tempBasalRepository.Object,
            _uploaderSnapshotRepository.Object,
            _stateSpanService.Object,
            _alertRepository.Object,
            _targetRangeScheduleRepository.Object,
            _activeProfileResolver.Object,
            _therapySettingsResolver.Object,
            Options.Create(options ?? new AlertEvaluationOptions()));

        return new SensorContextEnricher(
            deps,
            provider,
            _timeProvider,
            new NullLogger<SensorContextEnricher>());
    }

    private SensorContext BaseContext(decimal? trendRate = 0m) => new()
    {
        LatestValue = 110m,
        LatestTimestamp = _timeProvider.GetUtcNow().UtcDateTime,
        TrendRate = trendRate,
        LastReadingAt = _timeProvider.GetUtcNow().UtcDateTime,
    };

    private static AlertRuleSnapshot MakeRule(AlertConditionType type, string json) =>
        new(Id: Guid.NewGuid(),
            TenantId: Guid.NewGuid(),
            Name: "test",
            ConditionType: type,
            ConditionParams: json,
            Severity: AlertRuleSeverity.Warning,
            ClientConfiguration: "{}",
            SortOrder: 0,
            AutoResolveEnabled: false,
            AutoResolveParams: null);
}
