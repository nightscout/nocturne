using System.Text.Json;
using FluentAssertions;
using Nocturne.Alerts.ParityCorpus.Generator.Harness;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts.Engines;

/// <summary>
/// Sustained-timer continuity across engines: a timer row written by one engine must be
/// honoured (not reset) by the other, because the engines share the
/// <c>alert_condition_timers</c> rows keyed by <c>(rule_id, path)</c> and a cutover (or a
/// shadow/rust rollback) must not restart in-flight sustained windows.
/// </summary>
public class TimerContinuityTests
{
    private static readonly Guid RuleId = Guid.Parse("00000000-0000-0000-0000-0000000000aa");
    private static readonly DateTime T0 = new(2026, 1, 5, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>sustained{10 min, threshold below 70} as the rule's root condition.</summary>
    private static AlertRule BuildSustainedRule() => new()
    {
        Id = RuleId,
        Name = "sustained low",
        ConditionType = AlertConditionType.Sustained,
        ConditionParams =
            """{"minutes":10,"child":{"type":"threshold","threshold":{"direction":"below","value":70}}}""",
        ConfirmationReadings = 1,
        HysteresisMinutes = 0,
    };

    private static AlertRuleSnapshot ToSnapshot(AlertRule rule) => new(
        rule.Id, EngineTestHarness.TenantId, rule.Name, rule.ConditionType, rule.ConditionParams,
        AlertRuleSeverity.Warning, "{}", 0, rule.AutoResolveEnabled, rule.AutoResolveParams);

    private static SensorContext LowGlucoseContext(DateTime at) => new()
    {
        LatestValue = 60,
        LatestTimestamp = at,
        TrendRate = null,
        LastReadingAt = at,
    };

    [NativeFact]
    public async Task Timer_row_written_by_the_managed_engine_is_honoured_by_the_rust_engine()
    {
        var rule = BuildSustainedRule();
        var time = new ManualTimeProvider();
        var timerStore = new RecordingTimerStore();
        var trackerRepo = new InMemoryTrackerRepository([rule]);
        var snapshot = ToSnapshot(rule);

        // Managed engine starts the sustained window at T0 (child true, no timer → set, root false).
        var (managed, provider) = EngineTestHarness.BuildManagedEngine(time, timerStore, trackerRepo);
        await using var _ = provider;
        time.SetUtcNow(T0);
        var first = await managed.EvaluateRuleAsync(snapshot, LowGlucoseContext(T0), AlertEngineOptions.Default, CancellationToken.None);
        first.ConditionMet.Should().BeFalse("the window starts on the first true tick");
        timerStore.DrainOps().Should().ContainSingle(op => op.Op == "set" && op.Path == "sustained" && op.At == T0);

        // The rust engine picks up the same store mid-window: at T0+5 the window is not
        // complete (row honoured, NOT reset)...
        var rust = EngineTestHarness.BuildRustEngine(time, timerStore, trackerRepo);
        time.SetUtcNow(T0.AddMinutes(5));
        var mid = await rust.EvaluateRuleAsync(snapshot, LowGlucoseContext(T0.AddMinutes(5)), AlertEngineOptions.Default, CancellationToken.None);
        mid.ConditionMet.Should().BeFalse("only 5 of 10 sustained minutes have elapsed since the managed-written first-true");
        timerStore.DrainOps().Should().BeEmpty("an in-flight window must not be re-set or cleared");

        // ...and at T0+10 it completes exactly on the managed-written schedule.
        time.SetUtcNow(T0.AddMinutes(10));
        var done = await rust.EvaluateRuleAsync(snapshot, LowGlucoseContext(T0.AddMinutes(10)), AlertEngineOptions.Default, CancellationToken.None);
        done.ConditionMet.Should().BeTrue("10 sustained minutes elapsed since the managed-written first-true");
        done.Transition.Type.Should().Be(ExcursionTransitionType.ExcursionOpened);
        (await timerStore.GetFirstTrueAsync(RuleId, "sustained", CancellationToken.None))
            .Should().Be(T0, "the first-true instant the managed engine recorded must survive untouched");
    }

    [NativeFact]
    public async Task Timer_row_written_by_the_rust_engine_is_honoured_by_the_managed_engine()
    {
        var rule = BuildSustainedRule();
        var time = new ManualTimeProvider();
        var timerStore = new RecordingTimerStore();
        var trackerRepo = new InMemoryTrackerRepository([rule]);
        var snapshot = ToSnapshot(rule);

        // Rust engine starts the window at T0.
        var rust = EngineTestHarness.BuildRustEngine(time, timerStore, trackerRepo);
        time.SetUtcNow(T0);
        var first = await rust.EvaluateRuleAsync(snapshot, LowGlucoseContext(T0), AlertEngineOptions.Default, CancellationToken.None);
        first.ConditionMet.Should().BeFalse("the window starts on the first true tick");
        timerStore.DrainOps().Should().ContainSingle(op => op.Op == "set" && op.Path == "sustained" && op.At == T0);
        (await timerStore.GetFirstTrueAsync(RuleId, "sustained", CancellationToken.None))
            .Should().Be(T0, "the rust engine persists the first-true instant through the shared store");

        // The managed engine completes it on schedule.
        var (managed, provider) = EngineTestHarness.BuildManagedEngine(time, timerStore, trackerRepo);
        await using var _ = provider;
        time.SetUtcNow(T0.AddMinutes(10));
        var done = await managed.EvaluateRuleAsync(snapshot, LowGlucoseContext(T0.AddMinutes(10)), AlertEngineOptions.Default, CancellationToken.None);
        done.ConditionMet.Should().BeTrue("10 sustained minutes elapsed since the rust-written first-true");
        done.Transition.Type.Should().Be(ExcursionTransitionType.ExcursionOpened);
        (await timerStore.GetFirstTrueAsync(RuleId, "sustained", CancellationToken.None))
            .Should().Be(T0, "the first-true instant the rust engine recorded must survive untouched");
    }

    [NativeFact]
    public async Task Aux_scope_timers_thread_through_the_rust_evaluate_node_path()
    {
        // EvaluateNodeAsync (the snooze / sweep auto-resolve surface) must honour and
        // persist (rule_id, path) timers exactly like the rule-body path.
        var rule = BuildSustainedRule();
        var time = new ManualTimeProvider();
        var timerStore = new RecordingTimerStore();
        var trackerRepo = new InMemoryTrackerRepository([rule]);
        var rust = EngineTestHarness.BuildRustEngine(time, timerStore, trackerRepo);

        var node = JsonSerializer.Deserialize<ConditionNode>(
            """{"type":"sustained","sustained":{"minutes":10,"child":{"type":"threshold","threshold":{"direction":"below","value":70}}}}""",
            Nocturne.API.Services.Alerts.Evaluators.EvaluatorJson.Options)!;

        time.SetUtcNow(T0);
        var first = await rust.EvaluateNodeAsync(RuleId, node, LowGlucoseContext(T0), "snooze", CancellationToken.None);
        first.Should().BeFalse("the window starts on the first true tick");
        (await timerStore.GetFirstTrueAsync(RuleId, "snooze", CancellationToken.None))
            .Should().Be(T0, "aux-scope timers key under the path root, not the rule body");

        time.SetUtcNow(T0.AddMinutes(10));
        var done = await rust.EvaluateNodeAsync(RuleId, node, LowGlucoseContext(T0.AddMinutes(10)), "snooze", CancellationToken.None);
        done.Should().BeTrue("10 sustained minutes elapsed since the recorded first-true");
    }
}
