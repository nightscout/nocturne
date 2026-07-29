using Nocturne.Core.Alerts.Native;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Models;

namespace Nocturne.API.Services.Alerts.Engines;

/// <summary>
/// Side-effect-free secondary evaluation used by <see cref="ShadowAlertEngine"/>: given
/// the rule row and an explicit pre-state snapshot (timers + tracker), produce the
/// observable outcome without persisting anything.
/// </summary>
internal interface IShadowRuleEvaluator
{
    /// <summary>Engine name used in divergence log events (e.g. <c>rust</c>).</summary>
    string Name { get; }

    /// <summary>Evaluates one rule for one tick against explicit pre-state. Must not persist anything.</summary>
    Task<ShadowRuleOutcome> EvaluateAsync(
        AlertRule rule,
        SensorContext context,
        DateTime now,
        IReadOnlyDictionary<string, DateTime> timers,
        AlertTrackerState? trackerState,
        CancellationToken ct);
}

/// <summary>
/// The comparable surface of one shadow evaluation: condition truth, transition,
/// auto-resolve and post-state (timers + tracker), all engine-independent.
/// </summary>
internal sealed record ShadowRuleOutcome
{
    public bool Skipped { get; init; }
    public bool Root { get; init; }
    /// <summary>Transition wire form (<c>none | opened | continues | hysteresis_started | hysteresis_resumed | closed</c>).</summary>
    public string Transition { get; init; } = "none";
    public string? CloseReason { get; init; }
    public bool AutoResolved { get; init; }
    /// <summary>Post-evaluation timers (<c>path → first-true</c>).</summary>
    public IReadOnlyDictionary<string, DateTime> PostTimers { get; init; } = new Dictionary<string, DateTime>();
    /// <summary>Post-evaluation tracker state wire form, or null when no state exists.</summary>
    public string? PostTrackerState { get; init; }
    public int PostConfirmationCount { get; init; }
    public bool PostHasActiveExcursion { get; init; }
}

/// <summary>
/// <see cref="IShadowRuleEvaluator"/> over the Rust FFI. Pure: state goes in as data and
/// the response is only compared, never persisted.
/// </summary>
internal sealed class RustShadowRuleEvaluator : IShadowRuleEvaluator
{
    public string Name => "rust";

    public Task<ShadowRuleOutcome> EvaluateAsync(
        AlertRule rule,
        SensorContext context,
        DateTime now,
        IReadOnlyDictionary<string, DateTime> timers,
        AlertTrackerState? trackerState,
        CancellationToken ct)
    {
        var response = RustAlertEngine.Evaluate(
            RustEnvelopeMapper.BuildRule(rule),
            RustEnvelopeMapper.BuildContext(context),
            now,
            RustEnvelopeMapper.BuildTimers(timers),
            RustEnvelopeMapper.BuildTracker(trackerState));
        var result = RustAlertEngine.GetRuleResult(response);

        return Task.FromResult(new ShadowRuleOutcome
        {
            Skipped = result.Skipped == true,
            Root = result.Root ?? false,
            Transition = result.Transition ?? "none",
            CloseReason = result.CloseReason,
            AutoResolved = result.AutoResolved == true,
            PostTimers = response.Timers ?? new Dictionary<string, DateTime>(),
            PostTrackerState = response.Tracker?.State,
            PostConfirmationCount = response.Tracker?.ConfirmationCount ?? 0,
            PostHasActiveExcursion = response.Tracker?.ActiveExcursionOrdinal is not null,
        });
    }
}

/// <summary>
/// Shadow-mode <see cref="IAlertEvaluationEngine"/>: the managed engine is authoritative
/// (it evaluates, persists and its result is returned to the caller), while the secondary
/// engine re-evaluates the same rule from the same pre-state snapshot entirely in memory.
/// Divergences are logged as structured <c>AlertEngineDivergence</c> warnings; secondary
/// failures are logged as <c>AlertEngineShadowError</c> and never escape to the caller.
/// </summary>
/// <remarks>
/// Only <see cref="EvaluateRuleAsync"/> (the per-reading hot path) is shadowed; the
/// auxiliary scopes (<see cref="EvaluateNodeAsync"/>, <see cref="EvaluateAutoResolveAsync"/>)
/// pass straight through to the managed engine — they are covered by the same evaluator
/// core that the per-rule shadow already exercises.
/// </remarks>
internal sealed class ShadowAlertEngine(
    ManagedAlertEngine managedEngine,
    IShadowRuleEvaluator shadowEvaluator,
    IConditionTimerStore timerStore,
    IAlertTrackerRepository trackerRepository,
    TimeProvider timeProvider,
    ILogger<ShadowAlertEngine> logger)
    : IAlertEvaluationEngine
{
    /// <inheritdoc/>
    public async Task<AlertEngineEvaluation> EvaluateRuleAsync(
        AlertRuleSnapshot rule,
        SensorContext context,
        AlertEngineOptions options,
        CancellationToken ct)
    {
        // Snapshot pre-state BEFORE the managed evaluation mutates it, so the shadow run
        // sees exactly the state the managed run started from. The snapshot itself is
        // read-only; failures here must not break the authoritative path.
        AlertRule? ruleRow = null;
        IReadOnlyDictionary<string, DateTime>? preTimers = null;
        AlertTrackerState? preTracker = null;
        DateTime now = timeProvider.GetUtcNow().UtcDateTime;
        Exception? snapshotError = null;
        try
        {
            ruleRow = await trackerRepository.GetRuleAsync(rule.Id, ct);
            preTimers = await timerStore.GetAllForRuleAsync(rule.Id, ct);
            var state = await trackerRepository.GetTrackerStateAsync(rule.Id, ct);
            // Detach: the managed tracker mutates the same entity instance it loaded.
            preTracker = state is null ? null : new AlertTrackerState
            {
                AlertRuleId = state.AlertRuleId,
                State = state.State,
                ConfirmationCount = state.ConfirmationCount,
                ActiveExcursionId = state.ActiveExcursionId,
                UpdatedAt = state.UpdatedAt,
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            snapshotError = ex;
        }

        // The managed engine stays authoritative, including when it throws — the orchestrator's
        // per-rule catch is what decides a throwing rule is skipped, so the exception has to
        // reach it unchanged. But a throw is precisely the divergence class shadow mode is
        // otherwise blind to: the managed evaluators NRE on a missing payload field and the rule
        // is skipped, whereas the Rust engine fails closed to `false` — which moves an active
        // excursion into hysteresis and lets the 30s sweep force-close a live alert. The corpus
        // can't cover it either (the generator has no try/catch, so a throwing scenario cannot
        // be authored). Run the secondary engine on the same pre-state so the log carries what it
        // actually produced — the whole point is to learn which side diverges, and "managed threw"
        // on its own says nothing about the Rust half — then rethrow untouched.
        AlertEngineEvaluation managed;
        try
        {
            managed = await managedEngine.EvaluateRuleAsync(rule, context, options, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await LogManagedThrewAsync(rule, context, ruleRow, preTimers, preTracker, now, ex, ct);
            throw;
        }

        if (snapshotError is not null)
        {
            logger.LogWarning(snapshotError,
                "AlertEngineShadowError rule={RuleId} engine={Engine} stage=snapshot", rule.Id, shadowEvaluator.Name);
            return managed;
        }

        if (ruleRow is null)
        {
            // Managed tracker no-ops on a missing rule row; nothing meaningful to shadow.
            return managed;
        }

        try
        {
            var shadow = await shadowEvaluator.EvaluateAsync(ruleRow, context, now, preTimers!, preTracker, ct);
            await CompareAsync(rule.Id, managed, shadow, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex,
                "AlertEngineShadowError rule={RuleId} engine={Engine} stage=evaluate", rule.Id, shadowEvaluator.Name);
        }

        return managed;
    }

    /// <summary>
    /// Logs the <c>managed_threw</c> divergence, including what the secondary engine produced for
    /// the same rule and pre-state. Runs the secondary evaluation itself because the normal
    /// comparison path is unreachable once the managed call has thrown; the evaluation is pure, so
    /// running it here persists nothing. Reports the Rust side as <c>(unavailable)</c> when the
    /// pre-state snapshot failed or the rule row was missing, and as its own error when the
    /// secondary engine also fails — never as a value it did not produce.
    /// </summary>
    private async Task LogManagedThrewAsync(
        AlertRuleSnapshot rule,
        SensorContext context,
        AlertRule? ruleRow,
        IReadOnlyDictionary<string, DateTime>? preTimers,
        AlertTrackerState? preTracker,
        DateTime now,
        Exception managedError,
        CancellationToken ct)
    {
        string shadowOutcome;
        if (ruleRow is null || preTimers is null)
        {
            shadowOutcome = "(unavailable: no pre-state snapshot)";
        }
        else
        {
            try
            {
                var shadow = await shadowEvaluator.EvaluateAsync(
                    ruleRow, context, now, preTimers, preTracker, ct);
                shadowOutcome = shadow.Skipped
                    ? "skipped"
                    : $"root={shadow.Root} transition={shadow.Transition} auto_resolved={shadow.AutoResolved}";
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                shadowOutcome = $"threw {ex.GetType().Name}";
            }
        }

        logger.LogWarning(managedError,
            "AlertEngineDivergence rule={RuleId} engine={Engine} field=managed_threw managed={Managed} rust={Rust}",
            rule.Id, shadowEvaluator.Name, $"threw {managedError.GetType().Name}", shadowOutcome);
    }

    /// <inheritdoc/>
    public Task<bool> EvaluateNodeAsync(
        Guid ruleId, ConditionNode node, SensorContext context, string pathRoot, CancellationToken ct) =>
        managedEngine.EvaluateNodeAsync(ruleId, node, context, pathRoot, ct);

    /// <inheritdoc/>
    public Task<ExcursionTransition> EvaluateAutoResolveAsync(
        AlertRuleSnapshot rule, SensorContext context, CancellationToken ct) =>
        managedEngine.EvaluateAutoResolveAsync(rule, context, ct);

    private async Task CompareAsync(
        Guid ruleId,
        AlertEngineEvaluation managed,
        ShadowRuleOutcome shadow,
        CancellationToken ct)
    {
        if (managed.Skipped || shadow.Skipped)
        {
            if (managed.Skipped != shadow.Skipped)
                LogDivergence(ruleId, "skipped", managed.Skipped, shadow.Skipped);
            return;
        }

        if (managed.ConditionMet != shadow.Root)
            LogDivergence(ruleId, "condition_met", managed.ConditionMet, shadow.Root);

        var managedTransition = RustEnvelopeMapper.TransitionToWire(managed.Transition.Type);
        if (!string.Equals(managedTransition, shadow.Transition, StringComparison.Ordinal))
            LogDivergence(ruleId, "transition", managedTransition, shadow.Transition);

        var managedCloseReason = RustEnvelopeMapper.CloseReasonToWire(managed.Transition.CloseReason);
        if (!string.Equals(managedCloseReason, shadow.CloseReason, StringComparison.Ordinal))
            LogDivergence(ruleId, "close_reason", managedCloseReason ?? "(none)", shadow.CloseReason ?? "(none)");

        if (managed.AutoResolved != shadow.AutoResolved)
            LogDivergence(ruleId, "auto_resolved", managed.AutoResolved, shadow.AutoResolved);

        // Post-state comparisons: the managed engine has persisted by now, so the stores
        // hold its post-state; the shadow outcome carries the engine-computed post-state.
        var managedPostTimers = await timerStore.GetAllForRuleAsync(ruleId, ct);
        if (!TimersEqual(managedPostTimers, shadow.PostTimers))
            LogDivergence(ruleId, "timers", FormatTimers(managedPostTimers), FormatTimers(shadow.PostTimers));

        var managedPostState = await trackerRepository.GetTrackerStateAsync(ruleId, ct);
        var managedTrackerState = managedPostState?.State;
        if (!string.Equals(managedTrackerState, shadow.PostTrackerState, StringComparison.Ordinal))
            LogDivergence(ruleId, "tracker_state", managedTrackerState ?? "(none)", shadow.PostTrackerState ?? "(none)");

        var managedConfirmation = managedPostState?.ConfirmationCount ?? 0;
        if (managedConfirmation != shadow.PostConfirmationCount)
            LogDivergence(ruleId, "confirmation_count", managedConfirmation, shadow.PostConfirmationCount);

        var managedHasExcursion = managedPostState?.ActiveExcursionId is not null;
        if (managedHasExcursion != shadow.PostHasActiveExcursion)
            LogDivergence(ruleId, "active_excursion", managedHasExcursion, shadow.PostHasActiveExcursion);
    }

    private void LogDivergence(Guid ruleId, string field, object managed, object shadow)
    {
        logger.LogWarning(
            "AlertEngineDivergence rule={RuleId} engine={Engine} field={Field} managed={Managed} rust={Rust}",
            ruleId, shadowEvaluator.Name, field, managed, shadow);
    }

    /// <summary>
    /// The shadow run pins its own "now" just before the managed run starts, while the
    /// managed evaluators read the clock mid-evaluation — so freshly-set timer instants
    /// legitimately differ by however long the managed pass took. Anything inside this
    /// window is clock skew, not divergence.
    /// </summary>
    private static readonly TimeSpan TimerSkewTolerance = TimeSpan.FromSeconds(5);

    private static bool TimersEqual(
        IReadOnlyDictionary<string, DateTime> managed,
        IReadOnlyDictionary<string, DateTime> shadow)
    {
        if (managed.Count != shadow.Count) return false;
        foreach (var (path, at) in managed)
        {
            if (!shadow.TryGetValue(path, out var other)) return false;
            if ((other - at).Duration() > TimerSkewTolerance) return false;
        }
        return true;
    }

    private static string FormatTimers(IReadOnlyDictionary<string, DateTime> timers) =>
        timers.Count == 0
            ? "(empty)"
            : string.Join(";", timers.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => $"{kv.Key}={kv.Value:O}"));
}
