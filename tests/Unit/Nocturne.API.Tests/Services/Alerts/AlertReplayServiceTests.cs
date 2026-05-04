using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Nocturne.API.Configuration;
using Nocturne.API.Services.Alerts;
using Nocturne.API.Services.Glucose;
using Nocturne.API.Services.Treatments;
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
    private readonly Mock<ISensorGlucoseRepository> _glucoseRepository = new();
    private readonly Mock<ITenantAccessor> _tenantAccessor = new();
    // Looping-fact mocks plumbed through the real SensorContextEnricher so replay walks
    // the same code path as the live engine. Default-empty Moq returns mean cold-start
    // semantics by default; individual tests override what they care about.
    private readonly Mock<IIobService> _iobService = new();
    private readonly Mock<ICobService> _cobService = new();
    private readonly Mock<ITreatmentService> _treatmentService = new();
    private readonly Mock<IDeviceEventRepository> _deviceEventRepository = new();
    private readonly Mock<IPumpSnapshotRepository> _pumpSnapshotRepository = new();
    private readonly Mock<IApsSnapshotRepository> _apsSnapshotRepository = new();
    private readonly Mock<ITempBasalRepository> _tempBasalRepository = new();
    private readonly Mock<IUploaderSnapshotRepository> _uploaderSnapshotRepository = new();
    private readonly Mock<IStateSpanService> _stateSpanService = new();
    private readonly AlertReplayService _sut;

    private readonly Guid _tenantId = Guid.NewGuid();

    public AlertReplayServiceTests()
    {
        _tenantAccessor.Setup(t => t.TenantId).Returns(_tenantId);

        var enricherDeps = new SensorContextEnricherDependencies(
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
            new Mock<Nocturne.Core.Contracts.V4.Repositories.ITargetRangeScheduleRepository>().Object,
            new Mock<Nocturne.Core.Contracts.Profiles.Resolvers.IActiveProfileResolver>().Object,
            new Mock<Nocturne.Core.Contracts.Profiles.Resolvers.ITherapySettingsResolver>().Object,
            Options.Create(new AlertEvaluationOptions()));
        var enricher = new SensorContextEnricher(
            enricherDeps,
            new ServiceCollection().BuildServiceProvider(),
            TimeProvider.System,
            NullLogger<SensorContextEnricher>.Instance);

        _sut = new AlertReplayService(
            _alertRepository.Object,
            _glucoseRepository.Object,
            enricher,
            _tenantAccessor.Object,
            NullLogger<AlertReplayService>.Instance);
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
        Mgdl = mgdl,
    };

    [Fact]
    public async Task EmptyTenantId_ReturnsEmpty()
    {
        _tenantAccessor.Setup(t => t.TenantId).Returns(Guid.Empty);

        var result = await _sut.ReplayAsync(null, null, null, null, CancellationToken.None);

        result.Events.Should().BeEmpty();
    }

    [Fact]
    public async Task NoRules_ReturnsEmptyEventsButValidWindow()
    {
        _alertRepository.Setup(r => r.GetEnabledRulesAsync(_tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AlertRuleSnapshot>());

        var result = await _sut.ReplayAsync(
            new DateOnly(2026, 4, 28), "UTC", null, null, CancellationToken.None);

        result.Events.Should().BeEmpty();
        result.WindowStart.Should().Be(new DateTime(2026, 4, 28, 0, 0, 0, DateTimeKind.Utc));
        result.WindowEnd.Should().Be(new DateTime(2026, 4, 29, 0, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public async Task ThresholdBelow_LowReading_FiresOneEventAtLeadingEdge()
    {
        var ruleId = Guid.NewGuid();
        var rule = ThresholdRule(ruleId, "below", 70m);
        var date = new DateOnly(2026, 4, 28);
        var dayStart = new DateTime(2026, 4, 28, 0, 0, 0, DateTimeKind.Utc);

        _alertRepository.Setup(r => r.GetEnabledRulesAsync(_tenantId, It.IsAny<CancellationToken>()))
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
                It.IsAny<int>(), It.IsAny<int>(), false, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(readings);

        var result = await _sut.ReplayAsync(date, "UTC", null, null, CancellationToken.None);

        result.Events.Should().HaveCount(2);
        result.Events[0].RuleId.Should().Be(ruleId);
        result.Events[0].At.Should().Be(dayStart.AddHours(4));
        result.Events[1].At.Should().Be(dayStart.AddHours(5));
    }

    [Fact]
    public async Task ContinuouslyMet_ProducesSingleLeadingEdgeEvent()
    {
        var rule = ThresholdRule(Guid.NewGuid(), "below", 70m);
        var date = new DateOnly(2026, 4, 28);
        var dayStart = new DateTime(2026, 4, 28, 0, 0, 0, DateTimeKind.Utc);

        _alertRepository.Setup(r => r.GetEnabledRulesAsync(_tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { rule });

        // Continuous low for an hour — should produce ONE event at the first low tick.
        var readings = Enumerable.Range(0, 12)
            .Select(i => Reading(dayStart.AddHours(2).AddMinutes(i * 5), 60))
            .ToArray();
        _glucoseRepository.Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), null, null,
                It.IsAny<int>(), It.IsAny<int>(), false, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(readings);

        var result = await _sut.ReplayAsync(date, "UTC", null, null, CancellationToken.None);

        result.Events.Should().HaveCount(1);
        result.Events[0].At.Should().Be(dayStart.AddHours(2));
    }

    [Fact]
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
        _alertRepository.Setup(r => r.GetEnabledRulesAsync(_tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { ruleB, ruleA });

        _glucoseRepository.Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), null, null,
                It.IsAny<int>(), It.IsAny<int>(), false, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { Reading(dayStart.AddHours(3), 60) });

        var result = await _sut.ReplayAsync(date, "UTC", null, null, CancellationToken.None);

        // Both should fire. A first (its event was added first within the tick), then B.
        result.Events.Should().HaveCount(2);
        result.Events[0].RuleId.Should().Be(ruleAId);
        result.Events[1].RuleId.Should().Be(ruleBId);
    }

    [Fact]
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

        _alertRepository.Setup(r => r.GetEnabledRulesAsync(_tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { rule });
        // First reading at 03:00 satisfies both predicates (open + immediate resolve);
        // second reading at 03:05 is well above both so subsequent ticks stay quiet and the
        // assertion locks the same-tick pair in isolation.
        _glucoseRepository.Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), null, null,
                It.IsAny<int>(), It.IsAny<int>(), false, false, It.IsAny<CancellationToken>()))
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

    [Fact]
    public async Task RollingWindow_When_Date_IsNull()
    {
        _alertRepository.Setup(r => r.GetEnabledRulesAsync(_tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AlertRuleSnapshot>());

        var result = await _sut.ReplayAsync(null, null, null, null, CancellationToken.None);

        (result.WindowEnd - result.WindowStart).Should().BeCloseTo(TimeSpan.FromHours(24), TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task LoopStale_fires_at_correct_replay_tick()
    {
        // ApsSnapshot at dayStart+02:00; thereafter loop is silent. A loop_stale > 5 rule
        // should first fire at the leading edge once 5+ minutes have elapsed since the last
        // cycle (i.e. 02:10, the first tick after 02:00 + 5min).
        var ruleId = Guid.NewGuid();
        var rule = new AlertRuleSnapshot(ruleId, _tenantId, "loop-stale-5",
            AlertConditionType.LoopStale,
            """{"operator":">","minutes":5}""",
            AlertRuleSeverity.Warning, "{}", 0, false, null);

        var date = new DateOnly(2026, 4, 28);
        var dayStart = new DateTime(2026, 4, 28, 0, 0, 0, DateTimeKind.Utc);
        var lastCycle = dayStart.AddHours(2);

        _alertRepository.Setup(r => r.GetEnabledRulesAsync(_tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { rule });
        _glucoseRepository.Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), null, null,
                It.IsAny<int>(), It.IsAny<int>(), false, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<SensorGlucose>());

        // The enricher uses GetLatestTimestampAsync(asOf) — pinned to each replay tick. We
        // emulate "no cycle ever happened before lastCycle, then lastCycle is the only one"
        // by returning lastCycle when asOf >= lastCycle and null otherwise.
        _apsSnapshotRepository.Setup(r => r.GetLatestTimestampAsync(It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DateTime? asOf, CancellationToken _) =>
                asOf is not null && asOf.Value >= lastCycle ? lastCycle : null);

        var result = await _sut.ReplayAsync(date, "UTC", null, null, CancellationToken.None);

        result.Events.Should().NotBeEmpty();
        result.Events[0].RuleId.Should().Be(ruleId);
        // Tick cadence is 5 minutes; the first tick where elapsed > 5 min is 02:10.
        result.Events[0].At.Should().Be(dayStart.AddHours(2).AddMinutes(10));
    }

    [Fact]
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

        _alertRepository.Setup(r => r.GetEnabledRulesAsync(_tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { rule });
        _glucoseRepository.Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), null, null,
                It.IsAny<int>(), It.IsAny<int>(), false, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<SensorGlucose>());

        _pumpSnapshotRepository.Setup(r => r.GetLatestAsync(It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DateTime? asOf, CancellationToken _) =>
                asOf is null ? null : new PumpSnapshot { Timestamp = asOf.Value.AddMinutes(-1), BatteryPercent = 80 });

        _stateSpanService.Setup(s => s.GetActiveAtAsync(
                StateSpanCategory.PumpMode, PumpModeState.Suspended.ToString(),
                It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((StateSpanCategory _, string? _, DateTime at, CancellationToken _) =>
                at >= spanStart
                    ? new StateSpan
                    {
                        Category = StateSpanCategory.PumpMode,
                        State = PumpModeState.Suspended.ToString(),
                        StartTimestamp = spanStart,
                    }
                    : null);

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
    [Fact]
    public async Task FactTimelines_LatestGlucose_EmitsOnReadingChange()
    {
        var rule = ThresholdRule(Guid.NewGuid(), "below", 70m);
        var date = new DateOnly(2026, 4, 28);
        var dayStart = new DateTime(2026, 4, 28, 0, 0, 0, DateTimeKind.Utc);

        _alertRepository.Setup(r => r.GetEnabledRulesAsync(_tenantId, It.IsAny<CancellationToken>()))
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
                It.IsAny<int>(), It.IsAny<int>(), false, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(readings);

        var result = await _sut.ReplayAsync(date, "UTC", null, null, CancellationToken.None);

        result.FactTimelines.Should().ContainKey("latest_glucose");
        var glucosePoints = result.FactTimelines["latest_glucose"];
        glucosePoints.Select(p => p.Value).Should().ContainInOrder(65m, 95m, 60m);
    }

    /// <summary>
    /// Drift guard: every <see cref="AlertConditionType"/> evaluable at runtime must resolve to
    /// an evaluator in the replay container. Replay used to hand-maintain its own evaluator
    /// list and silently dropped any new condition kind (Sustained around a missing kind cleared
    /// the timer every tick, so no event ever fired). Sourcing the registry from the live DI
    /// extension closes that gap; this test fails closed if a future evaluator is added to live
    /// DI but not exposed to replay.
    /// </summary>
    [Fact]
    public void BuildReplayServices_ResolvesEveryRuntimeConditionType()
    {
        using var sp = AlertReplayService.BuildReplayServices(
            new InMemoryConditionTimerStore(), TimeProvider.System);
        var registry = sp.GetRequiredService<Nocturne.API.Services.Alerts.Evaluators.ConditionEvaluatorRegistry>();

        // SignalLoss is not condition-evaluator-driven — AlertSweepService handles it directly.
        var skipped = new[] { AlertConditionType.SignalLoss };

        foreach (var type in Enum.GetValues<AlertConditionType>())
        {
            if (skipped.Contains(type)) continue;
            registry.GetEvaluator(type).Should().NotBeNull(
                "replay must support every runtime condition type, but {0} has no evaluator", type);
        }
    }
}
