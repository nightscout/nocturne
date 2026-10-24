using System.Text.Json;
using FluentAssertions;
using Nocturne.Alerts.Corpus;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts.Engines;

/// <summary>
/// Sustained-timer continuity across engine instances: the <c>alert_condition_timers</c> rows
/// keyed by <c>(rule_id, path)</c> outlive the process, so a restarted engine must honour (not
/// reset) an in-flight sustained window.
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
    public async Task A_sustained_window_reads_its_first_true_from_the_timer_store()
    {
        var rule = BuildSustainedRule();
        var time = new ManualTimeProvider();
        var trackerRepo = new InMemoryTrackerRepository([rule]);
        var snapshot = ToSnapshot(rule);

        var before = EngineTestHarness.BuildRustEngine(time, trackerRepo);
        time.SetUtcNow(T0);
        var first = await before.EvaluateRuleAsync(snapshot, LowGlucoseContext(T0), AlertEngineOptions.Default, CancellationToken.None);
        first.ConditionMet.Should().BeFalse("the window starts on the first true tick");
        trackerRepo.Timers.DrainOps().Should().ContainSingle(op => op.Op == "set" && op.Path == "sustained" && op.At == T0);

        var after = EngineTestHarness.BuildRustEngine(time, trackerRepo);
        time.SetUtcNow(T0.AddMinutes(5));
        var mid = await after.EvaluateRuleAsync(snapshot, LowGlucoseContext(T0.AddMinutes(5)), AlertEngineOptions.Default, CancellationToken.None);
        mid.ConditionMet.Should().BeFalse("only 5 of 10 sustained minutes have elapsed since the stored first-true");
        trackerRepo.Timers.DrainOps().Should().BeEmpty("an in-flight window must not be re-set or cleared");

        time.SetUtcNow(T0.AddMinutes(10));
        var done = await after.EvaluateRuleAsync(snapshot, LowGlucoseContext(T0.AddMinutes(10)), AlertEngineOptions.Default, CancellationToken.None);
        done.ConditionMet.Should().BeTrue("10 sustained minutes elapsed since the stored first-true");
        done.Transition.Type.Should().Be(ExcursionTransitionType.ExcursionOpened);
        (await trackerRepo.Timers.GetAllForRuleAsync(RuleId, CancellationToken.None))
            .Should().Contain("sustained", T0, "the first-true instant the first instance recorded must survive untouched");
    }

    [NativeFact]
    public async Task Aux_scope_timers_thread_through_the_evaluate_node_path()
    {
        // EvaluateNodeAsync (the snooze / sweep auto-resolve surface) must honour and
        // persist (rule_id, path) timers exactly like the rule-body path.
        var rule = BuildSustainedRule();
        var time = new ManualTimeProvider();
        var trackerRepo = new InMemoryTrackerRepository([rule]);
        var rust = EngineTestHarness.BuildRustEngine(time, trackerRepo);

        var node = JsonDocument.Parse(
            """{"type":"sustained","sustained":{"minutes":10,"child":{"type":"threshold","threshold":{"direction":"below","value":70}}}}""")
            .RootElement;

        time.SetUtcNow(T0);
        var first = await rust.EvaluateNodeAsync(RuleId, node, LowGlucoseContext(T0), "snooze", CancellationToken.None);
        first.Should().BeFalse("the window starts on the first true tick");
        (await trackerRepo.Timers.GetAllForRuleAsync(RuleId, CancellationToken.None))
            .Should().Contain("snooze", T0, "aux-scope timers key under the path root, not the rule body");

        time.SetUtcNow(T0.AddMinutes(10));
        var done = await rust.EvaluateNodeAsync(RuleId, node, LowGlucoseContext(T0.AddMinutes(10)), "snooze", CancellationToken.None);
        done.Should().BeTrue("10 sustained minutes elapsed since the recorded first-true");
    }
}
