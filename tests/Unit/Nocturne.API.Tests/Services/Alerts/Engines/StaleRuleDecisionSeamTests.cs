using FluentAssertions;
using Nocturne.Alerts.ParityCorpus.Generator.Harness;
using Nocturne.API.Services.Alerts;
using Nocturne.API.Services.Alerts.Engines;
using Nocturne.API.Tests.Services.BackgroundServices;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts.Engines;

/// <summary>
/// An evaluation that loaded a rule before an edit writes nothing decided on the old rule, for
/// each engine: the edit lands after the evaluation read the rule and the tracker state, and
/// before it takes the rule's transition lock, as an edit on another replica can
/// (docs/alerts/engine-semantics.md §6.3).
/// </summary>
public class StaleRuleDecisionSeamTests
{
    private static readonly Guid RuleId = Guid.Parse("00000000-0000-0000-0000-0000000000f1");
    private static readonly DateTime T0 = new(2026, 1, 5, 12, 0, 0, DateTimeKind.Utc);

    public enum Engine { Managed, Rust }

    /// <summary>Both trees hold at 60 mg/dL, so an auto-resolve closes an active excursion and holds.</summary>
    private static AlertRule Rule() => new()
    {
        Id = RuleId,
        Name = "low",
        ConditionType = AlertConditionType.Threshold,
        ConditionParams = """{"direction":"below","value":70}""",
        ConfirmationReadings = 1,
        AutoResolveEnabled = true,
        AutoResolveParams = """{"type":"threshold","threshold":{"direction":"below","value":90}}""",
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

    private static void EditAutoResolve(AlertRule rule) =>
        rule.AutoResolveParams = """{"type":"threshold","threshold":{"direction":"below","value":50}}""";

    private sealed record Fixture(
        IAlertEvaluationEngine Engine, InMemoryTrackerRepository Store, Guid ExcursionId, IAsyncDisposable Scope);

    /// <summary>An engine over a rule with an active excursion, whose edit lands at the first transition lock.</summary>
    private static async Task<Fixture> BuildAsync(Engine kind, AlertRule rule, Action<AlertRule> edit)
    {
        var time = new ManualTimeProvider();
        time.SetUtcNow(T0);
        var timers = new RecordingTimerStore();
        var store = new InMemoryTrackerRepository([rule]);
        var excursion = await store.CreateExcursionAsync(RuleId, T0.AddMinutes(-5));
        await store.UpsertTrackerStateAsync(new AlertTrackerState
        {
            AlertRuleId = RuleId,
            State = "active",
            ActiveExcursionId = excursion.Id,
            UpdatedAt = T0.AddMinutes(-5),
        });

        var repository = new EditAtLockRepository(store, async () =>
        {
            edit(rule);
            if (await store.GetTrackerStateAsync(RuleId) is { AwaitingRearm: true } held)
                held.AwaitingRearm = false;
        });
        if (kind == Engine.Rust)
            return new Fixture(EngineTestHarness.BuildRustEngine(time, timers, repository), store, excursion.Id, NoScope.Instance);

        var (managed, provider) = EngineTestHarness.BuildManagedEngine(time, timers, repository);
        return new Fixture(managed, store, excursion.Id, provider);
    }

    [Fact] public Task A_pre_edit_evaluation_sets_no_hold_managed() =>
        A_pre_edit_evaluation_sets_no_hold(Engine.Managed);
    [NativeFact] public Task A_pre_edit_evaluation_sets_no_hold_rust() =>
        A_pre_edit_evaluation_sets_no_hold(Engine.Rust);

    private static async Task A_pre_edit_evaluation_sets_no_hold(Engine kind)
    {
        var rule = Rule();
        var preEdit = Snapshot(rule);
        var fixture = await BuildAsync(kind, rule, EditAutoResolve);
        await using var _ = fixture.Scope;

        var evaluation = await fixture.Engine.EvaluateRuleAsync(
            preEdit, Glucose(60), AlertEngineOptions.Default, CancellationToken.None);

        evaluation.AutoResolved.Should().BeFalse();
        await ShouldStayActiveAsync(fixture);
    }

    [Fact] public Task A_pre_edit_sweep_auto_resolve_sets_no_hold_managed() =>
        A_pre_edit_sweep_auto_resolve_sets_no_hold(Engine.Managed);
    [NativeFact] public Task A_pre_edit_sweep_auto_resolve_sets_no_hold_rust() =>
        A_pre_edit_sweep_auto_resolve_sets_no_hold(Engine.Rust);

    private static async Task A_pre_edit_sweep_auto_resolve_sets_no_hold(Engine kind)
    {
        var rule = Rule();
        var preEdit = Snapshot(rule);
        var fixture = await BuildAsync(kind, rule, EditAutoResolve);
        await using var _ = fixture.Scope;

        var resolved = await fixture.Engine.EvaluateAutoResolveAsync(preEdit, Glucose(60), CancellationToken.None);

        resolved.Type.Should().Be(ExcursionTransitionType.None);
        await ShouldStayActiveAsync(fixture);
    }

    [Fact] public Task A_close_before_a_disable_is_dropped_managed() =>
        A_close_before_a_disable_is_dropped(Engine.Managed);
    [NativeFact] public Task A_close_before_a_disable_is_dropped_rust() =>
        A_close_before_a_disable_is_dropped(Engine.Rust);

    private static async Task A_close_before_a_disable_is_dropped(Engine kind)
    {
        var rule = Rule();
        var preEdit = Snapshot(rule);
        var fixture = await BuildAsync(kind, rule, r => r.IsEnabled = false);
        await using var _ = fixture.Scope;

        var resolved = await fixture.Engine.EvaluateAutoResolveAsync(preEdit, Glucose(60), CancellationToken.None);

        resolved.Type.Should().Be(ExcursionTransitionType.None);
        await ShouldStayActiveAsync(fixture);
    }

    [Fact] public Task An_evaluation_of_the_current_rule_sets_the_hold_managed() =>
        An_evaluation_of_the_current_rule_sets_the_hold(Engine.Managed);
    [NativeFact] public Task An_evaluation_of_the_current_rule_sets_the_hold_rust() =>
        An_evaluation_of_the_current_rule_sets_the_hold(Engine.Rust);

    private static async Task An_evaluation_of_the_current_rule_sets_the_hold(Engine kind)
    {
        var rule = Rule();
        var fixture = await BuildAsync(kind, rule, _ => { });
        await using var _ = fixture.Scope;

        var evaluation = await fixture.Engine.EvaluateRuleAsync(
            Snapshot(rule), Glucose(60), AlertEngineOptions.Default, CancellationToken.None);

        evaluation.AutoResolveTransition.Should().Be(new ExcursionTransition(
            ExcursionTransitionType.ExcursionClosed, fixture.ExcursionId, ExcursionCloseReason.AutoResolve));
        var state = await fixture.Store.GetTrackerStateAsync(RuleId);
        state.Should().BeEquivalentTo(new { State = "idle", ActiveExcursionId = (Guid?)null, AwaitingRearm = true });
    }

    /// <summary>
    /// The edit lands after the snapshot was loaded and before the Rust-backed engine reads the rule
    /// row under the lease. The engine decides on the edited row, so its decision stands.
    /// </summary>
    [NativeFact]
    public async Task A_decision_on_a_row_edited_after_the_snapshot_is_written_rust()
    {
        var rule = Rule();
        rule.AutoResolveParams = """{"type":"threshold","threshold":{"direction":"below","value":50}}""";
        var preEdit = Snapshot(rule);
        var fixture = await BuildAsync(Engine.Rust, rule, _ => { });
        await using var _ = fixture.Scope;
        rule.AutoResolveParams = Rule().AutoResolveParams;

        var evaluation = await fixture.Engine.EvaluateRuleAsync(
            preEdit, Glucose(60), AlertEngineOptions.Default, CancellationToken.None);

        evaluation.AutoResolveTransition.Should().Be(new ExcursionTransition(
            ExcursionTransitionType.ExcursionClosed, fixture.ExcursionId, ExcursionCloseReason.AutoResolve));
        var state = await fixture.Store.GetTrackerStateAsync(RuleId);
        state.Should().BeEquivalentTo(new { State = "idle", ActiveExcursionId = (Guid?)null, AwaitingRearm = true });
    }

    /// <summary>A disable that lands before the row read leaves nothing for the decision to land on.</summary>
    [NativeFact]
    public async Task A_decision_on_a_row_disabled_after_the_snapshot_is_dropped_rust()
    {
        var rule = Rule();
        var preEdit = Snapshot(rule);
        var fixture = await BuildAsync(Engine.Rust, rule, _ => { });
        await using var _ = fixture.Scope;
        rule.IsEnabled = false;

        var evaluation = await fixture.Engine.EvaluateRuleAsync(
            preEdit, Glucose(60), AlertEngineOptions.Default, CancellationToken.None);

        evaluation.AutoResolveTransition.Should().BeNull();
        await ShouldStayActiveAsync(fixture);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void A_row_holds_its_own_conditions_only_while_enabled(bool enabled, bool held)
    {
        var rule = Rule();
        rule.IsEnabled = enabled;

        AlertRuleConditions.Of(rule).HeldBy(rule).Should().Be(held);
    }

    [Fact] public Task A_tree_with_a_repeated_key_opens_managed() =>
        A_tree_with_a_repeated_key_opens(Engine.Managed);
    [NativeFact] public Task A_tree_with_a_repeated_key_opens_rust() =>
        A_tree_with_a_repeated_key_opens(Engine.Rust);

    /// <summary>
    /// A stored tree that repeats a key binds its last occurrence, and still equals itself, so its
    /// decisions are written.
    /// </summary>
    private static async Task A_tree_with_a_repeated_key_opens(Engine kind)
    {
        var rule = Rule();
        rule.ConditionParams = """{"direction":"below","value":50,"value":70}""";
        rule.AutoResolveEnabled = false;
        var time = new ManualTimeProvider();
        time.SetUtcNow(T0);
        var timers = new RecordingTimerStore();
        var store = new InMemoryTrackerRepository([rule]);
        IAlertEvaluationEngine engine;
        IAsyncDisposable scope = NoScope.Instance;
        if (kind == Engine.Rust)
        {
            engine = EngineTestHarness.BuildRustEngine(time, timers, store);
        }
        else
        {
            var (managed, provider) = EngineTestHarness.BuildManagedEngine(time, timers, store);
            (engine, scope) = (managed, provider);
        }
        await using var _ = scope;

        var evaluation = await engine.EvaluateRuleAsync(
            Snapshot(rule), Glucose(60), AlertEngineOptions.Default, CancellationToken.None);

        evaluation.Transition.Type.Should().Be(ExcursionTransitionType.ExcursionOpened);
        var state = await store.GetTrackerStateAsync(RuleId);
        state.Should().BeEquivalentTo(new { State = "active", ActiveExcursionId = evaluation.Transition.ExcursionId });
    }

    private static async Task ShouldStayActiveAsync(Fixture fixture)
    {
        var state = await fixture.Store.GetTrackerStateAsync(RuleId);
        state.Should().BeEquivalentTo(new { State = "active", ActiveExcursionId = (Guid?)fixture.ExcursionId, AwaitingRearm = false });
        (await fixture.Store.GetExcursionAsync(fixture.ExcursionId))!.EndedAt.Should().BeNull();
    }

    /// <summary>
    /// Runs <paramref name="edit"/> once, as the first transition lock is taken and before the
    /// writer reads anything under it.
    /// </summary>
    private sealed class EditAtLockRepository(InMemoryTrackerRepository inner, Func<Task> edit) : IAlertTrackerRepository
    {
        private Func<Task>? _edit = edit;

        public async Task LockRuleAsync(Guid alertRuleId, CancellationToken ct = default)
        {
            if (Interlocked.Exchange(ref _edit, null) is { } pending)
                await pending();
            await ((IAlertTrackerRepository)inner).LockRuleAsync(alertRuleId, ct);
        }

        public Task<AlertTrackerState?> GetTrackerStateAsync(Guid alertRuleId, CancellationToken ct = default) =>
            inner.GetTrackerStateAsync(alertRuleId, ct);

        public Task UpsertTrackerStateAsync(AlertTrackerState state, CancellationToken ct = default) =>
            inner.UpsertTrackerStateAsync(state, ct);

        public Task<AlertRule?> GetRuleAsync(Guid alertRuleId, CancellationToken ct = default) =>
            inner.GetRuleAsync(alertRuleId, ct);

        public Task<AlertExcursion> CreateExcursionAsync(Guid alertRuleId, DateTime startedAt, CancellationToken ct = default) =>
            inner.CreateExcursionAsync(alertRuleId, startedAt, ct);

        public Task<AlertExcursion?> GetExcursionAsync(Guid excursionId, CancellationToken ct = default) =>
            inner.GetExcursionAsync(excursionId, ct);

        public Task CloseExcursionAsync(Guid excursionId, DateTime endedAt, CancellationToken ct = default) =>
            inner.CloseExcursionAsync(excursionId, endedAt, ct);

        public Task SetHysteresisStartedAsync(Guid excursionId, DateTime hysteresisStartedAt, CancellationToken ct = default) =>
            inner.SetHysteresisStartedAsync(excursionId, hysteresisStartedAt, ct);

        public Task ClearHysteresisAsync(Guid excursionId, CancellationToken ct = default) =>
            inner.ClearHysteresisAsync(excursionId, ct);
    }

    private sealed class NoScope : IAsyncDisposable
    {
        public static readonly NoScope Instance = new();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
