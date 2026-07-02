using System.Reflection;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Nocturne.Alerts.ParityCorpus.Generator.Harness;
using Nocturne.API.Services.Alerts.Engines;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts.Engines;

/// <summary>
/// Shadow-mode contract: the managed result is authoritative and always returned; the
/// secondary engine runs side-effect-free against the pre-state snapshot; agreement is
/// silent, divergence logs structured <c>AlertEngineDivergence</c> warnings with field
/// detail, and secondary failures log <c>AlertEngineShadowError</c> without escaping or
/// persisting anything.
/// </summary>
public class ShadowAlertEngineTests
{
    private static readonly Guid RuleId = Guid.Parse("00000000-0000-0000-0000-0000000000bb");
    private static readonly DateTime T0 = new(2026, 1, 5, 12, 0, 0, DateTimeKind.Utc);

    private static AlertRule BuildThresholdRule() => new()
    {
        Id = RuleId,
        Name = "low",
        ConditionType = AlertConditionType.Threshold,
        ConditionParams = """{"direction":"below","value":70}""",
        ConfirmationReadings = 1,
        HysteresisMinutes = 0,
    };

    private static AlertRuleSnapshot ToSnapshot(AlertRule rule) => new(
        rule.Id, EngineTestHarness.TenantId, rule.Name, rule.ConditionType, rule.ConditionParams,
        AlertRuleSeverity.Warning, "{}", 0, rule.AutoResolveEnabled, rule.AutoResolveParams);

    private static SensorContext LowGlucoseContext() => new()
    {
        LatestValue = 60,
        LatestTimestamp = T0,
        TrendRate = null,
        LastReadingAt = T0,
    };

    private sealed class FakeShadowEvaluator(
        Func<ShadowRuleOutcome>? outcome = null,
        Exception? throws = null) : IShadowRuleEvaluator
    {
        public int Calls { get; private set; }

        public string Name => "fake";

        public Task<ShadowRuleOutcome> EvaluateAsync(
            AlertRule rule, SensorContext context, DateTime now,
            IReadOnlyDictionary<string, DateTime> timers, AlertTrackerState? trackerState, CancellationToken ct)
        {
            Calls++;
            if (throws is not null) throw throws;
            return Task.FromResult(outcome!());
        }
    }

    private static (ShadowAlertEngine Engine, ListLogger<ShadowAlertEngine> Logger,
        RecordingTimerStore TimerStore, InMemoryTrackerRepository TrackerRepo, IAsyncDisposable Provider)
        BuildShadowEngine(AlertRule rule, IShadowRuleEvaluator shadowEvaluator)
    {
        var time = new ManualTimeProvider();
        time.SetUtcNow(T0);
        var timerStore = new RecordingTimerStore();
        var trackerRepo = new InMemoryTrackerRepository([rule]);
        var (managed, provider) = EngineTestHarness.BuildManagedEngine(time, timerStore, trackerRepo);
        var logger = new ListLogger<ShadowAlertEngine>();
        var engine = new ShadowAlertEngine(managed, shadowEvaluator, timerStore, trackerRepo, time, logger);
        return (engine, logger, timerStore, trackerRepo, provider);
    }

    /// <summary>The shadow outcome matching what the managed engine produces for a fresh low-threshold fire.</summary>
    private static ShadowRuleOutcome AgreeingOutcome() => new()
    {
        Root = true,
        Transition = "opened",
        AutoResolved = false,
        PostTimers = new Dictionary<string, DateTime>(),
        PostTrackerState = "active",
        PostConfirmationCount = 0,
        PostHasActiveExcursion = true,
    };

    [Fact]
    public async Task Agreement_produces_no_divergence_log()
    {
        var rule = BuildThresholdRule();
        var fake = new FakeShadowEvaluator(AgreeingOutcome);
        var (engine, logger, _, _, provider) = BuildShadowEngine(rule, fake);
        await using var _ = provider;

        var evaluation = await engine.EvaluateRuleAsync(
            ToSnapshot(rule), LowGlucoseContext(), AlertEngineOptions.Default, CancellationToken.None);

        evaluation.ConditionMet.Should().BeTrue();
        evaluation.Transition.Type.Should().Be(ExcursionTransitionType.ExcursionOpened);
        fake.Calls.Should().Be(1, "the shadow path must actually run");
        logger.Entries.Should().NotContain(e => e.Message.Contains("AlertEngineDivergence"));
        logger.Entries.Should().NotContain(e => e.Message.Contains("AlertEngineShadowError"));
    }

    [Fact]
    public async Task Divergence_is_logged_with_field_detail_and_the_managed_result_is_returned()
    {
        var rule = BuildThresholdRule();
        // The fake disagrees on root truth, transition and post tracker state.
        var fake = new FakeShadowEvaluator(() => new ShadowRuleOutcome
        {
            Root = false,
            Transition = "none",
            AutoResolved = false,
            PostTimers = new Dictionary<string, DateTime>(),
            PostTrackerState = "idle",
            PostConfirmationCount = 0,
            PostHasActiveExcursion = false,
        });
        var (engine, logger, _, _, provider) = BuildShadowEngine(rule, fake);
        await using var _ = provider;

        var evaluation = await engine.EvaluateRuleAsync(
            ToSnapshot(rule), LowGlucoseContext(), AlertEngineOptions.Default, CancellationToken.None);

        // Managed result is authoritative regardless of the divergence.
        evaluation.ConditionMet.Should().BeTrue();
        evaluation.Transition.Type.Should().Be(ExcursionTransitionType.ExcursionOpened);

        var divergences = logger.Entries
            .Where(e => e.Level == LogLevel.Warning && e.Message.Contains("AlertEngineDivergence"))
            .Select(e => e.Message)
            .ToList();
        divergences.Should().Contain(m => m.Contains("field=condition_met") && m.Contains("managed=True") && m.Contains("rust=False"));
        divergences.Should().Contain(m => m.Contains("field=transition") && m.Contains("managed=opened") && m.Contains("rust=none"));
        divergences.Should().Contain(m => m.Contains("field=tracker_state"));
        divergences.Should().Contain(m => m.Contains("field=active_excursion"));
    }

    [Fact]
    public async Task Shadow_failure_is_swallowed_logged_and_persists_nothing()
    {
        var rule = BuildThresholdRule();
        var fake = new FakeShadowEvaluator(throws: new InvalidOperationException("rust exploded"));
        var (engine, logger, timerStore, trackerRepo, provider) = BuildShadowEngine(rule, fake);
        await using var _ = provider;

        var evaluation = await engine.EvaluateRuleAsync(
            ToSnapshot(rule), LowGlucoseContext(), AlertEngineOptions.Default, CancellationToken.None);

        // Managed result still returned, no exception escaped.
        evaluation.ConditionMet.Should().BeTrue();
        evaluation.Transition.Type.Should().Be(ExcursionTransitionType.ExcursionOpened);

        logger.Entries.Should().ContainSingle(e =>
            e.Level == LogLevel.Warning && e.Message.Contains("AlertEngineShadowError") && e.Exception is InvalidOperationException);
        logger.Entries.Should().NotContain(e => e.Message.Contains("AlertEngineDivergence"));

        // Nothing persisted by the shadow path: the only state is what the managed engine
        // wrote (tracker active, no timers).
        timerStore.DrainOps().Should().HaveCount(0);
        (await timerStore.GetAllForRuleAsync(RuleId, CancellationToken.None)).Should().BeEmpty();
        var state = await trackerRepo.GetTrackerStateAsync(RuleId, CancellationToken.None);
        state!.State.Should().Be("active", "the managed write is the only persisted state");
    }

    [NativeFact]
    public async Task Real_rust_shadow_agrees_with_the_managed_engine()
    {
        var rule = BuildThresholdRule();
        var (engine, logger, _, _, provider) = BuildShadowEngine(rule, new RustShadowRuleEvaluator());
        await using var _ = provider;

        // Two ticks: open at 60, hysteresis at 120 — both must agree end to end.
        var evaluation = await engine.EvaluateRuleAsync(
            ToSnapshot(rule), LowGlucoseContext(), AlertEngineOptions.Default, CancellationToken.None);
        evaluation.Transition.Type.Should().Be(ExcursionTransitionType.ExcursionOpened);

        var normalContext = LowGlucoseContext() with { LatestValue = 120 };
        var second = await engine.EvaluateRuleAsync(
            ToSnapshot(rule), normalContext, AlertEngineOptions.Default, CancellationToken.None);
        second.Transition.Type.Should().Be(ExcursionTransitionType.HysteresisStarted);

        logger.Entries.Should().NotContain(e => e.Message.Contains("AlertEngineDivergence"));
        logger.Entries.Should().NotContain(e => e.Message.Contains("AlertEngineShadowError"));
    }
}

/// <summary>
/// Completeness guard for <see cref="ShadowAlertEngine"/>'s CompareAsync: every public
/// property of <see cref="AlertEngineEvaluation"/> must appear in exactly one of the two
/// sets below, so adding a new observable to the evaluation record forces a conscious
/// compare-or-exclude decision instead of silently escaping shadow comparison.
/// </summary>
public class ShadowComparatorCompletenessTests
{
    /// <summary>
    /// Properties CompareAsync compares (directly or via a wire projection):
    /// Skipped → "skipped", ConditionMet → "condition_met",
    /// Transition → "transition" + "close_reason", AutoResolved → "auto_resolved".
    /// The remaining compared fields (timers, tracker_state, confirmation_count,
    /// active_excursion) are post-state read from the stores, not evaluation properties.
    /// </summary>
    private static readonly IReadOnlySet<string> ComparedProperties = new HashSet<string>(StringComparer.Ordinal)
    {
        nameof(AlertEngineEvaluation.Skipped),
        nameof(AlertEngineEvaluation.ConditionMet),
        nameof(AlertEngineEvaluation.Transition),
        nameof(AlertEngineEvaluation.AutoResolved),
    };

    /// <summary>
    /// Properties deliberately not compared:
    /// AutoResolveTransition — its observable effect is already covered by the
    /// AutoResolved projection plus the tracker post-state comparisons;
    /// LeafValues — replay-only leaf log; the shadow path runs with live options,
    /// which never request leaf values.
    /// </summary>
    private static readonly IReadOnlySet<string> ExcludedProperties = new HashSet<string>(StringComparer.Ordinal)
    {
        nameof(AlertEngineEvaluation.AutoResolveTransition),
        nameof(AlertEngineEvaluation.LeafValues),
    };

    [Fact]
    public void Every_evaluation_property_is_compared_or_deliberately_excluded()
    {
        ComparedProperties.Intersect(ExcludedProperties).Should().BeEmpty(
            "a property cannot be both compared and excluded");

        var unaccounted = typeof(AlertEngineEvaluation)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .Except(ComparedProperties.Union(ExcludedProperties))
            .ToList();

        unaccounted.Should().BeEmpty(
            "every public property of AlertEngineEvaluation must be either compared by " +
            "ShadowAlertEngine.CompareAsync or explicitly listed as excluded here; decide for: {0}",
            string.Join(", ", unaccounted));
    }
}
