using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Clinical.LoopAnalysis;
using Nocturne.API.Services.Caching;
using Nocturne.API.Tests.Infrastructure;
using Nocturne.Core.Clinical.Treatments;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Nocturne.API.Configuration;
using Nocturne.API.Services.Alerts;
using Nocturne.API.Services.Devices;
using Nocturne.API.Services.Glucose;
using Nocturne.API.Services.Analytics;
using Nocturne.API.Tests.Services.Profiles.Resolvers;
using Nocturne.Alerts.Corpus;
using Nocturne.API.Services.Alerts.Engines;
using Nocturne.API.Tests.Services.Alerts.Engines;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.Sleep;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.API.Services.V4;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts;

[Trait("Category", "Unit")]
public class SensorContextEnricherTests
{
    private readonly Mock<IIobCalculator> _iobCalculator = new();
    private readonly Mock<ICobCalculator> _cobCalculator = new();
    private readonly Mock<ITreatmentService> _treatmentService = new();
    private readonly Mock<IBolusRepository> _bolusRepository = new();
    private readonly Mock<ICarbIntakeRepository> _carbIntakeRepository = new();
    private readonly Mock<IDeviceEventRepository> _deviceEventRepository = new();
    private readonly Mock<IPumpSnapshotRepository> _pumpSnapshotRepository = new();
    private readonly Mock<IApsSnapshotRepository> _apsSnapshotRepository = new();
    private readonly Mock<ITempBasalRepository> _tempBasalRepository = new();
    private readonly Mock<IUploaderSnapshotRepository> _uploaderSnapshotRepository = new();
    private readonly Mock<IStateSpanService> _stateSpanService = new();
    private readonly Mock<IAlertRepository> _alertRepository = new();
    private readonly Mock<IDndWindowRepository> _dndWindows = new();
    private readonly Mock<ITenantAlertSettingsRepository> _alertSettings = new();
    private readonly Mock<ITargetRangeScheduleRepository> _targetRangeScheduleRepository = new();
    private readonly Mock<IActiveProfileResolver> _activeProfileResolver = ActiveProfileResolverMock.WithDefault();
    private readonly Mock<IProfileActivationResolver> _profileActivations = new();
    private readonly Mock<ITherapySettingsResolver> _therapySettingsResolver = new();
    private readonly Mock<ISleepService> _sleepService = new();
    private readonly Mock<Nocturne.Core.Contracts.Repositories.ITrackerRepository> _trackerRepository = new();
    private readonly Mock<IPredictionService> _predictionService = new();
    private readonly Mock<IReservoirEstimationService> _reservoirEstimator = new();
    private readonly Mock<ISensorStateReportRepository> _sensorStates = new();
    private readonly FakeTimeProvider _timeProvider = new(new DateTimeOffset(2026, 3, 22, 12, 0, 0, TimeSpan.Zero));
    private readonly Guid _tenantId = Guid.NewGuid();

    public SensorContextEnricherTests()
    {
        // The repo never returns null; default the new DND-windows fetch to empty so the
        // unconditional enrichment block is exercised. Per-test setups override this.
        _dndWindows
            .Setup(r => r.GetUnexpiredSnapshotsAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<DndWindowSnapshot>());
    }

    private static readonly ReadingSource CareLink = new("carelink-connector", "CareLink MiniMed 780G");

    /// <summary>
    /// Makes <paramref name="newest"/> the report the repository reads as the newest of
    /// <paramref name="source"/>'s, whatever bounds it is asked for: which report that is, and when it
    /// arrived, is the repository's (DeviceStatusBatchReadsTests).
    /// </summary>
    private void NewestIs(ReadingSource source, ArrivedSensorStateReport newest) =>
        _sensorStates
            .Setup(r => r.GetNewestAsync(
                source.Device!, source.DataSource!, It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(newest);

    /// <summary>
    /// The newest report, arrived <paramref name="minutesAgo"/> before now: <paramref name="state"/> is
    /// what its status's value maps to, null for one outside the allowlist.
    /// </summary>
    private ArrivedSensorStateReport Newest(double minutesAgo, SensorState? state) =>
        new(new SensorStateReport { State = state }, _timeProvider.GetUtcNow().UtcDateTime.AddMinutes(-minutesAgo));

    private SensorContext ReadingOf(ReadingSource? source) => BaseContext() with { LatestSource = source };

    private static AlertRuleSnapshot LowRule() =>
        MakeRule(AlertConditionType.Threshold, """{"direction":"below","value":70}""");

    [Fact]
    public async Task A_glucose_rule_reads_the_state_its_readings_source_reports_until_it_lapses()
    {
        NewestIs(CareLink, Newest(4, SensorState.WarmingUp));

        var enriched = await BuildEnricher().EnrichAsync(ReadingOf(CareLink), [LowRule()], CancellationToken.None);

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        enriched.SensorState.Should().Be(new SensorStateSnapshot(SensorState.WarmingUp, Since: null, now.AddMinutes(7)),
            "it arrived 4 minutes ago, so it counts for 7 more");
    }

    [Theory]
    [InlineData(null, "CareLink MiniMed 780G")]
    [InlineData("carelink-connector", null)]
    [InlineData("carelink-connector", "")]
    public async Task A_reading_that_lacks_its_source_reads_no_report(string? dataSource, string? device)
    {
        var enriched = await BuildEnricher().EnrichAsync(
            ReadingOf(new ReadingSource(dataSource, device)), [LowRule()], CancellationToken.None);

        enriched.SensorState.Should().BeNull();
        _sensorStates.Verify(
            r => r.GetNewestAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task A_newest_reading_that_alternates_between_sources_reads_only_its_own_sources_state()
    {
        var dexcom = new ReadingSource("dexcom-connector", "Dexcom G7");
        NewestIs(CareLink, Newest(2, SensorState.Error));
        NewestIs(dexcom, Newest(1, SensorState.Ok));
        var enricher = BuildEnricher();

        var careLink = await enricher.EnrichAsync(ReadingOf(CareLink), [LowRule()], CancellationToken.None);
        var g7 = await enricher.EnrichAsync(ReadingOf(dexcom), [LowRule()], CancellationToken.None);
        var careLinkAgain = await enricher.EnrichAsync(ReadingOf(CareLink), [LowRule()], CancellationToken.None);

        careLink.SensorState!.State.Should().Be(SensorState.Error);
        g7.SensorState!.State.Should().Be(SensorState.Ok);
        careLinkAgain.SensorState!.State.Should().Be(SensorState.Error);
    }

    [Fact]
    public async Task A_rule_with_no_glucose_leaf_reads_no_status()
    {
        var rule = MakeRule(AlertConditionType.SignalLoss, """{"timeout_minutes":15}""");

        await BuildEnricher().EnrichAsync(ReadingOf(CareLink), [rule], CancellationToken.None);

        _sensorStates.Verify(
            r => r.GetNewestAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task The_read_asks_for_the_reports_of_the_readings_device_and_source_within_the_bound()
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;

        await BuildEnricher().EnrichAsync(ReadingOf(CareLink), [LowRule()], CancellationToken.None);

        _sensorStates.Verify(
            r => r.GetNewestAsync(
                "CareLink MiniMed 780G", "carelink-connector", now - SensorStateReports.MaxAge, now, AlertReadings.Bound(now),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [NativeFact]
    public async Task A_newest_report_with_no_state_leaves_the_state_absent()
    {
        NewestIs(CareLink, Newest(1, state: null));
        var low = LowRule();
        var error = MakeRule(AlertConditionType.SensorState, """{"states":["error"]}""");
        var reading = ReadingOf(CareLink) with { LatestValue = 55m };

        var enriched = await BuildEnricher().EnrichAsync(reading, [low, error], CancellationToken.None);
        var lowRead = await EvaluatedAsync(low, enriched);
        var errorRead = await EvaluatedAsync(error, enriched);

        enriched.SensorState.Should().BeNull("an older report does not speak for the sensor after the newest");
        lowRead.ConditionUnknown.Should().BeFalse("no state holds the low");
        lowRead.ConditionMet.Should().BeTrue();
        errorRead.ConditionUnknown.Should().BeTrue("the sensor's state is not known");
    }

    [Fact]
    public async Task No_report_reads_as_no_state_never_ok()
    {
        var enriched = await BuildEnricher().EnrichAsync(ReadingOf(CareLink), [LowRule()], CancellationToken.None);

        enriched.SensorState.Should().BeNull();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    public async Task Replay_reads_the_state_live_reads_at_the_same_instant(double minutesLater)
    {
        NewestIs(CareLink, Newest(4, SensorState.WarmingUp));
        var at = _timeProvider.GetUtcNow().UtcDateTime.AddMinutes(minutesLater);

        var replay = await BuildEnricher().EnrichAsOfAsync(ReadingOf(CareLink), [LowRule()], at, CancellationToken.None);
        _timeProvider.Advance(TimeSpan.FromMinutes(minutesLater));
        var live = await BuildEnricher().EnrichAsync(ReadingOf(CareLink), [LowRule()], CancellationToken.None);

        replay.SensorState.Should().NotBeNull().And.Be(live.SensorState);
        _sensorStates.Verify(
            r => r.GetNewestAsync(
                CareLink.Device!, CareLink.DataSource!, at - SensorStateReports.MaxAge, at, AlertReadings.Bound(at),
                It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    [Theory]
    [InlineData(SensorState.Error, SensorState.Error)]
    [InlineData(SensorState.Ok, SensorState.NoValue)]
    public async Task A_state_that_is_not_ok_says_why_a_reading_has_no_value_and_ok_does_not(SensorState reported, SensorState expected)
    {
        NewestIs(CareLink, Newest(1, reported));
        var rule = MakeRule(AlertConditionType.SensorState, """{"states":["error"]}""");
        var noValue = ReadingOf(CareLink) with
        {
            LatestValue = null,
            SensorState = new SensorStateSnapshot(SensorState.NoValue, _timeProvider.GetUtcNow().UtcDateTime),
        };

        var enriched = await BuildEnricher().EnrichAsync(noValue, [rule], CancellationToken.None);

        enriched.SensorState!.State.Should().Be(expected);
    }

    [Fact]
    public async Task BgAndTrend_only_rule_triggers_no_external_fetches()
    {
        var enricher = BuildEnricher();
        var rule = MakeRule(AlertConditionType.Threshold, """{"direction":"above","value":180}""");

        await enricher.EnrichAsync(BaseContext(trendRate: 1.5m), new[] { rule }, CancellationToken.None);

        _treatmentService.Verify(s => s.GetTreatmentsByRangeAsync(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
        _iobCalculator.Verify(s => s.CalculateTotalAsync(It.IsAny<List<Bolus>>(), It.IsAny<List<TempBasal>?>(), It.IsAny<long>(), It.IsAny<long?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        _cobCalculator.Verify(s => s.CalculateTotalAsync(It.IsAny<List<CarbIntake>>(), It.IsAny<List<Bolus>?>(), It.IsAny<List<TempBasal>?>(), It.IsAny<long>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        _predictionService.Verify(s => s.GetPredictionsAsync(It.IsAny<string?>(), It.IsAny<DateTimeOffset?>(), It.IsAny<CancellationToken>()), Times.Never);
        _pumpSnapshotRepository.Verify(s => s.GetAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        _deviceEventRepository.Verify(s => s.GetLatestByEventTypeAsync(It.IsAny<DeviceEventType>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Never);
        _deviceEventRepository.Verify(s => s.GetLatestOfAnyTypeAsync(It.IsAny<IReadOnlyCollection<DeviceEventType>>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Never);
        _alertRepository.Verify(s => s.GetActiveAlertSnapshotsAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task IobAndCob_share_one_treatment_fetch()
    {
        var enricher = BuildEnricher();
        _treatmentService.Setup(s => s.GetTreatmentsByRangeAsync(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<Treatment>());
        _iobCalculator.Setup(s => s.CalculateTotalAsync(It.IsAny<List<Bolus>>(), It.IsAny<List<TempBasal>?>(), It.IsAny<long>(), It.IsAny<long?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IobResult { Iob = 1.5 });
        _cobCalculator.Setup(s => s.CalculateTotalAsync(It.IsAny<List<CarbIntake>>(), It.IsAny<List<Bolus>?>(), It.IsAny<List<TempBasal>?>(), It.IsAny<long>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
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

        var enriched = await enricher.EnrichAsync(BaseContext(), new[] { rule }, CancellationToken.None);

        enriched.IobUnits.Should().Be(1.5m);
        enriched.CobGrams.Should().Be(24.0m);
        // V4-native enricher pulls Bolus/CarbIntake/TempBasal from their V4 repos directly
        // — the legacy treatment-range fetch is no longer in this path.
        _iobCalculator.Verify(s => s.CalculateTotalAsync(
            It.IsAny<List<Bolus>>(), It.IsAny<List<TempBasal>?>(), It.IsAny<long>(), It.IsAny<long?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
        _cobCalculator.Verify(s => s.CalculateTotalAsync(
            It.IsAny<List<CarbIntake>>(), It.IsAny<List<Bolus>?>(), It.IsAny<List<TempBasal>?>(), It.IsAny<long>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task LiveIob_IsCalculatedFresh_NotServedFromTheReadersOneMinuteCache()
    {
        var cache = TestTenantCache.Create(partition: "full");
        cache.SetLocal("iobcob:current", new IobCobReading(
            new IobResult { Iob = 9.0 }, new CobResult { Cob = 90 },
            new IobCobInputs([], [], [])), TimeSpan.FromMinutes(1));
        var enricher = BuildEnricher(iobCobCache: cache);
        _iobCalculator.Setup(s => s.CalculateTotalAsync(It.IsAny<List<Bolus>>(), It.IsAny<List<TempBasal>?>(), It.IsAny<long>(), It.IsAny<long?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IobResult { Iob = 1.5 });
        _cobCalculator.Setup(s => s.CalculateTotalAsync(It.IsAny<List<CarbIntake>>(), It.IsAny<List<Bolus>?>(), It.IsAny<List<TempBasal>?>(), It.IsAny<long>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CobResult { Cob = 24.0 });
        var rule = MakeRule(AlertConditionType.Composite, """
        {
          "operator": "and",
          "conditions": [
            { "type": "iob", "iob": { "operator": ">", "value": 1 } },
            { "type": "cob", "cob": { "operator": ">", "value": 10 } }
          ]
        }
        """);

        var enriched = await enricher.EnrichAsync(BaseContext(), new[] { rule }, CancellationToken.None);

        enriched.IobUnits.Should().Be(1.5m);
        enriched.CobGrams.Should().Be(24.0m);
    }

    [Fact]
    public async Task ACobFailure_LeavesIobEvaluated()
    {
        var enricher = BuildEnricher();
        _iobCalculator.Setup(s => s.CalculateTotalAsync(It.IsAny<List<Bolus>>(), It.IsAny<List<TempBasal>?>(), It.IsAny<long>(), It.IsAny<long?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IobResult { Iob = 1.5 });
        _cobCalculator.Setup(s => s.CalculateTotalAsync(It.IsAny<List<CarbIntake>>(), It.IsAny<List<Bolus>?>(), It.IsAny<List<TempBasal>?>(), It.IsAny<long>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("cob"));
        var rule = MakeRule(AlertConditionType.Composite, """
        {
          "operator": "and",
          "conditions": [
            { "type": "iob", "iob": { "operator": ">", "value": 1 } },
            { "type": "cob", "cob": { "operator": ">", "value": 10 } }
          ]
        }
        """);

        var enriched = await enricher.EnrichAsync(BaseContext(), new[] { rule }, CancellationToken.None);

        enriched.IobUnits.Should().Be(1.5m);
        enriched.CobGrams.Should().BeNull();
    }

    [Fact]
    public async Task ReplayWithOnlyIobRules_DoesNotCalculateCob()
    {
        var enricher = BuildEnricher();
        _iobCalculator.Setup(s => s.CalculateTotalAsync(It.IsAny<List<Bolus>>(), It.IsAny<List<TempBasal>?>(), It.IsAny<long>(), It.IsAny<long?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IobResult { Iob = 0.5 });
        var rule = MakeRule(AlertConditionType.Composite, """
        {
          "operator": "and",
          "conditions": [ { "type": "iob", "iob": { "operator": ">", "value": 1 } } ]
        }
        """);

        await enricher.EnrichAsOfAsync(
            BaseContext(), new[] { rule }, new DateTime(2026, 3, 22, 10, 0, 0, DateTimeKind.Utc), CancellationToken.None);

        _cobCalculator.Verify(s => s.CalculateTotalAsync(
            It.IsAny<List<CarbIntake>>(), It.IsAny<List<Bolus>?>(), It.IsAny<List<TempBasal>?>(),
            It.IsAny<long>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
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
        _iobCalculator.Setup(s => s.CalculateTotalAsync(
                It.IsAny<List<Bolus>>(), It.IsAny<List<TempBasal>?>(),
                It.IsAny<long>(), It.IsAny<long?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Callback<List<Bolus>, List<TempBasal>?, long, long?, bool, CancellationToken>(
                (_, _, time, _, _, _) => capturedIobTime = time)
            .ReturnsAsync(new IobResult { Iob = 0 });
        _cobCalculator.Setup(s => s.CalculateTotalAsync(
                It.IsAny<List<CarbIntake>>(), It.IsAny<List<Bolus>?>(), It.IsAny<List<TempBasal>?>(),
                It.IsAny<long>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Callback<List<CarbIntake>, List<Bolus>?, List<TempBasal>?, long, bool, CancellationToken>(
                (_, _, _, time, _, _) => capturedCobTime = time)
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
            BaseContext(), new[] { rule }, tick, CancellationToken.None);

        capturedIobTime.Should().Be(expectedMills);
        capturedCobTime.Should().Be(expectedMills);

        // One IOB read, the shared reader's window ending at the tick, serves both, and a
        // replay tick is not the live instant.
        _bolusRepository.Verify(r => r.GetAsync(
            tick.AddMilliseconds(-DailyProfileCalculator.ActingMs), tick, null, null, It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(),
            It.IsAny<BolusKind?>(), It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()),
            Times.Once);
        _bolusRepository.VerifyNoOtherCalls();
        _iobCalculator.Verify(s => s.CalculateTotalAsync(
            It.IsAny<List<Bolus>>(), It.IsAny<List<TempBasal>?>(), expectedMills, It.IsAny<long?>(), false,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Predictions_returns_empty_when_service_unregistered()
    {
        var enricher = BuildEnricher(includePredictionService: false);
        var rule = MakeRule(AlertConditionType.Predicted, """{"operator":"<","value":70,"within_minutes":30}""");

        var enriched = await enricher.EnrichAsync(BaseContext(), new[] { rule }, CancellationToken.None);

        enriched.Predictions.Should().BeEmpty();
    }

    [Fact]
    public async Task Predictions_swallows_invalid_operation_and_returns_empty()
    {
        var enricher = BuildEnricher();
        _predictionService.Setup(p => p.GetPredictionsAsync(It.IsAny<string?>(), It.IsAny<DateTimeOffset?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("no readings available"));
        var rule = MakeRule(AlertConditionType.Predicted, """{"operator":"<","value":70,"within_minutes":30}""");

        var enriched = await enricher.EnrichAsync(BaseContext(), new[] { rule }, CancellationToken.None);

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

        var enriched = await enricher.EnrichAsync(BaseContext(), new[] { rule }, CancellationToken.None);

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
            BaseContext(), new[] { rule }, tick, CancellationToken.None);

        enriched.Predictions.Should().HaveCount(1);
        capturedAsOf.Should().NotBeNull();
        capturedAsOf!.Value.UtcDateTime.Should().Be(tick);
    }

    [Fact]
    public async Task Reservoir_rule_pulls_estimate()
    {
        var enricher = BuildEnricher();
        _reservoirEstimator.Setup(e => e.GetEstimateAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ReservoirEstimate(42.5m, IsLowerBound: false, IsEstimated: true));
        var rule = MakeRule(AlertConditionType.Reservoir, """{"operator":"<","value":50}""");

        var enriched = await enricher.EnrichAsync(BaseContext(), new[] { rule }, CancellationToken.None);

        enriched.ReservoirUnits.Should().Be(42.5m);
        enriched.ReservoirIsLowerBound.Should().BeFalse();
    }

    [Fact]
    public async Task Reservoir_lower_bound_estimate_flows_to_context()
    {
        var enricher = BuildEnricher();
        _reservoirEstimator.Setup(e => e.GetEstimateAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ReservoirEstimate(50m, IsLowerBound: true, IsEstimated: false));
        var rule = MakeRule(AlertConditionType.Reservoir, """{"operator":"<","value":50}""");

        var enriched = await enricher.EnrichAsync(BaseContext(), new[] { rule }, CancellationToken.None);

        enriched.ReservoirUnits.Should().Be(50m);
        enriched.ReservoirIsLowerBound.Should().BeTrue();
    }

    [Fact]
    public async Task Reservoir_missing_estimate_leaves_context_null()
    {
        var enricher = BuildEnricher();
        _reservoirEstimator.Setup(e => e.GetEstimateAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ReservoirEstimate?)null);
        var rule = MakeRule(AlertConditionType.Reservoir, """{"operator":"<","value":50}""");

        var enriched = await enricher.EnrichAsync(BaseContext(), new[] { rule }, CancellationToken.None);

        enriched.ReservoirUnits.Should().BeNull();
        enriched.ReservoirIsLowerBound.Should().BeFalse();
    }

    [Fact]
    public async Task SiteAge_pulls_latest_site_change_event()
    {
        var enricher = BuildEnricher();
        var siteChangeAt = new DateTime(2026, 3, 20, 8, 0, 0, DateTimeKind.Utc);
        _deviceEventRepository.Setup(r => r.GetLatestOfAnyTypeAsync(
                It.Is<IReadOnlyCollection<DeviceEventType>>(t => t.Contains(DeviceEventType.SiteChange)),
                It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeviceEvent { Timestamp = siteChangeAt, EventType = DeviceEventType.SiteChange });
        var rule = MakeRule(AlertConditionType.SiteAge, """{"operator":">","value":72}""");

        var enriched = await enricher.EnrichAsync(BaseContext(), new[] { rule }, CancellationToken.None);

        enriched.LastSiteChangeAt.Should().Be(siteChangeAt);
        _deviceEventRepository.Verify(r => r.GetLatestByEventTypeAsync(DeviceEventType.SensorStart, It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>A pod change is a new site, however the pump reports it.</summary>
    [Fact]
    public async Task SiteAge_follows_pod_changes_and_activations()
    {
        var enricher = BuildEnricher();
        IReadOnlyCollection<DeviceEventType>? asked = null;
        _deviceEventRepository.Setup(r => r.GetLatestOfAnyTypeAsync(
                It.IsAny<IReadOnlyCollection<DeviceEventType>>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .Callback((IReadOnlyCollection<DeviceEventType> types, DateTime? _, CancellationToken _) => asked = types)
            .ReturnsAsync((DeviceEvent?)null);
        var rule = MakeRule(AlertConditionType.SiteAge, """{"operator":">","value":72}""");

        await enricher.EnrichAsync(BaseContext(), new[] { rule }, CancellationToken.None);

        asked.Should().Contain([DeviceEventType.SiteChange, DeviceEventType.PodChange, DeviceEventType.PodActivated]);
    }

    [Fact]
    public async Task ActiveAlerts_pulled_only_when_alert_state_referenced()
    {
        var enricher = BuildEnricher();
        var snapshot = new ActiveAlertSnapshot("firing", new DateTime(2026, 3, 22, 11, 50, 0, DateTimeKind.Utc), null);
        var dict = new Dictionary<Guid, ActiveAlertSnapshot> { [Guid.NewGuid()] = snapshot };
        _alertRepository.Setup(r => r.GetActiveAlertSnapshotsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(dict);

        var json = $$"""{"alert_id":"{{Guid.NewGuid()}}","state":"firing"}""";
        var rule = MakeRule(AlertConditionType.AlertState, json);

        var enriched = await enricher.EnrichAsync(BaseContext(), new[] { rule }, CancellationToken.None);

        enriched.ActiveAlerts.Should().BeSameAs(dict);
        _alertRepository.Verify(r => r.GetActiveAlertSnapshotsAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    // ----- Looping facts -----

    [Fact]
    public async Task LoopStale_need_fetches_only_aps_timestamp()
    {
        var enricher = BuildEnricher();
        var lastCycle = _timeProvider.GetUtcNow().UtcDateTime.AddMinutes(-7);
        _apsSnapshotRepository.Setup(r => r.GetLatestTimestampAsOfAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(lastCycle);
        var rule = MakeRule(AlertConditionType.LoopStale, """{"operator":">","minutes":15}""");

        var enriched = await enricher.EnrichAsync(BaseContext(), new[] { rule }, CancellationToken.None);

        enriched.LastApsCycleAt.Should().Be(lastCycle);
        enriched.HasEverApsCycled.Should().BeTrue();
        _apsSnapshotRepository.Verify(r => r.GetLatestTimestampAsOfAsync(null, It.IsAny<CancellationToken>()), Times.Once);
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
        _apsSnapshotRepository.Setup(r => r.GetLatestTimestampAsOfAsync(It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(now.AddMinutes(-3));
        _apsSnapshotRepository.Setup(r => r.GetLatestEnactedTimestampAsync(It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(now.AddMinutes(-3));
        var rule = MakeRule(AlertConditionType.LoopEnactionStale, """{"operator":">","minutes":15}""");

        var enriched = await enricher.EnrichAsync(BaseContext(), new[] { rule }, CancellationToken.None);

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
        SpansStored(PumpSpan(PumpModeState.Suspended.ToString(), now.AddMinutes(-3)));
        var rule = MakeRule(AlertConditionType.PumpSuspended, """{"is_active":true}""");

        var enriched = await enricher.EnrichAsync(BaseContext(), new[] { rule }, CancellationToken.None);

        enriched.HasEverPumpSnapshot.Should().BeTrue();
        enriched.PumpBatteryPercent.Should().Be(65m);
        enriched.ActivePumpSuspension.Should().NotBeNull();
        enriched.ActivePumpSuspension!.StartedAt.Should().Be(now.AddMinutes(-3));
        // Bidirectional contract: a fresh snapshot MUST drive a state-span lookup.
        PumpModeReads(Times.Once());
    }

    /// <summary>
    /// The reads of a live pass: the spans open at <paramref name="at"/>, apart from them the open ones starting
    /// after it within the tolerance, and the newest one not ended before it alone. A replay pass makes only the first.
    /// </summary>
    private void PumpModeReads(Times times, DateTime? at = null, bool replay = false)
    {
        var now = at ?? _timeProvider.GetUtcNow().UtcDateTime;
        _stateSpanService.Verify(s => s.GetStateSpansAsync(
            StateSpanCategory.PumpMode, null, now, now, null, null, It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(),
            It.IsAny<CancellationToken>()), times);
        _stateSpanService.Verify(s => s.GetStateSpansAsync(
            StateSpanCategory.PumpMode, null, now, AlertReadings.Bound(now), null, true, It.IsAny<int>(), It.IsAny<int>(),
            It.IsAny<bool>(), It.IsAny<CancellationToken>()), replay ? Times.Never() : times);
        _stateSpanService.Verify(s => s.GetStateSpansAsync(
            StateSpanCategory.PumpMode, null, now, null, null, null, 1, It.IsAny<int>(),
            It.IsAny<bool>(), It.IsAny<CancellationToken>()), replay ? Times.Never() : times);
    }

    private static StateSpan PumpSpan(string state, DateTime start) => new()
    {
        Category = StateSpanCategory.PumpMode,
        State = state,
        StartTimestamp = start,
    };

    /// <summary>
    /// Stores <paramref name="spans"/>, read as the repository reads them: a range read keeps a span that
    /// ends at or after <c>from</c> and starts at or before <c>to</c>, and an at-read one that contains the instant.
    /// </summary>
    private void SpansStored(params StateSpan[] spans)
    {
        _stateSpanService.Setup(s => s.GetStateSpansAsync(
                It.IsAny<StateSpanCategory?>(), It.IsAny<string?>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<string?>(), It.IsAny<bool?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((StateSpanCategory? category, string? state, DateTime? from, DateTime? to, string? _, bool? active,
                    int count, int _, bool descending, CancellationToken _) =>
                (descending
                    ? spans.OrderByDescending(s => s.StartTimestamp)
                    : spans.OrderBy(s => s.StartTimestamp))
                .Where(s => (category is null || s.Category == category)
                            && (state is null || s.State == state)
                            && (active is null || active == (s.EndTimestamp is null))
                            && (from is null || s.EndTimestamp is null || s.EndTimestamp >= from)
                            && (to is null || s.StartTimestamp <= to))
                .Take(count)
                .ToList());
        _stateSpanService.Setup(s => s.GetAllCopiesAsync(
                It.IsAny<StateSpanCategory>(), It.IsAny<DateTime>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((StateSpanCategory category, DateTime from, DateTime? to, CancellationToken _) =>
                (IReadOnlyList<StateSpan>)spans
                    .Where(s => s.Category == category
                                && (s.EndTimestamp is null || s.EndTimestamp >= from)
                                && (to is null || s.StartTimestamp <= to))
                    .OrderBy(s => s.StartTimestamp)
                    .ToList());
        _stateSpanService.Setup(s => s.GetActiveAtAsync(
                It.IsAny<StateSpanCategory>(), It.IsAny<string?>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((StateSpanCategory category, string? state, DateTime at, CancellationToken _) =>
                spans.Where(s => s.Category == category
                                 && (state is null || s.State == state)
                                 && s.StartTimestamp <= at
                                 && (s.EndTimestamp is null || s.EndTimestamp > at))
                    .MaxBy(s => s.StartTimestamp));
    }

    private async Task<IReadOnlyList<PumpModeSnapshot>> PumpModesFor(string ruleMode)
    {
        var rule = MakeRule(AlertConditionType.PumpState, $$"""{"mode":"{{ruleMode}}","is_active":false}""");
        var enriched = await BuildEnricher().EnrichAsync(BaseContext(), new[] { rule }, CancellationToken.None);
        return enriched.ActivePumpModes;
    }

    [Theory]
    [InlineData("Automatic")]
    [InlineData("Suspended")]
    [InlineData("Manual")]
    public async Task Every_active_pump_mode_is_sent_whichever_mode_the_rule_names(string ruleMode)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        SpansStored(PumpSpan("Suspended", now.AddMinutes(-5)), PumpSpan("Automatic", now.AddMinutes(-60)));

        var modes = await PumpModesFor(ruleMode);

        modes.Should().Equal(
            new PumpModeSnapshot("Automatic", now.AddMinutes(-60)),
            new PumpModeSnapshot("Suspended", now.AddMinutes(-5)));
        _stateSpanService.Verify(s => s.GetActiveAtAsync(
            It.IsAny<StateSpanCategory>(), It.IsAny<string?>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task An_unrecognised_state_is_sent_as_written_beside_the_known_modes()
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        SpansStored(
            PumpSpan("Suspended", now.AddMinutes(-1)),
            PumpSpan("Turbo", now.AddMinutes(-3)),
            PumpSpan("Zed", now.AddMinutes(-3)));

        var modes = await PumpModesFor("Manual");

        modes.Should().Equal(
            new PumpModeSnapshot("Turbo", now.AddMinutes(-3)),
            new PumpModeSnapshot("Zed", now.AddMinutes(-3)),
            new PumpModeSnapshot("Suspended", now.AddMinutes(-1)));
    }

    [Fact]
    public async Task Two_spans_of_one_mode_are_both_sent()
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        SpansStored(PumpSpan("Manual", now.AddMinutes(-20)), PumpSpan("Manual", now.AddMinutes(-2)));

        (await PumpModesFor("Manual")).Should().HaveCount(2);
    }

    [Fact]
    public async Task A_span_that_has_ended_or_has_no_state_is_not_open()
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var ended = PumpSpan("Manual", now.AddMinutes(-20));
        ended.EndTimestamp = now.AddMinutes(-1);
        SpansStored(ended, PumpSpan("", now.AddMinutes(-3)));

        (await PumpModesFor("Manual")).Should().BeEmpty();
    }

    [Fact]
    public async Task A_rule_with_no_pump_state_leaf_loads_no_pump_modes()
    {
        var rule = MakeRule(AlertConditionType.Iob, """{"operator":">","value":2}""");

        var enriched = await BuildEnricher().EnrichAsync(BaseContext(), new[] { rule }, CancellationToken.None);

        enriched.ActivePumpModes.Should().BeEmpty();
        _stateSpanService.Verify(s => s.GetStateSpansAsync(
            It.IsAny<StateSpanCategory?>(), It.IsAny<string?>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
            It.IsAny<string?>(), It.IsAny<bool?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    private const string SuspendedRule = """{"is_active":true}""";
    private const string SuspendedModeRule = """{"mode":"Suspended","is_active":true}""";
    private const string SuspendedFiveMinutesRule = """{"is_active":true,"for_minutes":5}""";
    private const string SuspendedModeFiveMinutesRule = """{"mode":"Suspended","is_active":true,"for_minutes":5}""";

    private void PumpIsFresh() =>
        _pumpSnapshotRepository.Setup(r => r.GetLatestAsync(It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DateTime? asOf, CancellationToken _) => new PumpSnapshot
            {
                Timestamp = (asOf ?? _timeProvider.GetUtcNow().UtcDateTime).AddMinutes(-1),
                BatteryPercent = 60,
            });

    private RustBackedAlertEngine EngineAtNow(params AlertRuleSnapshot[] rules)
    {
        var time = new ManualTimeProvider();
        time.SetUtcNow(_timeProvider.GetUtcNow().UtcDateTime);
        var stored = rules.Select(r => new AlertRule
        {
            Id = r.Id,
            Name = r.Name,
            ConditionType = r.ConditionType,
            ConditionParams = r.ConditionParams,
            OnUnknown = AlertRuleOnUnknown.Hold,
        });
        return EngineTestHarness.BuildRustEngine(time, new InMemoryTrackerRepository(stored));
    }

    private async Task<AlertEngineEvaluation> EvaluatedAsync(AlertRuleSnapshot rule, SensorContext context) =>
        await EngineAtNow(rule).EvaluateRuleAsync(rule, context, new AlertEngineOptions(), CancellationToken.None);

    private async Task<AlertEngineEvaluation> EvaluatedWithSpansAsync(
        AlertConditionType type, string conditionParams, params StateSpan[] spans)
    {
        PumpIsFresh();
        SpansStored(spans);
        var rule = MakeRule(type, conditionParams);
        var enriched = await BuildEnricher().EnrichAsync(BaseContext(), [rule], CancellationToken.None);
        return await EvaluatedAsync(rule, enriched);
    }

    private static StateSpan Closed(StateSpan span, DateTime end)
    {
        span.EndTimestamp = end;
        return span;
    }

    [NativeTheory]
    [InlineData(AlertConditionType.PumpSuspended, SuspendedRule)]
    [InlineData(AlertConditionType.PumpState, SuspendedModeRule)]
    public async Task An_open_suspension_a_fast_clock_dates_within_the_tolerance_fires(
        AlertConditionType type, string conditionParams)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;

        var evaluation = await EvaluatedWithSpansAsync(type, conditionParams, PumpSpan("Suspended", now.AddMinutes(2)));

        evaluation.ConditionMet.Should().BeTrue();
        evaluation.ConditionUnknown.Should().BeFalse();
        evaluation.ClockAnomalies.Should().BeEmpty();
    }

    [NativeTheory]
    [InlineData(AlertConditionType.PumpSuspended, SuspendedRule)]
    [InlineData(AlertConditionType.PumpSuspended, SuspendedFiveMinutesRule)]
    [InlineData(AlertConditionType.PumpState, SuspendedModeRule)]
    [InlineData(AlertConditionType.PumpState, SuspendedModeFiveMinutesRule)]
    public async Task A_suspension_dated_beyond_the_tolerance_is_not_sent_and_judges_nothing(
        AlertConditionType type, string conditionParams)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;

        var evaluation = await EvaluatedWithSpansAsync(type, conditionParams, PumpSpan("Suspended", now.AddMinutes(20)));

        evaluation.ConditionMet.Should().BeFalse();
        evaluation.ConditionUnknown.Should().BeFalse();
        evaluation.ClockAnomalies.Should().BeEmpty();
    }

    [NativeFact]
    public async Task A_suspension_open_a_year_ahead_does_not_make_a_pump_that_is_not_suspended_read_suspended()
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;

        var evaluation = await EvaluatedWithSpansAsync(
            AlertConditionType.PumpSuspended, SuspendedRule, PumpSpan("Suspended", now.AddDays(365)));

        evaluation.ConditionMet.Should().BeFalse();
    }

    [NativeFact]
    public async Task An_automatic_mode_open_a_year_ahead_does_not_hide_the_manual_mode_the_pump_is_in()
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;

        var evaluation = await EvaluatedWithSpansAsync(
            AlertConditionType.PumpState, """{"mode":"Automatic","is_active":false}""",
            PumpSpan("Manual", now.AddMinutes(-60)), PumpSpan("Automatic", now.AddDays(365)));

        evaluation.ConditionMet.Should().BeTrue("the pump is not in Automatic now");
    }

    [NativeTheory]
    [InlineData(2)]
    [InlineData(200 * 24 * 60)]
    public async Task A_suspension_that_ended_ahead_of_the_clock_is_not_sent(int minutesAhead)
    {
        var start = _timeProvider.GetUtcNow().UtcDateTime.AddMinutes(minutesAhead);

        var evaluation = await EvaluatedWithSpansAsync(
            AlertConditionType.PumpSuspended, SuspendedRule, Closed(PumpSpan("Suspended", start), start.AddMinutes(30)));

        evaluation.ConditionMet.Should().BeFalse();
    }

    [NativeFact]
    public async Task A_suspension_that_runs_on_into_a_re_suspend_dated_ahead_is_one_suspension_to_both_leaves()
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var spans = new[]
        {
            Closed(PumpSpan("Suspended", now.AddMinutes(-30)), now.AddMinutes(3)),
            PumpSpan("Suspended", now.AddMinutes(3)),
        };

        var mode = await EvaluatedWithSpansAsync(
            AlertConditionType.PumpState, """{"mode":"Suspended","is_active":true,"for_minutes":15}""", spans);
        var suspended = await EvaluatedWithSpansAsync(
            AlertConditionType.PumpSuspended, """{"is_active":true,"for_minutes":15}""", spans);

        mode.ConditionMet.Should().BeTrue();
        suspended.ConditionMet.Should().BeTrue();
    }

    [Fact]
    public async Task The_spans_open_now_and_the_ones_dated_ahead_are_read_apart_so_ahead_rows_cannot_crowd_the_cap()
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        PumpIsFresh();
        SpansStored(PumpSpan("Suspended", now.AddMinutes(-10)));

        await BuildEnricher().EnrichAsync(
            BaseContext(), [MakeRule(AlertConditionType.PumpSuspended, SuspendedRule)], CancellationToken.None);

        PumpModeReads(Times.Once());
    }

    [NativeFact]
    public async Task A_span_of_another_category_dated_ahead_is_not_sent_and_raises_no_clock_anomaly()
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        StateSpan Planned(StateSpanCategory category) => new()
        {
            Category = category,
            State = "Planned",
            StartTimestamp = now.AddMinutes(20),
        };
        SpansStored(Planned(StateSpanCategory.Override), Planned(StateSpanCategory.TemporaryTarget), Planned(StateSpanCategory.Exercise));
        var rules = new[]
        {
            MakeRule(AlertConditionType.OverrideActive, SuspendedRule),
            MakeRule(AlertConditionType.StateSpanActive, """{"category":"TemporaryTarget","state":"Planned","is_active":true}"""),
            MakeRule(AlertConditionType.StateSpanActive, """{"category":"Exercise","is_active":true}"""),
        };

        var enriched = await BuildEnricher().EnrichAsync(BaseContext(), rules, CancellationToken.None);

        enriched.ActiveOverride.Should().BeNull();
        enriched.ActiveStateSpans.Should().BeEmpty();
        foreach (var rule in rules)
        {
            var evaluation = await EvaluatedAsync(rule, enriched);
            evaluation.ConditionUnknown.Should().BeFalse();
            evaluation.ClockAnomalies.Should().BeEmpty();
        }
        PumpModeReads(Times.Never());
    }

    private static StateSpan OverrideSpan(DateTime start, string? collection) => new()
    {
        Category = StateSpanCategory.Override,
        State = nameof(OverrideState.Custom),
        StartTimestamp = start,
        Metadata = collection is null
            ? null
            : new Dictionary<string, object> { [StateSpanMetadataExtensions.CollectionKey] = collection },
    };

    private async Task<(SensorContext Context, AlertEngineEvaluation Evaluation)> OverrideRuleOverAsync(
        params StateSpan[] spans)
    {
        SpansStored(spans);
        var rule = MakeRule(AlertConditionType.OverrideActive, SuspendedRule);
        var enriched = await BuildEnricher().EnrichAsync(BaseContext(), [rule], CancellationToken.None);
        return (enriched, await EvaluatedAsync(rule, enriched));
    }

    [NativeFact]
    public async Task An_override_a_device_status_dated_two_minutes_ahead_reaches_an_override_active_rule()
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;

        var (context, evaluation) = await OverrideRuleOverAsync(
            OverrideSpan(now.AddMinutes(2), StateSpanMetadataExtensions.DeviceStatusCollection));

        context.ActiveOverride!.StartedAt.Should().Be(now.AddMinutes(2));
        evaluation.ConditionMet.Should().BeTrue();
        evaluation.ClockAnomalies.Should().BeEmpty();
    }

    [NativeFact]
    public async Task A_device_status_override_with_a_duration_dated_two_minutes_ahead_reaches_an_override_active_rule()
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var span = OverrideSpan(now.AddMinutes(2), StateSpanMetadataExtensions.DeviceStatusCollection);
        span.EndTimestamp = now.AddMinutes(62);

        var (context, evaluation) = await OverrideRuleOverAsync(span);

        context.ActiveOverride!.StartedAt.Should().Be(now.AddMinutes(2));
        evaluation.ConditionMet.Should().BeTrue();
    }

    [Fact]
    public async Task A_device_status_override_that_ends_before_it_starts_is_not_sent()
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var span = OverrideSpan(now.AddMinutes(2), StateSpanMetadataExtensions.DeviceStatusCollection);
        span.EndTimestamp = now.AddMinutes(1);

        var (context, _) = await OverrideRuleOverAsync(span);

        context.ActiveOverride.Should().BeNull();
    }

    [NativeTheory]
    [InlineData(null)]
    [InlineData(StateSpanMetadataExtensions.TreatmentsCollection)]
    public async Task An_override_planned_two_minutes_ahead_is_held_back_whatever_wrote_it_but_a_device_status(
        string? collection)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;

        var (context, evaluation) = await OverrideRuleOverAsync(OverrideSpan(now.AddMinutes(2), collection));

        context.ActiveOverride.Should().BeNull();
        evaluation.ConditionMet.Should().BeFalse();
    }

    [Fact]
    public async Task A_device_status_override_dated_beyond_the_tolerance_is_not_sent()
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;

        var (context, _) = await OverrideRuleOverAsync(
            OverrideSpan(now.AddMinutes(20), StateSpanMetadataExtensions.DeviceStatusCollection));

        context.ActiveOverride.Should().BeNull();
    }

    [Fact]
    public async Task A_device_status_override_dated_beyond_the_tolerance_is_reported_at_its_start()
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;

        var (context, _) = await OverrideRuleOverAsync(
            OverrideSpan(now.AddMinutes(20), StateSpanMetadataExtensions.DeviceStatusCollection));

        context.OverridesRead.Should().Be(new SpansRead(now, now.AddMinutes(20)));
    }

    [Fact]
    public async Task A_finished_device_status_override_dated_beyond_the_tolerance_is_reported_at_its_start()
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var span = OverrideSpan(now.AddMinutes(20), StateSpanMetadataExtensions.DeviceStatusCollection);
        span.EndTimestamp = now.AddMinutes(80);

        var (context, _) = await OverrideRuleOverAsync(span);

        context.ActiveOverride.Should().BeNull();
        context.OverridesRead.Should().Be(new SpansRead(now, now.AddMinutes(20)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(StateSpanMetadataExtensions.TreatmentsCollection)]
    public async Task An_override_planned_beyond_the_tolerance_is_not_reported_as_a_device_clock(string? collection)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;

        var (context, _) = await OverrideRuleOverAsync(OverrideSpan(now.AddMinutes(20), collection));

        context.OverridesRead.Should().Be(new SpansRead(now, null));
    }

    [Fact]
    public async Task A_device_status_override_ahead_is_reported_while_another_override_is_in_force()
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;

        var (context, _) = await OverrideRuleOverAsync(
            OverrideSpan(now.AddMinutes(-30), null),
            OverrideSpan(now.AddMinutes(20), StateSpanMetadataExtensions.DeviceStatusCollection));

        context.ActiveOverride!.StartedAt.Should().Be(now.AddMinutes(-30));
        context.OverridesRead.Should().Be(new SpansRead(now, now.AddMinutes(20)));
    }

    [Fact]
    public async Task A_pass_that_needs_no_override_reads_no_override_and_reports_none()
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        SpansStored(OverrideSpan(now.AddMinutes(20), StateSpanMetadataExtensions.DeviceStatusCollection));

        var enriched = await BuildEnricher().EnrichAsync(
            BaseContext(), [MakeRule(AlertConditionType.PumpState, SuspendedModeRule)], CancellationToken.None);

        enriched.OverridesRead.Should().BeNull();
        _stateSpanService.Verify(s => s.GetAllCopiesAsync(
            StateSpanCategory.Override, It.IsAny<DateTime>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Never());
    }

    [Fact]
    public async Task A_replay_tick_reads_no_override_copies_and_reports_none()
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        SpansStored(OverrideSpan(now.AddMinutes(20), StateSpanMetadataExtensions.DeviceStatusCollection));
        var rule = MakeRule(AlertConditionType.OverrideActive, SuspendedRule);

        var enriched = await BuildEnricher().EnrichAsOfAsync(BaseContext(), [rule], now, CancellationToken.None);

        enriched.OverridesRead.Should().BeNull();
        _stateSpanService.Verify(s => s.GetAllCopiesAsync(
            StateSpanCategory.Override, It.IsAny<DateTime>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Never());
    }

    [NativeFact]
    public async Task A_device_status_override_whose_copy_deduplication_hid_behind_a_treatment_one_is_still_sent()
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var treatment = OverrideSpan(now.AddMinutes(2), StateSpanMetadataExtensions.TreatmentsCollection);
        var deviceStatus = OverrideSpan(now.AddMinutes(2).AddSeconds(5), StateSpanMetadataExtensions.DeviceStatusCollection);
        SpansStored(treatment);
        _stateSpanService.Setup(s => s.GetAllCopiesAsync(
                StateSpanCategory.Override, It.IsAny<DateTime>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([treatment, deviceStatus]);
        var rule = MakeRule(AlertConditionType.OverrideActive, SuspendedRule);

        var enriched = await BuildEnricher().EnrichAsync(BaseContext(), [rule], CancellationToken.None);

        enriched.ActiveOverride!.StartedAt.Should().Be(deviceStatus.StartTimestamp);
        (await EvaluatedAsync(rule, enriched)).ConditionMet.Should().BeTrue();
    }

    [Fact]
    public async Task A_treatment_override_whose_device_status_copy_does_not_exist_stays_a_plan_among_every_copy()
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;

        var (context, _) = await OverrideRuleOverAsync(
            OverrideSpan(now.AddMinutes(2), StateSpanMetadataExtensions.TreatmentsCollection));

        context.ActiveOverride.Should().BeNull();
    }

    [Fact]
    public async Task Spans_beyond_the_bound_never_push_the_one_within_it_out_of_the_page_that_is_sent()
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        PumpIsFresh();
        var crowd = Enumerable.Range(0, 105).Select(i => PumpSpan("Suspended", now.AddMinutes(10 + i)));
        SpansStored([.. crowd, PumpSpan("Manual", now.AddMinutes(2))]);

        var enriched = await BuildEnricher().EnrichAsync(
            BaseContext(), [MakeRule(AlertConditionType.PumpState, SuspendedModeRule)], CancellationToken.None);

        enriched.ActivePumpModes.Should().Equal(new PumpModeSnapshot("Manual", now.AddMinutes(2)));
        enriched.PumpModesRead.Should().Be(new SpansRead(now, now.AddMinutes(114)));
    }

    [Fact]
    public async Task A_device_status_override_ahead_does_not_replace_an_override_in_force()
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;

        var (context, _) = await OverrideRuleOverAsync(
            OverrideSpan(now.AddMinutes(-30), null),
            OverrideSpan(now.AddMinutes(2), StateSpanMetadataExtensions.DeviceStatusCollection));

        context.ActiveOverride!.StartedAt.Should().Be(now.AddMinutes(-30));
    }

    [Fact]
    public async Task The_soonest_device_status_override_ahead_is_sent()
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;

        var (context, _) = await OverrideRuleOverAsync(
            OverrideSpan(now.AddMinutes(4), StateSpanMetadataExtensions.DeviceStatusCollection),
            OverrideSpan(now.AddMinutes(1), null),
            OverrideSpan(now.AddMinutes(3), StateSpanMetadataExtensions.DeviceStatusCollection));

        context.ActiveOverride!.StartedAt.Should().Be(now.AddMinutes(3));
    }

    [Fact]
    public async Task A_replay_tick_does_not_see_a_device_status_override_dated_ahead_of_it()
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        SpansStored(OverrideSpan(now.AddMinutes(2), StateSpanMetadataExtensions.DeviceStatusCollection));
        var rule = MakeRule(AlertConditionType.OverrideActive, SuspendedRule);

        var enriched = await BuildEnricher().EnrichAsOfAsync(BaseContext(), [rule], now, CancellationToken.None);

        enriched.ActiveOverride.Should().BeNull();
    }

    [Fact]
    public async Task A_pass_that_reads_pump_modes_reports_the_newest_open_start_even_when_it_sends_none_of_it()
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        SpansStored(PumpSpan("Automatic", now.AddMinutes(-60)), PumpSpan("Suspended", now.AddMinutes(20)));

        var enriched = await BuildEnricher().EnrichAsync(
            BaseContext(), [MakeRule(AlertConditionType.PumpState, SuspendedModeRule)], CancellationToken.None);

        enriched.PumpModesRead.Should().Be(new SpansRead(now, now.AddMinutes(20)));
        enriched.ActivePumpModes.Should().Equal(new PumpModeSnapshot("Automatic", now.AddMinutes(-60)));
    }

    [Fact]
    public async Task A_closed_pump_mode_span_starting_beyond_the_tolerance_is_reported_and_not_sent()
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var finished = PumpSpan("Suspended", now.AddMinutes(20));
        finished.EndTimestamp = now.AddMinutes(50);
        SpansStored(PumpSpan("Automatic", now.AddMinutes(-60)), finished);

        var enriched = await BuildEnricher().EnrichAsync(
            BaseContext(), [MakeRule(AlertConditionType.PumpState, SuspendedModeRule)], CancellationToken.None);

        enriched.PumpModesRead.Should().Be(new SpansRead(now, now.AddMinutes(20)));
        enriched.ActivePumpModes.Should().Equal(new PumpModeSnapshot("Automatic", now.AddMinutes(-60)));
    }

    [Fact]
    public async Task A_closed_pump_mode_span_starting_within_the_tolerance_is_reported_at_its_start_and_not_sent()
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var finished = PumpSpan("Suspended", now.AddMinutes(3));
        finished.EndTimestamp = now.AddMinutes(30);
        SpansStored(finished);

        var enriched = await BuildEnricher().EnrichAsync(
            BaseContext(), [MakeRule(AlertConditionType.PumpState, SuspendedModeRule)], CancellationToken.None);

        enriched.PumpModesRead.Should().Be(new SpansRead(now, now.AddMinutes(3)));
        enriched.ActivePumpModes.Should().BeEmpty();
    }

    [Fact]
    public async Task A_pass_that_reads_pump_modes_and_finds_none_open_says_so()
    {
        SpansStored();

        var enriched = await BuildEnricher().EnrichAsync(
            BaseContext(), [MakeRule(AlertConditionType.PumpState, SuspendedModeRule)], CancellationToken.None);

        enriched.PumpModesRead.Should().Be(new SpansRead(_timeProvider.GetUtcNow().UtcDateTime, null));
    }

    [Fact]
    public async Task A_pass_that_needs_no_pump_mode_reads_none_and_reports_none()
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        SpansStored(PumpSpan("Suspended", now.AddMinutes(20)));

        var enriched = await BuildEnricher().EnrichAsync(
            BaseContext(), [MakeRule(AlertConditionType.OverrideActive, SuspendedRule)], CancellationToken.None);

        enriched.PumpModesRead.Should().BeNull();
        PumpModeReads(Times.Never());
    }

    [Fact]
    public async Task A_replay_tick_reports_no_pump_mode_read()
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        SpansStored(PumpSpan("Suspended", now.AddMinutes(20)));

        var enriched = await BuildEnricher().EnrichAsOfAsync(
            BaseContext(), [MakeRule(AlertConditionType.PumpState, SuspendedModeRule)], now, CancellationToken.None);

        enriched.PumpModesRead.Should().BeNull();
    }

    [Fact]
    public async Task A_replay_tick_before_a_pump_span_starts_does_not_see_it()
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        PumpIsFresh();
        SpansStored(PumpSpan("Suspended", now.AddMinutes(2)));
        var rules = new[]
        {
            MakeRule(AlertConditionType.PumpSuspended, SuspendedRule),
            MakeRule(AlertConditionType.PumpState, SuspendedModeRule),
        };

        var before = await BuildEnricher().EnrichAsOfAsync(BaseContext(), rules, now, CancellationToken.None);
        var after = await BuildEnricher().EnrichAsOfAsync(BaseContext(), rules, now.AddMinutes(3), CancellationToken.None);

        before.ActivePumpSuspension.Should().BeNull();
        before.ActivePumpModes.Should().BeEmpty();
        after.ActivePumpSuspension.Should().NotBeNull();
        after.ActivePumpModes.Should().ContainSingle();
        PumpModeReads(Times.Once(), at: now, replay: true);
        PumpModeReads(Times.Once(), at: now.AddMinutes(3), replay: true);
    }

    [Fact]
    public async Task One_read_of_the_pump_modes_serves_every_fact_that_needs_them()
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        PumpIsFresh();
        SpansStored(PumpSpan("Suspended", now.AddMinutes(2)));

        await BuildEnricher().EnrichAsync(
            BaseContext(),
            [
                MakeRule(AlertConditionType.PumpSuspended, SuspendedRule),
                MakeRule(AlertConditionType.PumpState, SuspendedModeRule),
            ],
            CancellationToken.None);

        PumpModeReads(Times.Once());
    }

    [Fact]
    public async Task A_mode_open_now_is_listed_without_the_span_of_its_own_dated_ahead()
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        PumpIsFresh();
        SpansStored(PumpSpan("Suspended", now.AddMinutes(2)), PumpSpan("Suspended", now.AddMinutes(-10)));
        var rules = new[]
        {
            MakeRule(AlertConditionType.PumpSuspended, SuspendedRule),
            MakeRule(AlertConditionType.PumpState, SuspendedModeRule),
        };

        var enriched = await BuildEnricher().EnrichAsync(BaseContext(), rules, CancellationToken.None);

        enriched.ActivePumpSuspension!.StartedAt.Should().Be(now.AddMinutes(-10));
        enriched.ActivePumpModes.Select(m => m.StartedAt).Should().Equal(now.AddMinutes(-10));
    }

    [Fact]
    public async Task A_mode_not_open_now_is_listed_by_its_soonest_span_dated_within_the_tolerance()
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        PumpIsFresh();
        SpansStored(
            PumpSpan("Automatic", now.AddMinutes(-60)),
            PumpSpan("Suspended", now.AddMinutes(4)),
            PumpSpan("Suspended", now.AddMinutes(2)));

        var enriched = await BuildEnricher().EnrichAsync(
            BaseContext(), [MakeRule(AlertConditionType.PumpState, SuspendedModeRule)], CancellationToken.None);

        enriched.ActivePumpModes.Should().Equal(
            new PumpModeSnapshot("Automatic", now.AddMinutes(-60)),
            new PumpModeSnapshot("Suspended", now.AddMinutes(2)));
    }

    [Fact]
    public async Task Stale_pump_snapshot_nulls_active_suspension_projection()
    {
        var enricher = BuildEnricher();
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        // 11 minutes is past the configured PumpFreshnessThreshold (default 10 minutes).
        _pumpSnapshotRepository.Setup(r => r.GetLatestAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PumpSnapshot { Timestamp = now.AddMinutes(-11), BatteryPercent = 50 });
        SpansStored(PumpSpan(PumpModeState.Suspended.ToString(), now.AddMinutes(-30)));
        var rule = MakeRule(AlertConditionType.PumpSuspended, """{"is_active":true}""");

        var enriched = await enricher.EnrichAsync(BaseContext(), new[] { rule }, CancellationToken.None);

        enriched.HasEverPumpSnapshot.Should().BeTrue();
        enriched.ActivePumpSuspension.Should().BeNull();
        // State span should not even be queried when the pump snapshot is stale.
        PumpModeReads(Times.Never());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Active")]
    [InlineData("Night")]
    public async Task Profile_state_span_leaf_reads_the_active_profile_activation(string? state)
    {
        var enricher = BuildEnricher();
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var activation = new ProfileActivation
        {
            Id = Guid.NewGuid(),
            StartTimestamp = now.AddMinutes(-40),
            BaseProfileName = "Night",
            Phase = ProfileActivationPhase.Requested,
            Origin = ProfileActivationOrigin.Uploader,
        };
        _profileActivations.Setup(r => r.GetAtAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResolvedProfileActivation(
                activation, "Night", ProfileScheduleSource.ProfileStore, null, null));
        var stateJson = state is null ? "" : $",\"state\":\"{state}\"";
        var rule = MakeRule(AlertConditionType.StateSpanActive,
            $"{{\"category\":\"Profile\"{stateJson},\"is_active\":true}}");

        var enriched = await enricher.EnrichAsync(BaseContext(), new[] { rule }, CancellationToken.None);

        enriched.ActiveStateSpans.Should().ContainKey((StateSpanCategory.Profile, state));
        enriched.ActiveStateSpans[(StateSpanCategory.Profile, state)].StartedAt.Should().Be(now.AddMinutes(-40));
        _stateSpanService.Verify(s => s.GetActiveAtAsync(
            StateSpanCategory.Profile, It.IsAny<string?>(),
            It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Profile_state_span_leaf_naming_another_profile_is_not_active()
    {
        var enricher = BuildEnricher();
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        _profileActivations.Setup(r => r.GetAtAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResolvedProfileActivation(
                new ProfileActivation { Id = Guid.NewGuid(), StartTimestamp = now.AddMinutes(-40), BaseProfileName = "Night" },
                "Night", ProfileScheduleSource.ProfileStore, null, null));
        var rule = MakeRule(AlertConditionType.StateSpanActive,
            """{"category":"Profile","state":"Exercise","is_active":true}""");

        var enriched = await enricher.EnrichAsync(BaseContext(), new[] { rule }, CancellationToken.None);

        enriched.ActiveStateSpans.Should().NotContainKey((StateSpanCategory.Profile, "Exercise"));
    }

    [Fact]
    public async Task OverrideActive_need_projects_the_start_of_the_override_in_force()
    {
        var enricher = BuildEnricher();
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        SpansStored(new StateSpan
        {
            Category = StateSpanCategory.Override,
            StartTimestamp = now.AddMinutes(-15),
            EndTimestamp = now.AddMinutes(45),
        });
        var rule = MakeRule(AlertConditionType.OverrideActive, """{"is_active":true}""");

        var enriched = await enricher.EnrichAsync(BaseContext(), new[] { rule }, CancellationToken.None);

        enriched.ActiveOverride.Should().Be(new OverrideSnapshot(now.AddMinutes(-15)));
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

        var enriched = await enricher.EnrichAsync(BaseContext(), new[] { rule }, CancellationToken.None);

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

        var enriched1 = await enricher1.EnrichAsync(BaseContext(), new[] { rule }, CancellationToken.None);

        enriched1.SensitivityRatio.Should().Be(0.85m);
        enriched1.HasEverApsSensitivity.Should().BeTrue();

        // Case 2: repo returns null → HasEverApsSensitivity stays false.
        var apsRepo2 = new Mock<IApsSnapshotRepository>();
        apsRepo2.Setup(r => r.GetLatestSensitivityRatioAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync((decimal?)null);
        var deps2 = new SensorContextEnricherDependencies(
            new IobCobReader(_iobCalculator.Object, _cobCalculator.Object, _bolusRepository.Object,
                _tempBasalRepository.Object, _carbIntakeRepository.Object, Mock.Of<ITherapyTimelineResolver>(), Mock.Of<IStateSpanRepository>(), Mock.Of<IApsSnapshotRepository>(), Mock.Of<IPumpSnapshotRepository>(),
                TestTenantCache.Create(partition: null),
                NullLogger<IobCobReader>.Instance),
            _iobCalculator.Object, _cobCalculator.Object, _treatmentService.Object,
            _deviceEventRepository.Object, _pumpSnapshotRepository.Object,
            apsRepo2.Object, _tempBasalRepository.Object, _uploaderSnapshotRepository.Object,
            _stateSpanService.Object, _alertRepository.Object, _alertSettings.Object, _dndWindows.Object,
            _targetRangeScheduleRepository.Object,
            _activeProfileResolver.Object,
            _profileActivations.Object,
            _therapySettingsResolver.Object,
            _sleepService.Object,
            _trackerRepository.Object,
            _reservoirEstimator.Object,
            _sensorStates.Object,
            Options.Create(new AlertEvaluationOptions()));
        var enricher2 = new SensorContextEnricher(
            deps2,
            new ServiceCollection().BuildServiceProvider(),
            _timeProvider, new NullLogger<SensorContextEnricher>());

        var enriched2 = await enricher2.EnrichAsync(BaseContext(), new[] { rule }, CancellationToken.None);

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

        var enriched = await enricher.EnrichAsync(BaseContext(), new[] { rule }, CancellationToken.None);

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

        await enricher.EnrichAsync(BaseContext(), new[] { rule }, CancellationToken.None);

        _apsSnapshotRepository.Verify(r => r.GetLatestTimestampAsOfAsync(It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Never);
        _apsSnapshotRepository.Verify(r => r.GetLatestEnactedTimestampAsync(It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Never);
        _apsSnapshotRepository.Verify(r => r.GetLatestSensitivityRatioAsync(It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Never);
        _pumpSnapshotRepository.Verify(r => r.GetLatestAsync(It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Never);
        _tempBasalRepository.Verify(r => r.GetActiveAtAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
        _uploaderSnapshotRepository.Verify(r => r.GetLatestAsync(It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Never);
        _stateSpanService.Verify(s => s.GetActiveAtAsync(
            It.IsAny<StateSpanCategory>(), It.IsAny<string?>(),
            It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
        PumpModeReads(Times.Never());
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
        SpansStored(PumpSpan(PumpModeState.Suspended.ToString(), now.AddMinutes(-15)));
        var rule = MakeRule(AlertConditionType.PumpSuspended, """{"is_active":true}""");

        var enriched = await enricher.EnrichAsync(BaseContext(), new[] { rule }, CancellationToken.None);

        enriched.ActivePumpSuspension.Should().NotBeNull();
        enriched.ActivePumpSuspension!.StartedAt.Should().Be(now.AddMinutes(-15));
    }

    [Fact]
    public async Task TransmitterBattery_need_reads_the_latest_transmitter_status_however_old()
    {
        var enricher = BuildEnricher();
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        _uploaderSnapshotRepository
            .Setup(r => r.GetLatestOfTypeAsync(TransmitterStatus.UploaderType, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UploaderSnapshot
            {
                Timestamp = now.AddDays(-9),
                Type = TransmitterStatus.UploaderType,
                AdditionalProperties = new()
                {
                    ["voltagea"] = System.Text.Json.JsonSerializer.SerializeToElement(288),
                    ["voltageb"] = System.Text.Json.JsonSerializer.SerializeToElement(271),
                    ["days"] = System.Text.Json.JsonSerializer.SerializeToElement(95),
                },
            });
        var rule = MakeRule(AlertConditionType.TransmitterBattery,
            """{"metric":"voltage_a","operator":"<","value":290}""");

        var enriched = await enricher.EnrichAsync(BaseContext(), new[] { rule }, CancellationToken.None);

        enriched.TransmitterVoltageA.Should().Be(288m);
        enriched.TransmitterVoltageB.Should().Be(271m);
        enriched.TransmitterResistance.Should().BeNull();
        enriched.TransmitterDays.Should().Be(95m);
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

        var enriched = await enricher.EnrichAsync(BaseContext(), new[] { rule }, CancellationToken.None);

        enriched.ActiveTempBasal.Should().NotBeNull();
        enriched.ActiveTempBasal!.Rate.Should().Be(1.0m);
        enriched.ActiveTempBasal.ScheduledRate.Should().BeNull();
    }

    [Fact]
    public async Task GlucoseBucket_resolution_picks_schedule_entry_in_tenant_local_time_not_utc()
    {
        // Sydney is UTC+11 in March (AEDT). At 19:00 UTC on 2026-03-21 the local wall clock
        // is 06:00 on 2026-03-22 in Sydney. The schedule below has two entries: a low/tight
        // band before noon (low=80) and a relaxed band from noon onward (low=70). A reading
        // of 75 mg/dL must resolve to the 06:00-Sydney-local entry (low=80 → Low bucket),
        // not the noon-local entry. Before the fix the resolver pulled the time-of-day from
        // the UTC clock — 19:00 UTC sits past 12:00 so the wrong entry was picked.
        var enricher = BuildEnricher();
        var tickUtc = new DateTime(2026, 3, 21, 19, 0, 0, DateTimeKind.Utc);
        var sydneyTz = TryResolve("Australia/Sydney", "AUS Eastern Standard Time");

        _therapySettingsResolver.Setup(s => s.GetTimezoneAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(sydneyTz);
        _activeProfileResolver.Setup(s => s.GetActiveProfileNameAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("Default");
        _targetRangeScheduleRepository.Setup(s => s.GetActiveAtAsync("Default", It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TargetRangeSchedule
            {
                ProfileName = "Default",
                Entries =
                [
                    new TargetRangeEntry { Time = "00:00", TimeAsSeconds = 0,     Low = 80, High = 180 },
                    new TargetRangeEntry { Time = "12:00", TimeAsSeconds = 43200, Low = 70, High = 180 },
                ],
            });

        var rule = MakeRule(AlertConditionType.GlucoseBucket, """{"buckets":["low"]}""");
        var baseContext = BaseContext() with { LatestValue = 75m };

        var enriched = await enricher.EnrichAsOfAsync(baseContext, new[] { rule }, tickUtc, CancellationToken.None);

        enriched.GlucoseBucket.Should().Be(Nocturne.Core.Models.Alerts.GlucoseBucket.Low);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GlucoseBucket_resolution_reads_the_entry_time_when_TimeAsSeconds_is_absent(bool withSeconds)
    {
        var enricher = BuildEnricher();
        var tickUtc = new DateTime(2026, 3, 22, 7, 0, 0, DateTimeKind.Utc);

        _activeProfileResolver.Setup(s => s.GetActiveProfileNameAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("Default");
        _targetRangeScheduleRepository.Setup(s => s.GetActiveAtAsync("Default", It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TargetRangeSchedule
            {
                ProfileName = "Default",
                Entries =
                [
                    new TargetRangeEntry { Time = "06:00", TimeAsSeconds = withSeconds ? 21600 : null, Low = 80, High = 180 },
                    new TargetRangeEntry { Time = "00:00", TimeAsSeconds = withSeconds ? 0 : null, Low = 70, High = 180 },
                ],
            });

        var rule = MakeRule(AlertConditionType.GlucoseBucket, """{"buckets":["low"]}""");
        var baseContext = BaseContext() with { LatestValue = 75m };

        var enriched = await enricher.EnrichAsOfAsync(baseContext, new[] { rule }, tickUtc, CancellationToken.None);

        enriched.GlucoseBucket.Should().Be(Nocturne.Core.Models.Alerts.GlucoseBucket.Low);
    }

    private void ScheduleWithMorningTarget()
    {
        _activeProfileResolver.Setup(s => s.GetActiveProfileNameAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("Default");
        _targetRangeScheduleRepository.Setup(s => s.GetActiveAtAsync("Default", It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TargetRangeSchedule
            {
                ProfileName = "Default",
                Entries =
                [
                    new TargetRangeEntry { Time = "00:00", TimeAsSeconds = 0,     Low = 70, High = 180 },
                    new TargetRangeEntry { Time = "06:00", TimeAsSeconds = 21600, Low = 80.5, High = 160 },
                ],
            });
    }

    /// <summary>
    /// The targets a limit can name are the entry the bucket is placed with, from one lookup, so a rule
    /// below target_low and the low bucket cannot disagree.
    /// </summary>
    [Fact]
    public async Task The_targets_are_the_entry_the_bucket_is_placed_with()
    {
        var enricher = BuildEnricher();
        ScheduleWithMorningTarget();
        var bucket = MakeRule(AlertConditionType.GlucoseBucket, """{"buckets":["low"]}""");
        var belowTarget = MakeRule(AlertConditionType.Threshold, """{"direction":"below","value_from":{"fact":"target_low"}}""");

        var enriched = await enricher.EnrichAsOfAsync(
            BaseContext() with { LatestValue = 75m }, new[] { bucket, belowTarget },
            new DateTime(2026, 3, 22, 7, 0, 0, DateTimeKind.Utc), CancellationToken.None);

        enriched.TargetLow.Should().Be(80.5m);
        enriched.TargetHigh.Should().Be(160m);
        enriched.GlucoseBucket.Should().Be(Nocturne.Core.Models.Alerts.GlucoseBucket.Low);
        _targetRangeScheduleRepository.Verify(
            s => s.GetActiveAtAsync("Default", It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>A replayed tick reads the entry in effect at that tick, so the target moves at the boundary.</summary>
    [Fact]
    public async Task A_tick_reads_the_target_in_effect_then_without_a_reading()
    {
        var enricher = BuildEnricher();
        ScheduleWithMorningTarget();
        var belowTarget = MakeRule(AlertConditionType.Threshold, """{"direction":"below","value_from":{"fact":"target_low"}}""");

        var before = await enricher.EnrichAsOfAsync(
            BaseContext(), new[] { belowTarget }, new DateTime(2026, 3, 22, 5, 59, 0, DateTimeKind.Utc), CancellationToken.None);
        var after = await enricher.EnrichAsOfAsync(
            BaseContext(), new[] { belowTarget }, new DateTime(2026, 3, 22, 6, 0, 0, DateTimeKind.Utc), CancellationToken.None);

        before.TargetLow.Should().Be(70m);
        after.TargetLow.Should().Be(80.5m);
    }

    /// <summary>
    /// With no target range the bucket keeps its defaults and the targets stay absent: a rule that
    /// names one waits rather than comparing with a default.
    /// </summary>
    [Fact]
    public async Task No_schedule_leaves_the_targets_absent_while_the_bucket_keeps_its_defaults()
    {
        var enricher = BuildEnricher();
        _activeProfileResolver.Setup(s => s.GetActiveProfileNameAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("Default");
        var bucket = MakeRule(AlertConditionType.GlucoseBucket, """{"buckets":["low"]}""");
        var belowTarget = MakeRule(AlertConditionType.Threshold, """{"direction":"below","value_from":{"fact":"target_low"}}""");

        var enriched = await enricher.EnrichAsync(
            BaseContext() with { LatestValue = 65m }, new[] { bucket, belowTarget }, CancellationToken.None);

        enriched.TargetLow.Should().BeNull();
        enriched.TargetHigh.Should().BeNull();
        enriched.GlucoseBucket.Should().Be(Nocturne.Core.Models.Alerts.GlucoseBucket.Low);
    }

    [Fact]
    public async Task A_rule_that_names_no_target_reads_no_target()
    {
        var enricher = BuildEnricher();
        ScheduleWithMorningTarget();
        var literal = MakeRule(AlertConditionType.Threshold, """{"direction":"below","value":70}""");

        var enriched = await enricher.EnrichAsync(
            BaseContext() with { LatestValue = 65m }, new[] { literal }, CancellationToken.None);

        enriched.TargetLow.Should().BeNull();
        _targetRangeScheduleRepository.Verify(
            s => s.GetActiveAtAsync(It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ---- scoped Do Not Disturb (ADR 0004 D5) ----

    [Fact]
    public async Task LowsWindow_setsLowsScope_butNotActiveDnd()
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        _dndWindows
            .Setup(r => r.GetUnexpiredSnapshotsAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DndWindowSnapshot>
            {
                new(DndScope.Lows, StartedAt: now.AddMinutes(-5), EndsAt: null, ClearedAt: null, CreatedAt: now.AddMinutes(-5)),
            });

        var enricher = BuildEnricher();
        var rule = MakeRule(AlertConditionType.Threshold, """{"direction":"below","value":70}""");

        var enriched = await enricher.EnrichAsync(BaseContext(), new[] { rule }, CancellationToken.None);

        enriched.ActiveDndScopes.Should().BeEquivalentTo(new[] { DndScope.Lows });
        // lows/highs windows feed the gate only — they never trip the do_not_disturb condition.
        enriched.ActiveDoNotDisturb.Should().BeNull();
    }

    [Fact]
    public async Task AllWindow_setsAllScope_andActiveDndAnchoredOnTheWindow()
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var startedAt = now.AddMinutes(-30);
        _dndWindows
            .Setup(r => r.GetUnexpiredSnapshotsAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DndWindowSnapshot>
            {
                new(DndScope.All, StartedAt: startedAt, EndsAt: null, ClearedAt: null, CreatedAt: startedAt),
            });

        var enricher = BuildEnricher();
        var rule = MakeRule(AlertConditionType.Threshold, """{"direction":"below","value":70}""");

        var enriched = await enricher.EnrichAsync(BaseContext(), new[] { rule }, CancellationToken.None);

        enriched.ActiveDndScopes.Should().BeEquivalentTo(new[] { DndScope.All });
        enriched.ActiveDoNotDisturb.Should().NotBeNull();
        enriched.ActiveDoNotDisturb!.StartedAt.Should().Be(startedAt);
        enriched.ActiveDoNotDisturb.Source.Should().Be("manual");
    }

    [Fact]
    public async Task ScheduledDnd_contributesAllScope_andTripsActiveDnd()
    {
        // FakeTimeProvider is 2026-03-22 12:00 UTC; a 00:00–23:00 schedule (UTC fallback, no tz)
        // is active, so scheduled DND is tenant-wide == scope=all.
        _alertSettings
            .Setup(r => r.GetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TenantAlertSettingsSnapshot(
                DndScheduleEnabled: true,
                DndScheduleStart: new TimeOnly(0, 0),
                DndScheduleEnd: new TimeOnly(23, 0)));

        var enricher = BuildEnricher();
        var rule = MakeRule(AlertConditionType.Threshold, """{"direction":"below","value":70}""");

        var enriched = await enricher.EnrichAsync(BaseContext(), new[] { rule }, CancellationToken.None);

        enriched.ActiveDndScopes.Should().Contain(DndScope.All);
        enriched.ActiveDoNotDisturb.Should().NotBeNull();
        enriched.ActiveDoNotDisturb!.Source.Should().Be("scheduled");
    }

    [Fact]
    public async Task ManualWindowAndScheduledDnd_bothActive_anchorsOnTheManualWindow()
    {
        // Pre-window contract (TenantAlertSettingsSnapshot.Resolve): the manual path takes
        // precedence for the for_minutes anchor and source when both are active.
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var manualStartedAt = now.AddMinutes(-90);
        _alertSettings
            .Setup(r => r.GetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TenantAlertSettingsSnapshot(
                DndScheduleEnabled: true,
                DndScheduleStart: new TimeOnly(0, 0),
                DndScheduleEnd: new TimeOnly(23, 0)));
        _dndWindows
            .Setup(r => r.GetUnexpiredSnapshotsAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DndWindowSnapshot>
            {
                new(DndScope.All, StartedAt: manualStartedAt, EndsAt: null, ClearedAt: null, CreatedAt: manualStartedAt),
            });

        var enricher = BuildEnricher();
        var rule = MakeRule(AlertConditionType.Threshold, """{"direction":"below","value":70}""");

        var enriched = await enricher.EnrichAsync(BaseContext(), new[] { rule }, CancellationToken.None);

        enriched.ActiveDndScopes.Should().Contain(DndScope.All);
        enriched.ActiveDoNotDisturb.Should().NotBeNull();
        enriched.ActiveDoNotDisturb!.StartedAt.Should().Be(manualStartedAt);
        enriched.ActiveDoNotDisturb.Source.Should().Be("manual");
    }

    private static readonly AlertRuleSnapshot ForecastRule =
        MakeRule(AlertConditionType.Predicted, """{"operator":"<","value":70,"within_minutes":30,"min_of":["iob","cob"]}""");

    private static IReadOnlyList<(int Offset, decimal Mgdl)> Points(IEnumerable<PredictedGlucosePoint> points) =>
        points.Select(p => (p.OffsetMinutes, p.Mgdl)).ToList();

    /// <summary>
    /// The default series is mapped exactly as it always was, and every named curve the source
    /// returns reaches the context under its name with the same offsets. An empty curve is absent.
    /// </summary>
    [Fact]
    public async Task Every_curve_the_source_returns_reaches_the_context_under_its_name()
    {
        _predictionService.Setup(p => p.GetPredictionsAsync(It.IsAny<string?>(), It.IsAny<DateTimeOffset?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GlucosePredictionResponse
            {
                IntervalMinutes = 5,
                Predictions = new PredictionCurves
                {
                    Default = [110, 120, 130],
                    IobOnly = [110, 100],
                    Cob = [140],
                    Uam = null,
                    ZeroTemp = [],
                },
            });

        var enriched = await BuildEnricher().EnrichAsync(BaseContext(), [ForecastRule], CancellationToken.None);

        Points(enriched.Predictions).Should().Equal((5, 110m), (10, 120m), (15, 130m));
        enriched.PredictionCurves.Keys.Should().BeEquivalentTo([ForecastCurve.Iob, ForecastCurve.Cob]);
        Points(enriched.PredictionCurves[ForecastCurve.Iob]).Should().Equal((5, 110m), (10, 100m));
        Points(enriched.PredictionCurves[ForecastCurve.Cob]).Should().Equal((5, 140m));
    }

    private Task<SensorContext> EnrichFromSnapshot(ApsSnapshot snapshot)
    {
        var snapshots = new Mock<IApsSnapshotRepository>();
        snapshots.Setup(r => r.GetAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([snapshot]);
        var source = new DeviceStatusPredictionService(
            snapshots.Object, _timeProvider, NullLogger<DeviceStatusPredictionService>.Instance);
        return BuildEnricher(predictionService: source).EnrichAsync(BaseContext(), [ForecastRule], CancellationToken.None);
    }

    private ApsSnapshot Snapshot(AidAlgorithm algorithm) => new()
    {
        Timestamp = _timeProvider.GetUtcNow().UtcDateTime.AddMinutes(-2),
        AidAlgorithm = algorithm,
    };

    /// <summary>
    /// The device-status source for an AAPS upload: the default column holds the IOB curve, as the
    /// decomposer stores it, and the four oref curves are sent by name beside it.
    /// </summary>
    [Fact]
    public async Task An_aaps_upload_sends_its_default_and_the_four_oref_curves()
    {
        var snapshot = Snapshot(AidAlgorithm.AndroidAps);
        snapshot.PredictedDefaultJson = "[100,95]";
        snapshot.PredictedIobJson = "[100,95]";
        snapshot.PredictedCobJson = "[110,120,130]";
        snapshot.PredictedUamJson = "[105]";
        snapshot.PredictedZtJson = "[90,80]";

        var enriched = await EnrichFromSnapshot(snapshot);

        Points(enriched.Predictions).Should().Equal((5, 100m), (10, 95m));
        enriched.PredictionCurves.Keys.Should().BeEquivalentTo(
            [ForecastCurve.Iob, ForecastCurve.Cob, ForecastCurve.Uam, ForecastCurve.Zt]);
        Points(enriched.PredictionCurves[ForecastCurve.Cob]).Should().Equal((5, 110m), (10, 120m), (15, 130m));
        Points(enriched.PredictionCurves[ForecastCurve.Zt]).Should().Equal((5, 90m), (10, 80m), (15, 80m));
        Points(enriched.PredictionCurves[ForecastCurve.Iob]).Should().Equal((5, 100m), (10, 95m));
    }

    private static readonly double[] TrimmedIob = Enumerable.Range(0, 24).Select(i => 118.0 - i).ToArray();
    private static readonly double[] TrimmedCob =
        [.. Enumerable.Range(0, 17).Select(i => 140.0 - 4 * i), 62, 64, 66, 68, 70, 72, 74];
    private static readonly double[] TrimmedUam = [110, 108, 106, 104, 102, 100, 99, 98, 97, 96, 95, 95, 95];
    private static readonly double[] TrimmedZt = [100, 95, 92, 90, 88, 86, 85, 86];

    private static string Json(IEnumerable<double> values) => System.Text.Json.JsonSerializer.Serialize(values);

    /// <summary>
    /// An oref upload as AAPS, Trio and OpenAPS send it: <c>predBGs</c> with UAM trimmed to its 13 level
    /// values and ZT cut at 8 where it turns up, beside full IOB and COB curves, stored by the
    /// decomposer's column filler. The host carries UAM and ZT at their last value to the longest
    /// curve, exactly the curves the corpus scenario <c>predicted-oref-trimmed-curves</c> evaluates,
    /// and leaves the default as it was.
    /// </summary>
    [Fact]
    public async Task An_oref_upload_has_its_trimmed_curves_carried_to_the_longest()
    {
        var command = System.Text.Json.JsonSerializer.Deserialize<OpenApsSuggested>($$"""
            {
              "timestamp": "2026-03-22T11:58:00Z",
              "bg": 120,
              "eventualBG": 74,
              "reason": "COB: 30, Dev: -12, BGI: -2.1, ISF: 50, CR: 10, minPredBG 62, minGuardBG 62, IOBpredBG 95, COBpredBG 74, UAMpredBG 95",
              "predBGs": { "IOB": {{Json(TrimmedIob)}}, "ZT": {{Json(TrimmedZt)}}, "COB": {{Json(TrimmedCob)}}, "UAM": {{Json(TrimmedUam)}} }
            }
            """)!;
        var snapshot = Snapshot(AidAlgorithm.Trio);
        ApsSnapshotCurves.FillColumns(snapshot, command);
        snapshot.PredictedDefaultJson = Json(TrimmedIob);

        var enriched = await EnrichFromSnapshot(snapshot);

        double[] Held(double[] curve) => [.. curve, .. Enumerable.Repeat(curve[^1], 24 - curve.Length)];
        Points(enriched.Predictions).Should().Equal(Offsets(TrimmedIob));
        Points(enriched.PredictionCurves[ForecastCurve.Iob]).Should().Equal(Offsets(TrimmedIob));
        Points(enriched.PredictionCurves[ForecastCurve.Cob]).Should().Equal(Offsets(TrimmedCob));
        Points(enriched.PredictionCurves[ForecastCurve.Uam]).Should().Equal(Offsets(Held(TrimmedUam)));
        Points(enriched.PredictionCurves[ForecastCurve.Zt]).Should().Equal(Offsets(Held(TrimmedZt)));
    }

    private static IReadOnlyList<(int Offset, decimal Mgdl)> Offsets(double[] curve) =>
        curve.Select((v, i) => ((i + 1) * 5, v.ToDecimal())).ToList();

    /// <summary>
    /// The default is never extended, and an IOB, COB or UAM curve shorter than a trimmed one can be
    /// simply ends: only ZT is carried at any length.
    /// </summary>
    [Fact]
    public async Task Neither_the_default_nor_a_curve_too_short_to_be_trimmed_is_extended()
    {
        _predictionService.Setup(p => p.GetPredictionsAsync(It.IsAny<string?>(), It.IsAny<DateTimeOffset?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GlucosePredictionResponse
            {
                IntervalMinutes = 5,
                Algorithm = AidAlgorithm.AndroidAps,
                Predictions = new PredictionCurves
                {
                    Default = [100, 95],
                    IobOnly = [100, 95],
                    Cob = [.. TrimmedCob],
                    Uam = [110, 105, 100],
                    ZeroTemp = [90, 88],
                },
            });

        var enriched = await BuildEnricher().EnrichAsync(BaseContext(), [ForecastRule], CancellationToken.None);

        Points(enriched.Predictions).Should().Equal((5, 100m), (10, 95m));
        enriched.PredictionCurves[ForecastCurve.Iob].Should().HaveCount(2);
        enriched.PredictionCurves[ForecastCurve.Uam].Should().HaveCount(3);
        enriched.PredictionCurves[ForecastCurve.Zt].Should().HaveCount(24);
    }

    /// <summary>
    /// A level-trimmed COB or IOB curve (13 or more values ending flat) that ends before a longer
    /// named curve is held at its last value out to the longest, as COB is once a meal is absorbed
    /// while UAM runs on.
    /// </summary>
    [Theory]
    [InlineData(ForecastCurve.Cob)]
    [InlineData(ForecastCurve.Iob)]
    public async Task A_level_trimmed_curve_shorter_than_another_is_held_to_the_longest(ForecastCurve trimmed)
    {
        double[] levelled = [.. Enumerable.Range(0, 13).Select(i => 150.0 - 5 * i), 90, 90];
        double[] full = [.. Enumerable.Range(0, 24).Select(i => 130.0 - i)];
        _predictionService.Setup(p => p.GetPredictionsAsync(It.IsAny<string?>(), It.IsAny<DateTimeOffset?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GlucosePredictionResponse
            {
                IntervalMinutes = 5,
                Algorithm = AidAlgorithm.Trio,
                Predictions = new PredictionCurves
                {
                    Default = [.. full],
                    IobOnly = trimmed == ForecastCurve.Iob ? [.. levelled] : [.. full],
                    Cob = trimmed == ForecastCurve.Cob ? [.. levelled] : [.. full],
                    Uam = [.. full],
                },
            });

        var enriched = await BuildEnricher().EnrichAsync(BaseContext(), [ForecastRule], CancellationToken.None);

        double[] held = [.. levelled, .. Enumerable.Repeat(90.0, 24 - levelled.Length)];
        Points(enriched.PredictionCurves[trimmed]).Should().Equal(Offsets(held));
        Points(enriched.Predictions).Should().Equal(Offsets(full));
    }

    /// <summary>Only an oref source has its curves carried; a source of another or no algorithm is read as sent.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData(AidAlgorithm.Loop)]
    public async Task A_source_that_is_not_oref_sends_its_curves_as_they_are(AidAlgorithm? algorithm)
    {
        _predictionService.Setup(p => p.GetPredictionsAsync(It.IsAny<string?>(), It.IsAny<DateTimeOffset?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GlucosePredictionResponse
            {
                IntervalMinutes = 5,
                Algorithm = algorithm,
                Predictions = new PredictionCurves { Default = [100], Cob = [.. TrimmedCob], ZeroTemp = [90, 88] },
            });

        var enriched = await BuildEnricher().EnrichAsync(BaseContext(), [ForecastRule], CancellationToken.None);

        enriched.PredictionCurves[ForecastCurve.Zt].Should().HaveCount(2);
    }

    /// <summary>Trio stores no default column; the default is still the IOB curve, as it always was.</summary>
    [Fact]
    public async Task A_trio_upload_keeps_the_iob_curve_as_its_default()
    {
        var snapshot = Snapshot(AidAlgorithm.Trio);
        snapshot.PredictedIobJson = "[100,95]";
        snapshot.PredictedCobJson = "[110]";

        var enriched = await EnrichFromSnapshot(snapshot);

        Points(enriched.Predictions).Should().Equal((5, 100m), (10, 95m));
        enriched.PredictionCurves.Keys.Should().BeEquivalentTo([ForecastCurve.Iob, ForecastCurve.Cob]);
    }

    /// <summary>A Loop upload has one forecast, the default, and no named curve.</summary>
    [Fact]
    public async Task A_loop_upload_sends_its_one_forecast_as_the_default_only()
    {
        var snapshot = Snapshot(AidAlgorithm.Loop);
        snapshot.PredictedDefaultJson = "[120,118,116]";

        var enriched = await EnrichFromSnapshot(snapshot);

        Points(enriched.Predictions).Should().Equal((5, 120m), (10, 118m), (15, 116m));
        enriched.PredictionCurves.Should().BeEmpty();
    }

    private static readonly AlertRuleSnapshot CarbsRule =
        MakeRule(AlertConditionType.CarbsRequired, """{"operator":">","value":10}""");

    private void NewestSnapshotIs(ApsSnapshot? snapshot) =>
        _apsSnapshotRepository
            .Setup(r => r.GetAsync(null, It.IsAny<DateTime?>(), null, null, 1, 0, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(snapshot is null ? [] : [snapshot]);

    private Task<SensorContext> EnrichDetermination() =>
        BuildEnricher().EnrichAsync(BaseContext(), [CarbsRule], CancellationToken.None);

    [Theory]
    [InlineData(AidAlgorithm.OpenAps)]
    [InlineData(AidAlgorithm.AndroidAps)]
    [InlineData(AidAlgorithm.Trio)]
    [InlineData(AidAlgorithm.IAPS)]
    public async Task An_oref_determination_carries_its_eventual_glucose_and_carbs_required(AidAlgorithm algorithm)
    {
        var snapshot = Snapshot(algorithm);
        snapshot.EventualBg = 85;
        snapshot.SuggestedJson = """{"timestamp":"2026-03-22T11:58:00Z","eventualBG":85,"carbsReq":12}""";
        NewestSnapshotIs(snapshot);

        var enriched = await EnrichDetermination();

        enriched.ApsDetermination.Should().Be(new ApsDetermination(snapshot.Timestamp, 85m, 12m));
    }

    /// <summary>oref leaves <c>carbsReq</c> out when it needs none, so a readable command without it is 0.</summary>
    [Theory]
    [InlineData(AidAlgorithm.OpenAps)]
    [InlineData(AidAlgorithm.AndroidAps)]
    [InlineData(AidAlgorithm.Trio)]
    [InlineData(AidAlgorithm.IAPS)]
    public async Task An_oref_command_without_carbs_required_reads_zero(AidAlgorithm algorithm)
    {
        var snapshot = Snapshot(algorithm);
        snapshot.EventualBg = 120;
        snapshot.SuggestedJson = """{"timestamp":"2026-03-22T11:58:00Z","eventualBG":120}""";
        NewestSnapshotIs(snapshot);

        (await EnrichDetermination()).ApsDetermination!.CarbsRequiredGrams.Should().Be(0m);
    }

    /// <summary>The carbs come from the command the eventual glucose came from: the later of the two.</summary>
    [Fact]
    public async Task The_carbs_required_come_from_the_freshest_command()
    {
        var snapshot = Snapshot(AidAlgorithm.AndroidAps);
        snapshot.SuggestedJson = """{"timestamp":"2026-03-22T11:58:00Z","eventualBG":60,"carbsReq":30}""";
        snapshot.EnactedJson = """{"timestamp":"2026-03-22T11:53:00Z","eventualBG":70,"carbsReq":5,"received":true}""";
        NewestSnapshotIs(snapshot);

        (await EnrichDetermination()).ApsDetermination!.CarbsRequiredGrams.Should().Be(30m);
    }

    /// <summary>
    /// oref's early returns (a noisy sensor, old readings) upload a reason and none of a
    /// determination's values: no eventual glucose, so no carbs either, not 0.
    /// </summary>
    [Fact]
    public async Task An_oref_command_that_is_not_a_determination_has_no_carbs_required()
    {
        var snapshot = Snapshot(AidAlgorithm.AndroidAps);
        snapshot.SuggestedJson = """{"timestamp":"2026-03-22T11:58:00Z","reason":"CGM is calibrating, in ??? state, or noise is high"}""";
        NewestSnapshotIs(snapshot);

        var enriched = await EnrichDetermination();

        enriched.ApsDetermination.Should().Be(new ApsDetermination(snapshot.Timestamp, null, null));
    }

    [Fact]
    public async Task A_loop_determination_has_no_carbs_required()
    {
        var snapshot = Snapshot(AidAlgorithm.Loop);
        snapshot.EventualBg = 140;
        snapshot.LoopJson = """{"timestamp":"2026-03-22T11:58:00Z"}""";
        NewestSnapshotIs(snapshot);

        var enriched = await EnrichDetermination();

        enriched.ApsDetermination.Should().Be(new ApsDetermination(snapshot.Timestamp, 140m, null));
    }

    /// <summary>A command that is missing or does not parse says nothing: absent, never 0.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("{not json")]
    public async Task An_oref_snapshot_without_a_readable_command_has_no_carbs_required(string? suggested)
    {
        var snapshot = Snapshot(AidAlgorithm.AndroidAps);
        snapshot.SuggestedJson = suggested;
        NewestSnapshotIs(snapshot);

        (await EnrichDetermination()).ApsDetermination!.CarbsRequiredGrams.Should().BeNull();
    }

    [Fact]
    public async Task No_snapshot_is_no_determination()
    {
        NewestSnapshotIs(null);

        (await EnrichDetermination()).ApsDetermination.Should().BeNull();
    }

    /// <summary>Replay reads the newest snapshot at or before the tick, of any age.</summary>
    [Fact]
    public async Task A_replay_tick_reads_the_determination_at_or_before_it()
    {
        var tick = new DateTime(2026, 3, 22, 10, 0, 0, DateTimeKind.Utc);
        NewestSnapshotIs(Snapshot(AidAlgorithm.Trio));

        await BuildEnricher().EnrichAsOfAsync(BaseContext(), [CarbsRule], tick, CancellationToken.None);

        _apsSnapshotRepository.Verify(r => r.GetAsync(null, tick, null, null, 1, 0, true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_rule_that_reads_no_determination_does_not_fetch_one()
    {
        await BuildEnricher().EnrichAsync(BaseContext(), [LowRule()], CancellationToken.None);

        _apsSnapshotRepository.Verify(r => r.GetAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(),
            It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static string TryResolve(string ianaId, string windowsId)
    {
        try { TimeZoneInfo.FindSystemTimeZoneById(ianaId); return ianaId; }
        catch { return windowsId; }
    }

    private SensorContextEnricher BuildEnricher(
        bool includePredictionService = true,
        AlertEvaluationOptions? options = null,
        ITenantCache? iobCobCache = null,
        IPredictionService? predictionService = null)
    {
        var services = new ServiceCollection();
        if (includePredictionService)
        {
            services.AddSingleton(predictionService ?? _predictionService.Object);
        }
        var provider = services.BuildServiceProvider();

        var deps = new SensorContextEnricherDependencies(
            new IobCobReader(_iobCalculator.Object, _cobCalculator.Object, _bolusRepository.Object,
                _tempBasalRepository.Object, _carbIntakeRepository.Object,
                Mock.Of<ITherapyTimelineResolver>(), Mock.Of<IStateSpanRepository>(), Mock.Of<IApsSnapshotRepository>(), Mock.Of<IPumpSnapshotRepository>(),
                iobCobCache ?? TestTenantCache.Create(partition: null), NullLogger<IobCobReader>.Instance),
            _iobCalculator.Object,
            _cobCalculator.Object,
            _treatmentService.Object,
            _deviceEventRepository.Object,
            _pumpSnapshotRepository.Object,
            _apsSnapshotRepository.Object,
            _tempBasalRepository.Object,
            _uploaderSnapshotRepository.Object,
            _stateSpanService.Object,
            _alertRepository.Object,
            _alertSettings.Object,
            _dndWindows.Object,
            _targetRangeScheduleRepository.Object,
            _activeProfileResolver.Object,
            _profileActivations.Object,
            _therapySettingsResolver.Object,
            _sleepService.Object,
            _trackerRepository.Object,
            _reservoirEstimator.Object,
            _sensorStates.Object,
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
