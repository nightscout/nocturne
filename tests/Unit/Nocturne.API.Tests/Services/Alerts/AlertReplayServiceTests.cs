using System.Globalization;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.API.Tests.Infrastructure;
using Nocturne.Core.Clinical.Treatments;
using FluentAssertions;
using Nocturne.Alerts.Corpus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Nocturne.API.Configuration;
using Nocturne.API.Services.Alerts;
using Nocturne.API.Services.Alerts.Engines;
using Nocturne.API.Services.Glucose;
using Nocturne.API.Services.Analytics;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts;

[Trait("Category", "Unit")]
public class AlertReplayServiceTests
{
    private readonly Mock<IAlertRepository> _alertRepository = new();
    private readonly Mock<IDndWindowRepository> _dndWindows = new();
    private readonly Mock<ITenantAlertSettingsRepository> _alertSettings = new();
    private readonly Mock<ISensorGlucoseRepository> _glucoseRepository = new();
    private readonly Mock<ITenantAccessor> _tenantAccessor = new();
    // Looping-fact mocks plumbed through the real SensorContextEnricher so replay walks
    // the same code path as the live engine. Default-empty Moq returns mean cold-start
    // semantics by default; individual tests override what they care about.
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
    private readonly Mock<Nocturne.API.Services.Devices.IReservoirEstimationService> _reservoirEstimation = new();
    private readonly SensorContextEnricher _enricher;
    private readonly AlertReplayService _sut;

    private readonly Guid _tenantId = Guid.NewGuid();

    public AlertReplayServiceTests()
    {
        _tenantAccessor.Setup(t => t.TenantId).Returns(_tenantId);
        _glucoseRepository
            .Setup(r => r.GetHiddenAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<int>(),
                It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        // The repo never returns null; default the DND-window fetches to empty (no DND).
        // Tests that exercise scoped suppression override GetReceivedByAsync.
        _dndWindows
            .Setup(r => r.GetReceivedByAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<DndWindowSnapshot>());
        _dndWindows
            .Setup(r => r.GetUnexpiredSnapshotsAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<DndWindowSnapshot>());

        var enricherDeps = new SensorContextEnricherDependencies(
            new IobCobReader(_iobCalculator.Object, _cobCalculator.Object, _bolusRepository.Object,
                _tempBasalRepository.Object, _carbIntakeRepository.Object, Mock.Of<ITherapyTimelineResolver>(), Mock.Of<IStateSpanRepository>(), Mock.Of<IApsSnapshotRepository>(), Mock.Of<IPumpSnapshotRepository>(),
                TestTenantCache.Create(partition: null), NullLogger<IobCobReader>.Instance),
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
            new Mock<Nocturne.Core.Contracts.V4.Repositories.ITargetRangeScheduleRepository>().Object,
            new Mock<Nocturne.Core.Contracts.Profiles.Resolvers.IActiveProfileResolver>().Object,
            new Mock<Nocturne.Core.Contracts.Profiles.Resolvers.IProfileActivationResolver>().Object,
            new Mock<Nocturne.Core.Contracts.Profiles.Resolvers.ITherapySettingsResolver>().Object,
            new Mock<Nocturne.Core.Contracts.Sleep.ISleepService>().Object,
            new Mock<Nocturne.Core.Contracts.Repositories.ITrackerRepository>().Object,
            _reservoirEstimation.Object,
            Mock.Of<Nocturne.Core.Contracts.V4.Repositories.ISensorStateReportRepository>(),
            Options.Create(new AlertEvaluationOptions()));
        _enricher = new SensorContextEnricher(
            enricherDeps,
            new ServiceCollection().BuildServiceProvider(),
            TimeProvider.System,
            NullLogger<SensorContextEnricher>.Instance);

        _sut = new AlertReplayService(
            _alertRepository.Object,
            _dndWindows.Object,
            _glucoseRepository.Object,
            _enricher,
            _tenantAccessor.Object,
            Options.Create(new AlertEvaluationOptions()),
            new RustAlertReplayEngine(new AlertEngineErrors(
                new Nocturne.API.Tests.Services.BackgroundServices.TestMeterFactory(), TimeProvider.System)),
            new ImportedHistory(_glucoseRepository.Object, new Mock<Nocturne.Core.Contracts.Auth.IOAuthGrantRepository>().Object, TestTenantCache.Create()),
            TimeProvider.System);
    }

    private static AlertRuleSnapshot ThresholdRule(Guid id, string direction, decimal value,
        AlertRuleSeverity severity = AlertRuleSeverity.Warning) =>
        new(id, Guid.NewGuid(), $"{direction}-{value}", AlertConditionType.Threshold,
            $$"""{"direction":"{{direction}}","value":{{value}}}""", severity, "{}", 0,
            AutoResolveEnabled: false, AutoResolveParams: null);

    private static SensorGlucose Reading(DateTime at, double mgdl) => new()
    {
        Id = Guid.NewGuid(),
        Timestamp = at,
        CreatedAt = at,
        Mgdl = mgdl,
    };

    private sealed class CapturingReplayEngine : IAlertReplayEngine
    {
        public AlertReplayInput? Input { get; private set; }

        public Task<AlertReplayRun> ReplayAsync(AlertReplayInput input, CancellationToken ct)
        {
            Input = input;
            return Task.FromResult(new AlertReplayRun([], [], [], null));
        }
    }

    private void SetupReadings(params SensorGlucose[] readings) =>
        _glucoseRepository.Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), null, null,
                It.IsAny<int>(), It.IsAny<int>(), false, It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>(), null, It.IsAny<GlucoseStreams>()))
            .ReturnsAsync(readings);

    /// <summary>The readings the repository finds dated before their stream's first arrival.</summary>
    /// <summary>
    /// Places <paramref name="readings"/> in a connection whose first arrival is after all of them, so
    /// they are imported history; every other reading belongs to no stream the repository reports.
    /// </summary>
    private void SetupImported(params SensorGlucose[] readings)
    {
        _glucoseRepository.Setup(r => r.GetStreamReadingsAsync(
                It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(readings.Select(r => new GlucoseStreamReading(r.Id, r.Timestamp, null, "import", null)).ToList());
        _glucoseRepository.Setup(r => r.GetStreamFirstArrivalsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([new GlucoseStreamArrival(null, "import", null, readings.Max(r => r.Timestamp).AddTicks(1))]);
    }

    private static SensorGlucose Arriving(DateTime at, double mgdl, TimeSpan delay)
    {
        var reading = Reading(at, mgdl);
        reading.CreatedAt = at + delay;
        return reading;
    }

    private static AlertRuleSnapshot SignalLoss(Guid id, int minutes) =>
        new(id, Guid.NewGuid(), "signal-loss", AlertConditionType.SignalLoss,
            $$"""{"timeout_minutes":{{minutes}}}""", AlertRuleSeverity.Warning, "{}", 0, false, null);

    /// <summary>
    /// Every rule is evaluated at the window's start and at each reading where live evaluated it:
    /// its arrival, however late, unless it is imported history, then its timestamp. A gap longer
    /// than five minutes samples the other facts every five minutes from the last tick, and the
    /// engine steps on to the window's end (docs/alerts/engine-semantics.md §8). A reading uploaded
    /// late is evaluated after one uploaded on time, and its context keeps the newer reading.
    /// </summary>
    [NativeFact]
    public async Task Ticks_are_the_readings_where_live_evaluated_them_and_samples_of_the_gaps()
    {
        var windowStart = new DateTime(2026, 4, 28, 0, 0, 0, DateTimeKind.Utc);
        var windowEnd = windowStart.AddMinutes(40);
        _alertRepository.Setup(r => r.GetEnabledRulesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { ThresholdRule(Guid.NewGuid(), "below", 70m) });
        var import = Arriving(windowStart.AddMinutes(30), 130, TimeSpan.FromDays(1));
        SetupReadings(
            Reading(windowStart.AddMinutes(2.5), 100),
            Arriving(windowStart.AddMinutes(19), 110, TimeSpan.FromMinutes(6)),
            Arriving(windowStart.AddMinutes(23), 120, TimeSpan.FromSeconds(30)),
            import);
        SetupImported(import);
        var engine = new CapturingReplayEngine();
        var sut = new AlertReplayService(
            _alertRepository.Object, _dndWindows.Object, _glucoseRepository.Object, _enricher,
            _tenantAccessor.Object, Options.Create(new AlertEvaluationOptions()), engine,
            new ImportedHistory(_glucoseRepository.Object, new Mock<Nocturne.Core.Contracts.Auth.IOAuthGrantRepository>().Object, TestTenantCache.Create()), TimeProvider.System);

        await sut.ReplayAsync(null, null, windowStart, windowEnd, CancellationToken.None);

        engine.Input!.Until.Should().Be(windowEnd);
        engine.Input.Ticks.Select(t => ((t.At - windowStart).TotalMinutes, t.Reading, t.Context.LatestValue))
            .Should().Equal(new (double, bool, decimal?)[]
            {
                (0, true, null), (2.5, true, 100m), (7.5, false, 100m), (12.5, false, 100m),
                (17.5, false, 100m), (22.5, false, 100m), (23.5, true, 120m), (25, true, 120m),
                (30, true, 130m), (35, false, 130m),
            });
        var imported = engine.Input.Ticks.Single(t => t.At == windowStart.AddMinutes(30)).Context;
        imported.LatestReceivedAt.Should().BeNull("imported history was not received by live as it came");
        engine.Input.Ticks.Single(t => t.At == windowStart.AddMinutes(23.5)).Context.LatestReceivedAt
            .Should().Be(windowStart.AddMinutes(23.5));
    }

    /// <summary>
    /// A reading with no glucose value is replayed as live reads it: no value, the sensor's
    /// no_value state from the first such reading, and signal loss measured from the last reading
    /// with a value (docs/alerts/engine-semantics.md §8).
    /// </summary>
    [NativeFact]
    public async Task A_reading_with_no_value_is_replayed_as_the_no_value_state()
    {
        var windowStart = new DateTime(2026, 4, 28, 0, 0, 0, DateTimeKind.Utc);
        _alertRepository.Setup(r => r.GetEnabledRulesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { ThresholdRule(Guid.NewGuid(), "below", 70m) });
        SetupReadings(
            Reading(windowStart.AddMinutes(5), 60),
            Reading(windowStart.AddMinutes(10), 0),
            Reading(windowStart.AddMinutes(15), 0),
            Reading(windowStart.AddMinutes(20), 80));
        var engine = new CapturingReplayEngine();
        var sut = new AlertReplayService(
            _alertRepository.Object, _dndWindows.Object, _glucoseRepository.Object, _enricher,
            _tenantAccessor.Object, Options.Create(new AlertEvaluationOptions()), engine,
            new ImportedHistory(_glucoseRepository.Object, new Mock<Nocturne.Core.Contracts.Auth.IOAuthGrantRepository>().Object, TestTenantCache.Create()), TimeProvider.System);

        await sut.ReplayAsync(null, null, windowStart, windowStart.AddMinutes(25), CancellationToken.None);

        SensorContext At(double minutes) => engine.Input!.Ticks.Single(t => t.At == windowStart.AddMinutes(minutes)).Context;
        At(5).SensorState.Should().BeNull();
        var valueless = At(15);
        valueless.LatestValue.Should().BeNull("a reading with no value is not a reading of zero");
        valueless.LatestTimestamp.Should().Be(windowStart.AddMinutes(15));
        valueless.LastReadingAt.Should().Be(windowStart.AddMinutes(5));
        valueless.SensorState.Should().Be(new SensorStateSnapshot(SensorState.NoValue, windowStart.AddMinutes(10)));
        At(20).Should().Match<SensorContext>(c => c.LatestValue == 80m && c.SensorState == null && c.LastReadingAt == windowStart.AddMinutes(20));
    }

    /// <summary>
    /// A reading uploaded late reaches live after its timestamp, so the outage live measures runs
    /// until the arrival. Readings at 00:00 and 00:17, each four minutes late: a 20-minute signal
    /// loss fires at 00:20, before the second arrives.
    /// </summary>
    [NativeFact]
    public async Task Signal_loss_fires_over_an_upload_delay_as_live_does()
    {
        var dayStart = new DateTime(2026, 4, 28, 0, 0, 0, DateTimeKind.Utc);
        _alertRepository.Setup(r => r.GetEnabledRulesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { SignalLoss(Guid.NewGuid(), 20) });
        var delay = TimeSpan.FromMinutes(4);
        SetupReadings(Arriving(dayStart.AddHours(1), 100, delay), Arriving(dayStart.AddHours(1).AddMinutes(17), 100, delay));

        var result = await _sut.ReplayAsync(null, null, dayStart.AddHours(1), dayStart.AddHours(2), CancellationToken.None);

        result.Events.Where(e => e.Kind == AlertReplayEventKind.Fired).Select(e => e.At)
            .Should().Equal(dayStart.AddHours(1).AddMinutes(20), dayStart.AddHours(1).AddMinutes(37));
    }

    /// <summary>
    /// A connector that catches up after an outage uploads the readings it missed at once, up to
    /// 17 minutes late. Live saw the outage until the catch-up, so a 20-minute signal loss fires at
    /// 00:50 and ends at the catch-up at 00:52.
    /// </summary>
    [NativeFact]
    public async Task A_catch_up_after_an_outage_shows_the_signal_loss_live_raised()
    {
        var dayStart = new DateTime(2026, 4, 28, 0, 0, 0, DateTimeKind.Utc);
        _alertRepository.Setup(r => r.GetEnabledRulesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { SignalLoss(Guid.NewGuid(), 20) });
        var catchUp = dayStart.AddMinutes(52);
        SetupReadings(Enumerable.Range(0, 11)
            .Select(i => dayStart.AddMinutes(5 * i))
            .Select(at => at <= dayStart.AddMinutes(30) ? Reading(at, 100) : Arriving(at, 100, catchUp - at))
            .ToArray());

        var result = await _sut.ReplayAsync(null, null, dayStart, dayStart.AddHours(1), CancellationToken.None);

        result.Events.Where(e => e.Kind == AlertReplayEventKind.Fired).Select(e => e.At)
            .Should().Equal(dayStart.AddMinutes(50));
    }

    /// <summary>
    /// A first connect brings in the days before it: readings dated before the stream first sent
    /// anything are imported history, replayed at their timestamps, so the gap inside them reads as
    /// it would have live and the time before the connect is not one long outage. Readings every
    /// 5 minutes from 00:00 to 01:55 with none from 00:30 to 01:05, all stored at the connect at
    /// 02:00, then live readings: a 30-minute signal loss fires once, at 00:55, in the gap.
    /// </summary>
    [NativeFact]
    public async Task A_first_connect_backfill_reads_its_gaps_from_its_timestamps()
    {
        var dayStart = new DateTime(2026, 4, 28, 0, 0, 0, DateTimeKind.Utc);
        _alertRepository.Setup(r => r.GetEnabledRulesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { SignalLoss(Guid.NewGuid(), 30) });
        var connect = dayStart.AddHours(2);
        var backfill = Enumerable.Range(0, 24)
            .Select(i => dayStart.AddMinutes(5 * i))
            .Where(at => at < dayStart.AddMinutes(30) || at > dayStart.AddMinutes(65))
            .Select(at => Arriving(at, 100, connect - at))
            .ToArray();
        SetupReadings([.. backfill, Arriving(connect.AddMinutes(5), 100, TimeSpan.FromSeconds(20)),
            Arriving(connect.AddMinutes(10), 100, TimeSpan.FromSeconds(20))]);
        SetupImported(backfill);

        var result = await _sut.ReplayAsync(null, null, dayStart, dayStart.AddHours(3), CancellationToken.None);

        result.Events.Where(e => e.Kind == AlertReplayEventKind.Fired).Select(e => e.At)
            .Should().Equal(dayStart.AddMinutes(55), dayStart.AddMinutes(160));
    }

    /// <summary>
    /// A backfilled day (dated before its stream first sent anything) is aged from each reading's
    /// timestamp, so the hour-long gap in it reads stale as it would have live. Readings at 00:00,
    /// 00:05, 01:05 and 01:10 against a below-70 rule with the 11-minute bound: under <c>met</c>
    /// the stale gap fires; under <c>not_met</c> it ends the excursion, and the low after it fires
    /// again; under <c>hold</c> the excursion is held across the gap. Under <c>met</c> the last
    /// reading goes stale again 11 minutes after it.
    /// </summary>
    [NativeTheory]
    [InlineData("met", 100, 100, "16+ 81+")]
    [InlineData("not_met", 60, 60, "0 65")]
    [InlineData("hold", 60, 60, "0")]
    public async Task A_backfilled_day_goes_stale_across_its_gaps(
        string onUnknown, double before, double after, string fires)
    {
        var ruleId = Guid.NewGuid();
        var dayStart = new DateTime(2026, 4, 28, 0, 0, 0, DateTimeKind.Utc);
        _alertRepository.Setup(r => r.GetEnabledRulesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { ThresholdRule(ruleId, "below", 70m) with { OnUnknown = onUnknown } });
        var backfill = TimeSpan.FromDays(1);
        SensorGlucose[] readings =
        [
            Arriving(dayStart, before, backfill),
            Arriving(dayStart.AddMinutes(5), before, backfill),
            Arriving(dayStart.AddMinutes(65), after, backfill),
            Arriving(dayStart.AddMinutes(70), 100, backfill),
        ];
        SetupReadings(readings);
        SetupImported(readings);

        var result = await _sut.ReplayAsync(null, null, dayStart, dayStart.AddHours(2), CancellationToken.None);

        // "16+" is the first instant past minute 16, where a stale reading turns unknown.
        var expected = fires.Split(' ').Select(f => f.EndsWith('+')
            ? dayStart.AddMinutes(int.Parse(f.TrimEnd('+'), CultureInfo.InvariantCulture)).AddTicks(1)
            : dayStart.AddMinutes(int.Parse(f, CultureInfo.InvariantCulture)));
        result.Events.Where(e => e.Kind == AlertReplayEventKind.Fired).Select(e => e.At).Should().Equal(expected);
    }

    [NativeFact]
    public async Task EmptyTenantId_ReturnsEmpty()
    {
        _tenantAccessor.Setup(t => t.TenantId).Returns(Guid.Empty);

        var result = await _sut.ReplayAsync(null, null, null, null, CancellationToken.None);

        result.Events.Should().BeEmpty();
    }

    [NativeFact]
    public async Task NoRules_ReturnsEmptyEventsButValidWindow()
    {
        _alertRepository.Setup(r => r.GetEnabledRulesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AlertRuleSnapshot>());

        var result = await _sut.ReplayAsync(
            new DateOnly(2026, 4, 28), "UTC", null, null, CancellationToken.None);

        result.Events.Should().BeEmpty();
        result.WindowStart.Should().Be(new DateTime(2026, 4, 28, 0, 0, 0, DateTimeKind.Utc));
        result.WindowEnd.Should().Be(new DateTime(2026, 4, 29, 0, 0, 0, DateTimeKind.Utc));
    }

    [NativeFact]
    public async Task ThresholdBelow_LowReading_FiresOneEventAtLeadingEdge()
    {
        var ruleId = Guid.NewGuid();
        var rule = ThresholdRule(ruleId, "below", 70m);
        var date = new DateOnly(2026, 4, 28);
        var dayStart = new DateTime(2026, 4, 28, 0, 0, 0, DateTimeKind.Utc);

        _alertRepository.Setup(r => r.GetEnabledRulesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { rule });

        // Single low reading at 04:00, then a clear at 04:30, then another low at 05:00.
        var readings = new[]
        {
            Reading(dayStart.AddHours(4), 65),
            Reading(dayStart.AddHours(4).AddMinutes(30), 95),
            Reading(dayStart.AddHours(5), 60),
        };
        _glucoseRepository.Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), null, null,
                It.IsAny<int>(), It.IsAny<int>(), false, It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>(), null, It.IsAny<GlucoseStreams>()))
            .ReturnsAsync(readings);

        var result = await _sut.ReplayAsync(date, "UTC", null, null, CancellationToken.None);

        result.Events.Should().HaveCount(2);
        result.Events[0].RuleId.Should().Be(ruleId);
        result.Events[0].At.Should().Be(dayStart.AddHours(4));
        result.Events[1].At.Should().Be(dayStart.AddHours(5));
    }

    [NativeFact]
    public async Task ContinuouslyMet_ProducesSingleLeadingEdgeEvent()
    {
        var rule = ThresholdRule(Guid.NewGuid(), "below", 70m);
        var date = new DateOnly(2026, 4, 28);
        var dayStart = new DateTime(2026, 4, 28, 0, 0, 0, DateTimeKind.Utc);

        _alertRepository.Setup(r => r.GetEnabledRulesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { rule });

        // Continuous low for an hour — should produce ONE event at the first low tick.
        var readings = Enumerable.Range(0, 12)
            .Select(i => Reading(dayStart.AddHours(2).AddMinutes(i * 5), 60))
            .ToArray();
        _glucoseRepository.Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), null, null,
                It.IsAny<int>(), It.IsAny<int>(), false, It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>(), null, It.IsAny<GlucoseStreams>()))
            .ReturnsAsync(readings);

        var result = await _sut.ReplayAsync(date, "UTC", null, null, CancellationToken.None);

        result.Events.Should().HaveCount(1);
        result.Events[0].At.Should().Be(dayStart.AddHours(2));
    }

    [NativeFact]
    public async Task ScopedDnd_LowsWindow_SuppressesALowRuleInReplay()
    {
        var rule = new AlertRuleSnapshot(Guid.NewGuid(), _tenantId, "low", AlertConditionType.Threshold,
            """{"direction":"below","value":70}""", AlertRuleSeverity.Warning, "{}", 0,
            AutoResolveEnabled: false, AutoResolveParams: null, AllowThroughDnd: false,
            ScopeClass: RuleScopeClass.Low);
        var date = new DateOnly(2026, 4, 28);
        var dayStart = new DateTime(2026, 4, 28, 0, 0, 0, DateTimeKind.Utc);

        _alertRepository.Setup(r => r.GetEnabledRulesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { rule });

        // A lows window received before the fire tick (created_at <= tick) and active across it.
        _dndWindows.Setup(r => r.GetReceivedByAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new DndWindowSnapshot(DndScope.Lows, StartedAt: dayStart, EndsAt: null, ClearedAt: null, CreatedAt: dayStart),
            });

        var readings = Enumerable.Range(0, 12)
            .Select(i => Reading(dayStart.AddHours(2).AddMinutes(i * 5), 60))
            .ToArray();
        _glucoseRepository.Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), null, null,
                It.IsAny<int>(), It.IsAny<int>(), false, It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>(), null, It.IsAny<GlucoseStreams>()))
            .ReturnsAsync(readings);

        var result = await _sut.ReplayAsync(date, "UTC", null, null, CancellationToken.None);

        result.Events.Should().HaveCount(1);
        result.Events[0].Kind.Should().Be(AlertReplayEventKind.SuppressedByDnd);
    }

    [NativeFact]
    public async Task ScopedDnd_WindowReceivedAfterTheFire_DoesNotSuppress_ReceiptGating()
    {
        var rule = new AlertRuleSnapshot(Guid.NewGuid(), _tenantId, "low", AlertConditionType.Threshold,
            """{"direction":"below","value":70}""", AlertRuleSeverity.Warning, "{}", 0,
            AutoResolveEnabled: false, AutoResolveParams: null, AllowThroughDnd: false,
            ScopeClass: RuleScopeClass.Low);
        var date = new DateOnly(2026, 4, 28);
        var dayStart = new DateTime(2026, 4, 28, 0, 0, 0, DateTimeKind.Utc);

        _alertRepository.Setup(r => r.GetEnabledRulesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { rule });

        // The window's intent (StartedAt) covers the fire, but it was *received* an hour after
        // the fire tick — receipt gating (created_at <= tick) must NOT retroactively suppress.
        _dndWindows.Setup(r => r.GetReceivedByAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new DndWindowSnapshot(DndScope.Lows, StartedAt: dayStart, EndsAt: null, ClearedAt: null,
                    CreatedAt: dayStart.AddHours(3)),
            });

        var readings = Enumerable.Range(0, 12)
            .Select(i => Reading(dayStart.AddHours(2).AddMinutes(i * 5), 60))
            .ToArray();
        _glucoseRepository.Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), null, null,
                It.IsAny<int>(), It.IsAny<int>(), false, It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>(), null, It.IsAny<GlucoseStreams>()))
            .ReturnsAsync(readings);

        var result = await _sut.ReplayAsync(date, "UTC", null, null, CancellationToken.None);

        result.Events.Should().HaveCount(1);
        result.Events[0].Kind.Should().Be(AlertReplayEventKind.Fired);
    }

    [NativeFact]
    public async Task DoNotDisturbLeaf_EvaluatesTrueAtTicksWhereAnAllWindowWasActive()
    {
        // A do_not_disturb(is_active=true) rule with AllowThroughDnd so the fire is not
        // itself suppressed by the same all-window that makes the leaf true.
        var ruleId = Guid.NewGuid();
        var rule = new AlertRuleSnapshot(ruleId, _tenantId, "dnd-on", AlertConditionType.DoNotDisturb,
            """{"is_active":true}""", AlertRuleSeverity.Info, "{}", 0,
            AutoResolveEnabled: false, AutoResolveParams: null, AllowThroughDnd: true);
        var date = new DateOnly(2026, 4, 28);
        var dayStart = new DateTime(2026, 4, 28, 0, 0, 0, DateTimeKind.Utc);

        _alertRepository.Setup(r => r.GetEnabledRulesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { rule });

        // A scope=all window active (and received) 02:00-03:00; replay must reconstruct
        // ActiveDoNotDisturb from it so the leaf evaluates true across exactly that span.
        _dndWindows.Setup(r => r.GetReceivedByAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new DndWindowSnapshot(DndScope.All, StartedAt: dayStart.AddHours(2),
                    EndsAt: dayStart.AddHours(3), ClearedAt: null, CreatedAt: dayStart.AddHours(2)),
            });

        // The leaf needs no glucose; replay still walks the day on no-data ticks.
        _glucoseRepository.Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), null, null,
                It.IsAny<int>(), It.IsAny<int>(), false, It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>(), null, It.IsAny<GlucoseStreams>()))
            .ReturnsAsync(Array.Empty<SensorGlucose>());

        var result = await _sut.ReplayAsync(date, "UTC", null, null, CancellationToken.None);

        // One leading-edge fire at the window start; the leaf goes false again at expiry.
        result.Events.Should().ContainSingle();
        result.Events[0].RuleId.Should().Be(ruleId);
        result.Events[0].Kind.Should().Be(AlertReplayEventKind.Fired);
        result.Events[0].At.Should().Be(dayStart.AddHours(2));
    }

    [NativeFact]
    public async Task MultipleRules_OrderedByTopologicalDependency()
    {
        // Rule A: threshold < 70
        // Rule B: alert_state(A, "firing") — should fire at the SAME tick as A, but only after.
        var ruleAId = Guid.NewGuid();
        var ruleBId = Guid.NewGuid();
        var ruleA = ThresholdRule(ruleAId, "below", 70m);
        var ruleB = new AlertRuleSnapshot(ruleBId, _tenantId, "B-chains-A",
            AlertConditionType.AlertState,
            $$"""{"alert_id":"{{ruleAId}}","state":"firing"}""",
            AlertRuleSeverity.Critical, "{}", 0, false, null);

        var date = new DateOnly(2026, 4, 28);
        var dayStart = new DateTime(2026, 4, 28, 0, 0, 0, DateTimeKind.Utc);

        // Insertion order is B then A (deliberately reversed) — topo-sort must put A first
        // so B sees A's "firing" snapshot in the same tick.
        _alertRepository.Setup(r => r.GetEnabledRulesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { ruleB, ruleA });

        _glucoseRepository.Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), null, null,
                It.IsAny<int>(), It.IsAny<int>(), false, It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>(), null, It.IsAny<GlucoseStreams>()))
            .ReturnsAsync(new[] { Reading(dayStart.AddHours(3), 60) });

        var result = await _sut.ReplayAsync(date, "UTC", null, null, CancellationToken.None);

        // Both should fire. A first (its event was added first within the tick), then B.
        result.Events.Should().HaveCount(2);
        result.Events[0].RuleId.Should().Be(ruleAId);
        result.Events[1].RuleId.Should().Be(ruleBId);
    }

    [NativeFact]
    public async Task SameTickOpenAndAutoResolve_EmitsFiredThenAutoResolved()
    {
        // Mirror of AlertOrchestrator.EvaluateRuleAsync: HandleExcursionOpened runs first,
        // then TryAutoResolveAsync runs unconditionally on the same reading. A rule whose
        // body opens at tick T and whose auto-resolve predicate is already true at T should
        // produce both events at T (live stamps resolution_reason="auto_resolve" on the
        // near-zero-duration excursion). Replay must mirror this — gating auto-resolve on
        // the previous tick's wasFiring would silently drop the resolve.
        var ruleId = Guid.NewGuid();
        // Open condition: glucose < 80. Auto-resolve: glucose > 70. A reading at 75 satisfies
        // both simultaneously.
        var rule = new AlertRuleSnapshot(
            ruleId, _tenantId, "open-and-resolve",
            AlertConditionType.Threshold,
            """{"direction":"below","value":80}""",
            AlertRuleSeverity.Warning, "{}", 0,
            AutoResolveEnabled: true,
            AutoResolveParams: """{"type":"threshold","threshold":{"direction":"above","value":70}}""");

        var date = new DateOnly(2026, 4, 28);
        var dayStart = new DateTime(2026, 4, 28, 0, 0, 0, DateTimeKind.Utc);

        _alertRepository.Setup(r => r.GetEnabledRulesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { rule });
        // First reading at 03:00 satisfies both predicates (open + immediate resolve);
        // second reading at 03:05 is well above both so subsequent ticks stay quiet and the
        // assertion locks the same-tick pair in isolation.
        _glucoseRepository.Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), null, null,
                It.IsAny<int>(), It.IsAny<int>(), false, It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>(), null, It.IsAny<GlucoseStreams>()))
            .ReturnsAsync(new[]
            {
                Reading(dayStart.AddHours(3), 75),
                Reading(dayStart.AddHours(3).AddMinutes(5), 110),
            });

        var result = await _sut.ReplayAsync(date, "UTC", null, null, CancellationToken.None);

        result.Events.Should().HaveCount(2);
        result.Events[0].RuleId.Should().Be(ruleId);
        result.Events[0].Kind.Should().Be(AlertReplayEventKind.Fired);
        result.Events[1].RuleId.Should().Be(ruleId);
        result.Events[1].Kind.Should().Be(AlertReplayEventKind.AutoResolved);
        result.Events[0].At.Should().Be(result.Events[1].At, "fire and auto-resolve happen on the same tick");
    }

    [NativeFact]
    public async Task RollingWindow_When_Date_IsNull()
    {
        _alertRepository.Setup(r => r.GetEnabledRulesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AlertRuleSnapshot>());

        var result = await _sut.ReplayAsync(null, null, null, null, CancellationToken.None);

        (result.WindowEnd - result.WindowStart).Should().BeCloseTo(TimeSpan.FromHours(24), TimeSpan.FromSeconds(5));
    }

    [NativeFact]
    public async Task LoopStale_fires_the_first_instant_past_its_bound()
    {
        // ApsSnapshot at dayStart+02:00; thereafter loop is silent. A loop_stale > 5 rule
        // fires the first instant more than 5 minutes have elapsed since the last cycle, between
        // the samples, as the live sweep would within its cadence.
        var ruleId = Guid.NewGuid();
        var rule = new AlertRuleSnapshot(ruleId, _tenantId, "loop-stale-5",
            AlertConditionType.LoopStale,
            """{"operator":">","minutes":5}""",
            AlertRuleSeverity.Warning, "{}", 0, false, null);

        var date = new DateOnly(2026, 4, 28);
        var dayStart = new DateTime(2026, 4, 28, 0, 0, 0, DateTimeKind.Utc);
        var lastCycle = dayStart.AddHours(2);

        _alertRepository.Setup(r => r.GetEnabledRulesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { rule });
        _glucoseRepository.Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), null, null,
                It.IsAny<int>(), It.IsAny<int>(), false, It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>(), null, It.IsAny<GlucoseStreams>()))
            .ReturnsAsync(Array.Empty<SensorGlucose>());

        // The enricher uses GetLatestTimestampAsync(asOf) — pinned to each replay tick. We
        // emulate "no cycle ever happened before lastCycle, then lastCycle is the only one"
        // by returning lastCycle when asOf >= lastCycle and null otherwise.
        _apsSnapshotRepository.Setup(r => r.GetLatestTimestampAsOfAsync(It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DateTime? asOf, CancellationToken _) =>
                asOf is not null && asOf.Value >= lastCycle ? lastCycle : null);

        var result = await _sut.ReplayAsync(date, "UTC", null, null, CancellationToken.None);

        result.Events.Should().NotBeEmpty();
        result.Events[0].RuleId.Should().Be(ruleId);
        result.Events[0].At.Should().Be(lastCycle.AddMinutes(5).AddTicks(1));
    }

    [NativeFact]
    public async Task PumpSuspended_fires_at_correct_replay_tick()
    {
        // A pump-suspended StateSpan opens at 03:00 with no end. Rule: pump_suspended is_active=true.
        // The freshness gate requires a recent PumpSnapshot — we provide one at every replay
        // tick (Timestamp = asOf - 1 min) so the suspension projection isn't suppressed.
        var ruleId = Guid.NewGuid();
        var rule = new AlertRuleSnapshot(ruleId, _tenantId, "pump-suspended",
            AlertConditionType.PumpSuspended,
            """{"is_active":true}""",
            AlertRuleSeverity.Warning, "{}", 0, false, null);

        var date = new DateOnly(2026, 4, 28);
        var dayStart = new DateTime(2026, 4, 28, 0, 0, 0, DateTimeKind.Utc);
        var spanStart = dayStart.AddHours(3);

        _alertRepository.Setup(r => r.GetEnabledRulesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { rule });
        _glucoseRepository.Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), null, null,
                It.IsAny<int>(), It.IsAny<int>(), false, It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>(), null, It.IsAny<GlucoseStreams>()))
            .ReturnsAsync(Array.Empty<SensorGlucose>());

        _pumpSnapshotRepository.Setup(r => r.GetLatestAsync(It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DateTime? asOf, CancellationToken _) =>
                asOf is null ? null : new PumpSnapshot { Timestamp = asOf.Value.AddMinutes(-1), BatteryPercent = 80 });

        var suspension = new StateSpan
        {
            Category = StateSpanCategory.PumpMode,
            State = PumpModeState.Suspended.ToString(),
            StartTimestamp = spanStart,
        };
        _stateSpanService.Setup(s => s.GetStateSpansAsync(
                StateSpanCategory.PumpMode, It.IsAny<string?>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<string?>(), It.IsAny<bool?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((StateSpanCategory? _, string? _, DateTime? _, DateTime? to, string? _, bool? _,
                    int _, int _, bool _, CancellationToken _) =>
                to is null || suspension.StartTimestamp <= to ? [suspension] : Array.Empty<StateSpan>());

        var result = await _sut.ReplayAsync(date, "UTC", null, null, CancellationToken.None);

        result.Events.Should().HaveCount(1);
        result.Events[0].RuleId.Should().Be(ruleId);
        // First tick at-or-after spanStart is exactly 03:00 since 5-min ticks land on 0/5/10/...
        result.Events[0].At.Should().Be(spanStart);
    }

    /// <summary>
    /// Replay must emit a <c>latest_glucose</c> fact timeline so the rule sidebar can
    /// annotate threshold leaves with the current value at the playhead. Asserts the
    /// glucose value flows through change-detection (rounded to integer mg/dL) and the
    /// emitted points match the readings.
    /// </summary>
    [NativeFact]
    public async Task FactTimelines_LatestGlucose_EmitsOnReadingChange()
    {
        var rule = ThresholdRule(Guid.NewGuid(), "below", 70m);
        var date = new DateOnly(2026, 4, 28);
        var dayStart = new DateTime(2026, 4, 28, 0, 0, 0, DateTimeKind.Utc);

        _alertRepository.Setup(r => r.GetEnabledRulesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { rule });

        // Three distinct values at 04:00/04:30/05:00 — expect ≥ 3 emitted points
        // (the snapshot also seeds a baseline at first observation if non-null).
        var readings = new[]
        {
            Reading(dayStart.AddHours(4), 65),
            Reading(dayStart.AddHours(4).AddMinutes(30), 95),
            Reading(dayStart.AddHours(5), 60),
        };
        _glucoseRepository.Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), null, null,
                It.IsAny<int>(), It.IsAny<int>(), false, It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>(), null, It.IsAny<GlucoseStreams>()))
            .ReturnsAsync(readings);

        var result = await _sut.ReplayAsync(date, "UTC", null, null, CancellationToken.None);

        result.FactTimelines.Should().ContainKey("latest_glucose");
        var glucosePoints = result.FactTimelines["latest_glucose"];
        glucosePoints.Select(p => p.Value).Should().ContainInOrder(65m, 95m, 60m);
    }

    /// <summary>
    /// <c>ReservoirIsLowerBound</c> is a bool fact projected to 0/1 by the Direct conversion.
    /// A capped reading ("50+") must surface in the <c>reservoir_is_lower_bound</c> timeline
    /// so the FE can distinguish it from an exact 50.0.
    /// </summary>
    [NativeFact]
    public async Task FactTimelines_ReservoirIsLowerBound_EmitsBoolAsOne()
    {
        var rule = new AlertRuleSnapshot(
            Guid.NewGuid(), Guid.NewGuid(), "reservoir-low", AlertConditionType.Reservoir,
            """{"operator":"<","value":10}""", AlertRuleSeverity.Warning, "{}", 0,
            AutoResolveEnabled: false, AutoResolveParams: null);
        var date = new DateOnly(2026, 4, 28);

        _alertRepository.Setup(r => r.GetEnabledRulesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { rule });
        _glucoseRepository.Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), null, null,
                It.IsAny<int>(), It.IsAny<int>(), false, It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>(), null, It.IsAny<GlucoseStreams>()))
            .ReturnsAsync(Array.Empty<SensorGlucose>());
        _reservoirEstimation.Setup(s => s.GetEstimateAsync(It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Nocturne.API.Services.Devices.ReservoirEstimate(
                50m, IsLowerBound: true, IsEstimated: false));

        var result = await _sut.ReplayAsync(date, "UTC", null, null, CancellationToken.None);

        result.FactTimelines.Should().ContainKey("reservoir_is_lower_bound");
        result.FactTimelines["reservoir_is_lower_bound"].Select(p => p.Value).Should().Equal(1m);
    }

    /// <summary>
    /// The engine replays the rule snapshots it is handed, so a dry-run override that tightens
    /// a stored rule's threshold is what it evaluates, not the stored row.
    /// </summary>
    [NativeFact]
    public async Task DryRunOverride_OfAStoredRule_ReplaysAsWritten()
    {
        var ruleId = Guid.NewGuid();
        var dayStart = new DateTime(2026, 4, 28, 0, 0, 0, DateTimeKind.Utc);
        _alertRepository.Setup(r => r.GetEnabledRulesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { ThresholdRule(ruleId, "below", 70m) });
        _glucoseRepository.Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), null, null,
                It.IsAny<int>(), It.IsAny<int>(), false, It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>(), null, It.IsAny<GlucoseStreams>()))
            .ReturnsAsync(new[] { Reading(dayStart.AddHours(1), 90) });
        var ruleOverride = new ReplayRuleOverride(
            ruleId, "tightened", AlertConditionType.Threshold, """{"direction":"below","value":100}""",
            AlertRuleSeverity.Warning, AllowThroughDnd: false, AutoResolveEnabled: false, AutoResolveParams: null);
        var result = await _sut.ReplayDryRunAsync(
            null, null, dayStart, dayStart.AddHours(2), ruleOverride, CancellationToken.None);

        result.Events.Should().ContainSingle().Which.Should().Be(new AlertReplayEvent(
            dayStart.AddHours(1), ruleId, "tightened", AlertRuleSeverity.Warning, AlertReplayEventKind.Fired));
    }

    /// <summary>
    /// Before the first reading the threshold is unknown (docs/alerts/engine-semantics.md §2.4), so
    /// whether the dry run fires there is decided by the rule's on_unknown, as live decides it.
    /// </summary>
    [NativeTheory]
    [InlineData(null, AlertRuleOnUnknown.Met, true)]
    [InlineData(null, AlertRuleOnUnknown.Hold, false)]
    [InlineData(AlertRuleOnUnknown.NotMet, AlertRuleOnUnknown.Met, false)]
    [InlineData(AlertRuleOnUnknown.Met, AlertRuleOnUnknown.Hold, true)]
    public async Task DryRunOverride_reads_an_unknown_root_through_its_on_unknown_or_the_stored_rules(
        string? overrideWord, string storedWord, bool firesOnTheGap)
    {
        var ruleId = Guid.NewGuid();
        var dayStart = new DateTime(2026, 4, 28, 0, 0, 0, DateTimeKind.Utc);
        var stored = ThresholdRule(ruleId, "below", 70m) with { OnUnknown = storedWord };
        _alertRepository.Setup(r => r.GetEnabledRulesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { stored });
        _alertRepository.Setup(r => r.GetRuleAsync(ruleId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AlertRuleRecord(stored, null, true, null, null, []));
        _glucoseRepository.Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), null, null,
                It.IsAny<int>(), It.IsAny<int>(), false, It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>(), null, It.IsAny<GlucoseStreams>()))
            .ReturnsAsync(new[] { Reading(dayStart.AddHours(1), 90) });
        var ruleOverride = new ReplayRuleOverride(
            ruleId, "low", AlertConditionType.Threshold, """{"direction":"below","value":70}""",
            AlertRuleSeverity.Warning, AllowThroughDnd: false, AutoResolveEnabled: false, AutoResolveParams: null,
            OnUnknown: overrideWord);

        var result = await _sut.ReplayDryRunAsync(
            null, null, dayStart, dayStart.AddHours(2), ruleOverride, CancellationToken.None);

        result.Events.Any(e => e.RuleId == ruleId && e.Kind == AlertReplayEventKind.Fired)
            .Should().Be(firesOnTheGap);
    }

    /// <summary>
    /// Under <c>met</c> a stale reading fires (docs/alerts/engine-semantics.md §3), so the rule
    /// fires again after a false reading the first instant past its freshness bound: the
    /// override's own, else the stored rule's, else 11 minutes.
    /// </summary>
    [NativeTheory]
    [InlineData(null, null, 11)]
    [InlineData(null, 30, 30)]
    [InlineData(20, 30, 20)]
    public async Task DryRunOverride_reads_a_stale_reading_through_its_bound_or_the_stored_rules(
        int? overrideMinutes, int? storedMinutes, int boundMinutes)
    {
        var ruleId = Guid.NewGuid();
        var dayStart = new DateTime(2026, 4, 28, 0, 0, 0, DateTimeKind.Utc);
        var stored = ThresholdRule(ruleId, "below", 70m) with
        {
            OnUnknown = AlertRuleOnUnknown.Met,
            GlucoseFreshnessMinutes = storedMinutes,
        };
        _alertRepository.Setup(r => r.GetEnabledRulesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { stored });
        _alertRepository.Setup(r => r.GetRuleAsync(ruleId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AlertRuleRecord(stored, null, true, null, null, []));
        var reading = dayStart.AddHours(1);
        _glucoseRepository.Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), null, null,
                It.IsAny<int>(), It.IsAny<int>(), false, It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>(), null, It.IsAny<GlucoseStreams>()))
            .ReturnsAsync(new[] { Reading(reading, 90) });
        var ruleOverride = new ReplayRuleOverride(
            ruleId, "low", AlertConditionType.Threshold, """{"direction":"below","value":70}""",
            AlertRuleSeverity.Warning, AllowThroughDnd: false, AutoResolveEnabled: false, AutoResolveParams: null,
            GlucoseFreshnessMinutes: overrideMinutes);

        var result = await _sut.ReplayDryRunAsync(
            null, null, dayStart, dayStart.AddHours(2), ruleOverride, CancellationToken.None);

        result.Events.Where(e => e.RuleId == ruleId && e.Kind == AlertReplayEventKind.Fired && e.At > reading)
            .Select(e => e.At).Should().Equal(reading.AddMinutes(boundMinutes).AddTicks(1));
    }

    /// <summary>
    /// Replay ages a reading that arrived live from when it was stored, as live does
    /// (docs/alerts/engine-semantics.md §3, §8): under <c>met</c>, one uploaded 10 or 40 minutes
    /// late fires again only once 11 minutes have passed since its arrival. One stored 3 minutes
    /// before its timestamp, within the clock tolerance, is a live arrival aged from that arrival;
    /// one stored 10 minutes before it is past the tolerance, so it is aged from its timestamp.
    /// </summary>
    [NativeTheory]
    [InlineData(10, 10)]
    [InlineData(40, 40)]
    [InlineData(-3, -3)]
    [InlineData(-10, 0)]
    public async Task DryRunOverride_ages_a_live_upload_from_its_arrival(int delayMinutes, int agedFromMinutes)
    {
        var ruleId = Guid.NewGuid();
        var dayStart = new DateTime(2026, 4, 28, 0, 0, 0, DateTimeKind.Utc);
        var stored = ThresholdRule(ruleId, "below", 70m) with { OnUnknown = AlertRuleOnUnknown.Met };
        _alertRepository.Setup(r => r.GetEnabledRulesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { stored });
        _alertRepository.Setup(r => r.GetRuleAsync(ruleId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AlertRuleRecord(stored, null, true, null, null, []));
        var reading = dayStart.AddHours(1);
        var late = Reading(reading, 90);
        late.CreatedAt = reading.AddMinutes(delayMinutes);
        _glucoseRepository.Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), null, null,
                It.IsAny<int>(), It.IsAny<int>(), false, It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>(), null, It.IsAny<GlucoseStreams>()))
            .ReturnsAsync(new[] { late });
        var ruleOverride = new ReplayRuleOverride(
            ruleId, "low", AlertConditionType.Threshold, """{"direction":"below","value":70}""",
            AlertRuleSeverity.Warning, AllowThroughDnd: false, AutoResolveEnabled: false, AutoResolveParams: null);

        var result = await _sut.ReplayDryRunAsync(
            null, null, dayStart, dayStart.AddHours(2), ruleOverride, CancellationToken.None);

        result.Events.Where(e => e.RuleId == ruleId && e.Kind == AlertReplayEventKind.Fired && e.At > reading)
            .Select(e => e.At).Should().Equal(reading.AddMinutes(agedFromMinutes + 11).AddTicks(1));
    }

    // ── firing search ────────────────────────────────────────────────────────────────────────

    private static readonly DateTime SearchNow = new(2026, 4, 28, 12, 0, 0, DateTimeKind.Utc);

    private AlertReplayService FiringSearch(AlertEvaluationOptions? options = null) =>
        new(
            _alertRepository.Object,
            _dndWindows.Object,
            _glucoseRepository.Object,
            _enricher,
            _tenantAccessor.Object,
            Options.Create(options ?? new AlertEvaluationOptions()),
            new RustAlertReplayEngine(new AlertEngineErrors(
                new Nocturne.API.Tests.Services.BackgroundServices.TestMeterFactory(), TimeProvider.System)),
            new ImportedHistory(_glucoseRepository.Object, new Mock<Nocturne.Core.Contracts.Auth.IOAuthGrantRepository>().Object, TestTenantCache.Create()),
            new Microsoft.Extensions.Time.Testing.FakeTimeProvider(new DateTimeOffset(SearchNow)));

    /// <summary>Serves each replay window the readings inside it, as the repository does.</summary>
    private void SetupReadingsByWindow(params SensorGlucose[] readings) =>
        _glucoseRepository.Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), null, null,
                It.IsAny<int>(), It.IsAny<int>(), false, It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>(), null, It.IsAny<GlucoseStreams>()))
            .Returns((DateTime? from, DateTime? to, string? _, string? _, int _, int _, bool _,
                    DateTime? after, Guid? _, CancellationToken _, Guid? _, GlucoseStreams _) =>
                Task.FromResult<IEnumerable<SensorGlucose>>(readings
                    .Where(r => r.Timestamp >= from && r.Timestamp < to && (after is null || r.Timestamp > after))
                    .OrderBy(r => r.Timestamp)
                    .ToList()));

    private static ReplayRuleOverride LowDraft() => new(
        Id: null, "draft", AlertConditionType.Threshold, """{"direction":"below","value":70}""",
        AlertRuleSeverity.Warning, AllowThroughDnd: false, AutoResolveEnabled: false, AutoResolveParams: null);

    [NativeFact]
    public async Task FindFirings_ReportsEveryFiringNewestFirst()
    {
        var recent = SearchNow.AddHours(-2);
        var older = SearchNow.AddDays(-3).AddHours(-2);
        SetupReadingsByWindow(
            Reading(older, 65), Reading(older.AddMinutes(30), 95),
            Reading(recent, 60), Reading(recent.AddMinutes(30), 95));

        var result = await FiringSearch().FindFiringsAsync(LowDraft(), lastDayOnly: false, CancellationToken.None);

        result.Firings.Should().Equal(
            new AlertFiring(recent, AlertReplayEventKind.Fired),
            new AlertFiring(older, AlertReplayEventKind.Fired));
        result.Total.Should().Be(2);
        result.HasReadings.Should().BeTrue();
        result.SearchedTo.Should().Be(SearchNow);
        result.SearchedFrom.Should().Be(SearchNow.AddDays(-14));
    }

    /// <summary>
    /// Each searched day is its own replay, which starts the rule from rest. A low that runs across
    /// the boundary between two days is one firing, at the time it began.
    /// </summary>
    [NativeFact]
    public async Task FindFirings_ALowAcrossADayBoundaryIsOneFiring()
    {
        var began = SearchNow.AddHours(-25);
        var low = Enumerable.Range(0, 25).Select(i => Reading(began.AddMinutes(i * 5), 60));
        SetupReadingsByWindow([.. low, Reading(began.AddMinutes(125), 95)]);

        var result = await FiringSearch().FindFiringsAsync(LowDraft(), lastDayOnly: false, CancellationToken.None);

        result.Firings.Should().Equal(new AlertFiring(began, AlertReplayEventKind.Fired));
    }

    [NativeFact]
    public async Task FindFirings_EvaluatesTheDraftWithoutTheSavedRules()
    {
        SetupReadingsByWindow(Reading(SearchNow.AddHours(-2), 60));

        await FiringSearch().FindFiringsAsync(LowDraft(), lastDayOnly: false, CancellationToken.None);

        _alertRepository.Verify(r => r.GetEnabledRulesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [NativeFact]
    public async Task FindFirings_AReaderLimitedToADaySearchesThatDayOnly()
    {
        SetupReadingsByWindow(Reading(SearchNow.AddDays(-3), 60), Reading(SearchNow.AddHours(-2), 60));

        var result = await FiringSearch().FindFiringsAsync(LowDraft(), lastDayOnly: true, CancellationToken.None);

        result.SearchedFrom.Should().Be(SearchNow.AddDays(-1));
        result.Firings.Should().Equal(new AlertFiring(SearchNow.AddHours(-2), AlertReplayEventKind.Fired));
    }

    [NativeFact]
    public async Task FindFirings_OutOfTime_ReportsTheSpanItCovered()
    {
        SetupReadingsByWindow(Reading(SearchNow.AddDays(-3), 60));
        var outOfTime = new AlertEvaluationOptions { FiringSearchBudget = TimeSpan.FromTicks(-1) };

        var result = await FiringSearch(outOfTime).FindFiringsAsync(LowDraft(), lastDayOnly: false, CancellationToken.None);

        result.SearchedFrom.Should().Be(SearchNow.AddDays(-1));
        result.Firings.Should().BeEmpty();
    }

    [NativeFact]
    public async Task FindFirings_NoReadings_SaysSo()
    {
        SetupReadingsByWindow();

        var result = await FiringSearch().FindFiringsAsync(LowDraft(), lastDayOnly: false, CancellationToken.None);

        result.HasReadings.Should().BeFalse();
        result.Total.Should().Be(0);
    }
}
