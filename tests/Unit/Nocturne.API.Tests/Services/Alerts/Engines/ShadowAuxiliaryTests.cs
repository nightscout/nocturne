using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.Alerts.ParityCorpus.Generator.Harness;
using Nocturne.API.Services.Alerts;
using Nocturne.API.Services.Alerts.Engines;
using Nocturne.API.Services.Alerts.Evaluators;
using Nocturne.API.Tests.Services.BackgroundServices;
using Nocturne.API.Tests.TestDoubles;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts.Engines;

/// <summary>
/// Shadow mode outside the per-rule evaluation: smart-snooze nodes, the sweep's auto-resolve and
/// the tracker operations are compared too, with the managed result returned either way.
/// </summary>
public class ShadowAuxiliaryTests
{
    private static readonly Guid RuleId = Guid.Parse("00000000-0000-0000-0000-0000000000f1");
    private static readonly DateTime T0 = new(2026, 1, 5, 12, 0, 0, DateTimeKind.Utc);

    private static readonly ConditionNode LowNode = JsonSerializer.Deserialize<ConditionNode>(
        """{"type":"threshold","threshold":{"direction":"below","value":70}}""", EvaluatorJson.Options)!;

    private static AlertRule Rule() => new()
    {
        Id = RuleId,
        Name = "low",
        ConditionType = AlertConditionType.Threshold,
        ConditionParams = """{"direction":"below","value":70}""",
        ConfirmationReadings = 1,
        AutoResolveEnabled = true,
        AutoResolveParams = """{"type":"threshold","threshold":{"direction":"above","value":80}}""",
    };

    private static AlertRuleSnapshot Snapshot(AlertRule rule) => new(
        rule.Id, EngineTestHarness.TenantId, rule.Name, rule.ConditionType, rule.ConditionParams,
        AlertRuleSeverity.Warning, "{}", 0, rule.AutoResolveEnabled, rule.AutoResolveParams);

    private static SensorContext Glucose(decimal value) => new()
    {
        LatestValue = value,
        LatestTimestamp = T0,
        TrendRate = null,
        LastReadingAt = T0,
    };

    private static IEnumerable<string> Divergences<T>(ListLogger<T> logger) =>
        logger.Entries
            .Where(e => e.Level == LogLevel.Warning && e.Message.Contains("AlertEngineDivergence"))
            .Select(e => e.Message);

    private sealed class FixedShadow(ShadowNodeOutcome? nodeOutcome = null, ShadowAutoResolveOutcome? autoResolveOutcome = null)
        : IShadowRuleEvaluator
    {
        public string Name => "fake";

        public Task<ShadowRuleOutcome> EvaluateAsync(
            AlertRule rule, SensorContext context, DateTime now,
            IReadOnlyDictionary<string, DateTime> timers, AlertTrackerState? trackerState, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<ShadowNodeOutcome> EvaluateNodeAsync(
            Guid ruleId, ConditionNode node, string pathRoot, SensorContext context, DateTime now,
            IReadOnlyDictionary<string, DateTime> timers, CancellationToken ct) =>
            Task.FromResult(nodeOutcome!);

        public Task<ShadowAutoResolveOutcome> EvaluateAutoResolveAsync(
            AlertRuleSnapshot rule, SensorContext context, DateTime now,
            IReadOnlyDictionary<string, DateTime> timers, AlertTrackerState? trackerState, CancellationToken ct) =>
            Task.FromResult(autoResolveOutcome!);
    }

    private static (ShadowAlertEngine Engine, ListLogger<ShadowAlertEngine> Logger, InMemoryTrackerRepository Repository, IAsyncDisposable Provider)
        Build(AlertRule rule, IShadowRuleEvaluator shadow)
    {
        var time = new ManualTimeProvider();
        time.SetUtcNow(T0);
        var timers = new RecordingTimerStore();
        var repository = new InMemoryTrackerRepository([rule]);
        var (managed, provider) = EngineTestHarness.BuildManagedEngine(time, timers, repository);
        var logger = new ListLogger<ShadowAlertEngine>();
        return (new ShadowAlertEngine(managed, shadow, timers, repository, time, logger), logger, repository, provider);
    }

    private static RustShadowRuleEvaluator RustShadow() => new(
        new AlertEngineErrors(new TestMeterFactory(), TimeProvider.System),
        NullLogger<RustShadowRuleEvaluator>.Instance);

    [Fact]
    public async Task A_node_divergence_is_logged_under_its_path_root_and_managed_wins()
    {
        var shadow = new FixedShadow(nodeOutcome: new ShadowNodeOutcome(false, new Dictionary<string, DateTime>()));
        var (engine, logger, _, provider) = Build(Rule(), shadow);
        await using var _ = provider;

        var value = await engine.EvaluateNodeAsync(RuleId, LowNode, Glucose(60), "snooze", CancellationToken.None);

        value.Should().BeTrue();
        Divergences(logger).Should().ContainSingle(m =>
            m.Contains("field=snooze.value") && m.Contains("managed=True") && m.Contains("rust=False"));
    }

    [NativeFact]
    public async Task The_rust_node_shadow_agrees_with_the_managed_engine()
    {
        var (engine, logger, _, provider) = Build(Rule(), RustShadow());
        await using var _ = provider;

        (await engine.EvaluateNodeAsync(RuleId, LowNode, Glucose(60), "snooze", CancellationToken.None)).Should().BeTrue();
        (await engine.EvaluateNodeAsync(RuleId, LowNode, Glucose(90), "snooze", CancellationToken.None)).Should().BeFalse();

        logger.Entries.Should().NotContain(e => e.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task An_auto_resolve_divergence_is_logged_and_managed_wins()
    {
        var rule = Rule();
        var shadow = new FixedShadow(autoResolveOutcome: new ShadowAutoResolveOutcome(
            ExcursionTransitionType.None, null, new Dictionary<string, DateTime>(),
            new TrackerPostState("active", 0, true, T0, null)));
        var (engine, logger, repository, provider) = Build(rule, shadow);
        await using var _ = provider;
        await engine.EvaluateRuleAsync(Snapshot(rule), Glucose(60), AlertEngineOptions.Default, CancellationToken.None);
        logger.Entries.Clear();

        var resolved = await engine.EvaluateAutoResolveAsync(Snapshot(rule), Glucose(100), CancellationToken.None);

        resolved.Type.Should().Be(ExcursionTransitionType.ExcursionClosed);
        (await repository.GetTrackerStateAsync(RuleId))!.State.Should().Be("idle");
        Divergences(logger).Should().Contain(m =>
            m.Contains("field=auto_resolve.transition") && m.Contains("managed=closed") && m.Contains("rust=none"));
        Divergences(logger).Should().Contain(m => m.Contains("field=auto_resolve.close_reason"));
        Divergences(logger).Should().Contain(m => m.Contains("field=auto_resolve.tracker_state"));
    }

    [NativeFact]
    public async Task The_rust_auto_resolve_shadow_agrees_with_the_managed_engine()
    {
        var rule = Rule();
        var (engine, logger, _, provider) = Build(rule, RustShadow());
        await using var _ = provider;
        await engine.EvaluateRuleAsync(Snapshot(rule), Glucose(60), AlertEngineOptions.Default, CancellationToken.None);

        (await engine.EvaluateAutoResolveAsync(Snapshot(rule), Glucose(65), CancellationToken.None))
            .Type.Should().Be(ExcursionTransitionType.None);
        (await engine.EvaluateAutoResolveAsync(Snapshot(rule), Glucose(100), CancellationToken.None))
            .Type.Should().Be(ExcursionTransitionType.ExcursionClosed);

        logger.Entries.Should().NotContain(e => e.Level >= LogLevel.Warning);
    }

    private sealed class FixedDecider(TrackerDecision decision) : IExcursionDecider
    {
        public TrackerDecision Process(Guid ruleId, AlertTrackerState? state, TrackerConfig config, bool conditionMet, DateTime now) => decision;

        public TrackerDecision ForceClose(Guid ruleId, AlertTrackerState? state, ExcursionCloseReason reason, DateTime now) => decision;

        public TrackerDecision CloseElapsedHysteresis(Guid ruleId, AlertTrackerState? state, TrackerConfig config, DateTime now) => decision;
    }

    private sealed class ThrowingDecider : IExcursionDecider
    {
        public TrackerDecision Process(Guid ruleId, AlertTrackerState? state, TrackerConfig config, bool conditionMet, DateTime now) => throw new InvalidOperationException();

        public TrackerDecision ForceClose(Guid ruleId, AlertTrackerState? state, ExcursionCloseReason reason, DateTime now) => throw new InvalidOperationException();

        public TrackerDecision CloseElapsedHysteresis(Guid ruleId, AlertTrackerState? state, TrackerConfig config, DateTime now) => throw new InvalidOperationException();
    }

    private static AlertTrackerState Hysteresis() => new()
    {
        AlertRuleId = RuleId,
        State = "hysteresis",
        ActiveExcursionId = Guid.NewGuid(),
        UpdatedAt = T0.AddMinutes(-20),
        HysteresisStartedAt = T0.AddMinutes(-20),
    };

    [Fact]
    public void A_tracker_decision_divergence_is_logged_and_the_managed_decision_returned()
    {
        var logger = new ListLogger<ShadowExcursionDecider>();
        var shadow = new ShadowExcursionDecider(
            ManagedExcursionDecider.Instance,
            new FixedDecider(new TrackerDecision(ExcursionTransitionType.None, null, TrackerPostState.Of(Hysteresis()))),
            logger);

        var decision = shadow.CloseElapsedHysteresis(RuleId, Hysteresis(), new TrackerConfig(1, 10), T0);

        decision.Type.Should().Be(ExcursionTransitionType.ExcursionClosed);
        Divergences(logger).Should().Contain(m =>
            m.Contains("field=tracker_close_elapsed_hysteresis.transition") && m.Contains("managed=closed"));
        Divergences(logger).Should().Contain(m => m.Contains("field=tracker_close_elapsed_hysteresis.tracker_state"));
    }

    [Fact]
    public void A_failing_shadow_decider_is_logged_and_never_reaches_the_caller()
    {
        var logger = new ListLogger<ShadowExcursionDecider>();
        var shadow = new ShadowExcursionDecider(ManagedExcursionDecider.Instance, new ThrowingDecider(), logger);

        var decision = shadow.ForceClose(RuleId, Hysteresis(), ExcursionCloseReason.Manual, T0);

        decision.Type.Should().Be(ExcursionTransitionType.ExcursionClosed);
        logger.Entries.Should().ContainSingle(e =>
            e.Message.Contains("AlertEngineShadowError") && e.Message.Contains("stage=tracker_force_close"));
    }

    [NativeFact]
    public async Task The_rust_tracker_shadow_agrees_with_the_managed_decider_through_a_lifecycle()
    {
        var logger = new ListLogger<ShadowExcursionDecider>();
        var time = new ManualTimeProvider();
        time.SetUtcNow(T0);
        var repository = new RecordingTrackerRepository(new AlertRule { Id = RuleId, Name = "low", HysteresisMinutes = 10 });
        var tracker = new ExcursionTracker(
            repository, new AlertRuleEvaluationGate(), time, NullLogger<ExcursionTracker>.Instance,
            new ShadowExcursionDecider(
                ManagedExcursionDecider.Instance,
                new RustExcursionDecider(new AlertEngineErrors(new TestMeterFactory(), time), AlertEngineErrors.ShadowEngine),
                logger));

        await tracker.ProcessEvaluationAsync(RuleId, true, CancellationToken.None);
        await tracker.ProcessEvaluationAsync(RuleId, false, CancellationToken.None);
        time.SetUtcNow(T0.AddMinutes(5));
        await tracker.CloseElapsedHysteresisAsync(RuleId, CancellationToken.None);
        await tracker.ProcessEvaluationAsync(RuleId, true, CancellationToken.None);
        await tracker.ForceCloseAsync(RuleId, ExcursionCloseReason.Manual, CancellationToken.None);
        await tracker.ProcessEvaluationAsync(RuleId, true, CancellationToken.None);
        await tracker.ProcessEvaluationAsync(RuleId, false, CancellationToken.None);
        time.SetUtcNow(T0.AddMinutes(20));
        var closed = await tracker.CloseElapsedHysteresisAsync(RuleId, CancellationToken.None);

        closed.Type.Should().Be(ExcursionTransitionType.ExcursionClosed);
        logger.Entries.Should().NotContain(e => e.Level >= LogLevel.Warning);
    }
}
