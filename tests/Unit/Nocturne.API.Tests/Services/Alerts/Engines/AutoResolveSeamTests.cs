using FluentAssertions;
using Nocturne.Alerts.ParityCorpus.Generator.Harness;
using Nocturne.API.Services.Alerts.Engines;
using Nocturne.API.Tests.Services.BackgroundServices;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts.Engines;

/// <summary>
/// <see cref="IAlertEvaluationEngine.EvaluateAutoResolveAsync"/>, the sweep's auto-resolve, for
/// each engine: it closes the open excursion with reason auto only when the tree holds.
/// </summary>
public class AutoResolveSeamTests
{
    private static readonly Guid RuleId = Guid.Parse("00000000-0000-0000-0000-0000000000e1");
    private static readonly DateTime T0 = new(2026, 1, 5, 12, 0, 0, DateTimeKind.Utc);

    private static AlertRule Rule(bool autoResolveEnabled = true) => new()
    {
        Id = RuleId,
        Name = "low",
        ConditionType = AlertConditionType.Threshold,
        ConditionParams = """{"direction":"below","value":70}""",
        ConfirmationReadings = 1,
        AutoResolveEnabled = autoResolveEnabled,
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

    public enum Engine { Managed, Rust }

    private static (IAlertEvaluationEngine Engine, InMemoryTrackerRepository Repository, IAsyncDisposable Scope)
        Build(Engine engine, AlertRule rule)
    {
        var time = new ManualTimeProvider();
        time.SetUtcNow(T0);
        var timers = new RecordingTimerStore();
        var repository = new InMemoryTrackerRepository([rule]);
        if (engine == Engine.Rust)
            return (EngineTestHarness.BuildRustEngine(time, timers, repository), repository, NoScope.Instance);

        var (managed, provider) = EngineTestHarness.BuildManagedEngine(time, timers, repository);
        return (managed, repository, provider);
    }

    private sealed class NoScope : IAsyncDisposable
    {
        public static readonly NoScope Instance = new();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact] public Task Closes_the_open_excursion_when_the_tree_holds_managed() =>
        Closes_the_open_excursion_when_the_tree_holds(Engine.Managed);
    [NativeFact] public Task Closes_the_open_excursion_when_the_tree_holds_rust() =>
        Closes_the_open_excursion_when_the_tree_holds(Engine.Rust);

    private static async Task Closes_the_open_excursion_when_the_tree_holds(Engine kind)
    {
        var rule = Rule();
        var (engine, repository, scope) = Build(kind, rule);
        await using var _ = scope;
        var opened = await engine.EvaluateRuleAsync(
            Snapshot(rule), Glucose(60), AlertEngineOptions.Default, CancellationToken.None);

        var resolved = await engine.EvaluateAutoResolveAsync(Snapshot(rule), Glucose(100), CancellationToken.None);

        resolved.Should().Be(new ExcursionTransition(
            ExcursionTransitionType.ExcursionClosed, opened.Transition.ExcursionId, ExcursionCloseReason.AutoResolve));
        var state = await repository.GetTrackerStateAsync(RuleId);
        state.Should().BeEquivalentTo(new { State = "idle", ActiveExcursionId = (Guid?)null });
    }

    [Fact] public Task Leaves_the_excursion_open_when_the_tree_does_not_hold_managed() =>
        Leaves_the_excursion_open_when_the_tree_does_not_hold(Engine.Managed);
    [NativeFact] public Task Leaves_the_excursion_open_when_the_tree_does_not_hold_rust() =>
        Leaves_the_excursion_open_when_the_tree_does_not_hold(Engine.Rust);

    private static async Task Leaves_the_excursion_open_when_the_tree_does_not_hold(Engine kind)
    {
        var rule = Rule();
        var (engine, repository, scope) = Build(kind, rule);
        await using var _ = scope;
        var opened = await engine.EvaluateRuleAsync(
            Snapshot(rule), Glucose(60), AlertEngineOptions.Default, CancellationToken.None);

        var resolved = await engine.EvaluateAutoResolveAsync(Snapshot(rule), Glucose(65), CancellationToken.None);

        resolved.Type.Should().Be(ExcursionTransitionType.None);
        (await repository.GetTrackerStateAsync(RuleId))!.ActiveExcursionId.Should().Be(opened.Transition.ExcursionId);
    }

    [Fact] public Task Does_nothing_when_auto_resolve_is_disabled_managed() =>
        Does_nothing_when_auto_resolve_is_disabled(Engine.Managed);
    [NativeFact] public Task Does_nothing_when_auto_resolve_is_disabled_rust() =>
        Does_nothing_when_auto_resolve_is_disabled(Engine.Rust);

    private static async Task Does_nothing_when_auto_resolve_is_disabled(Engine kind)
    {
        var rule = Rule(autoResolveEnabled: false);
        var (engine, repository, scope) = Build(kind, rule);
        await using var _ = scope;
        await engine.EvaluateRuleAsync(Snapshot(rule), Glucose(60), AlertEngineOptions.Default, CancellationToken.None);

        var resolved = await engine.EvaluateAutoResolveAsync(Snapshot(rule), Glucose(100), CancellationToken.None);

        resolved.Type.Should().Be(ExcursionTransitionType.None);
        (await repository.GetTrackerStateAsync(RuleId))!.State.Should().Be("active");
    }

    [Fact] public Task Does_nothing_without_an_open_excursion_managed() =>
        Does_nothing_without_an_open_excursion(Engine.Managed);
    [NativeFact] public Task Does_nothing_without_an_open_excursion_rust() =>
        Does_nothing_without_an_open_excursion(Engine.Rust);

    private static async Task Does_nothing_without_an_open_excursion(Engine kind)
    {
        var rule = Rule();
        var (engine, repository, scope) = Build(kind, rule);
        await using var _ = scope;

        var resolved = await engine.EvaluateAutoResolveAsync(Snapshot(rule), Glucose(100), CancellationToken.None);

        resolved.Type.Should().Be(ExcursionTransitionType.None);
        (await repository.GetTrackerStateAsync(RuleId)).Should().BeNull();
    }
}
