using Nocturne.Core.Alerts.Native;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;

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
    public ExcursionTransitionType Transition { get; init; }
    public ExcursionCloseReason? CloseReason { get; init; }
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
internal sealed class RustShadowRuleEvaluator(AlertEngineErrors errors) : IShadowRuleEvaluator
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
        var (response, result) = errors.Track("evaluate", AlertEngineErrors.ShadowEngine, () =>
        {
            var response = RustAlertEngine.Evaluate(
                RustEnvelopeMapper.BuildRule(rule),
                RustEnvelopeMapper.BuildContext(context),
                now,
                RustEnvelopeMapper.BuildTimers(timers),
                RustEnvelopeMapper.BuildTracker(trackerState),
                includeLeaves: false);
            return (response, RustAlertEngine.GetRuleResult(response, leavesRequested: false));
        });

        return Task.FromResult(new ShadowRuleOutcome
        {
            Skipped = result.Skipped,
            Root = result.Root ?? false,
            Transition = result.Transition is { } transition
                ? RustEnvelopeMapper.TransitionFromWire(transition)
                : ExcursionTransitionType.None,
            CloseReason = result.CloseReason is { } reason ? RustEnvelopeMapper.CloseReasonFromWire(reason) : null,
            AutoResolved = result.AutoResolved,
            PostTimers = response.Timers!,
            PostTrackerState = response.Tracker!.State,
            PostConfirmationCount = response.Tracker.ConfirmationCount,
            PostHasActiveExcursion = response.Tracker.ActiveExcursionOrdinal is not null,
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
                HysteresisStartedAt = state.HysteresisStartedAt,
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            snapshotError = ex;
        }

        // A managed throw reaches the orchestrator unchanged, since its per-rule catch is what
        // skips the rule. The secondary engine still runs on the same pre-state: a managed throw
        // is a skip, and only the secondary outcome says whether Rust skipped too.
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
    /// the same rule and pre-state, unless it skipped the rule too. Runs the secondary evaluation
    /// itself because the normal comparison path is unreachable once the managed call has thrown;
    /// the evaluation is pure, so running it here persists nothing. Reports the Rust side as
    /// <c>(unavailable)</c> when the pre-state snapshot failed or the rule row was missing, and as
    /// its own error when the secondary engine fails otherwise, never as a value it did not produce.
    /// </summary>
    /// <remarks>
    /// Both engines skip a rule they cannot evaluate (docs/alerts/engine-semantics.md §1.4): the
    /// managed engine by throwing, the Rust engine by rejecting it
    /// (<see cref="RustAlertEngineException"/>) or reporting it skipped. That is agreement, and a
    /// stored rule of that shape would otherwise log a divergence on every tick.
    /// </remarks>
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
                if (shadow.Skipped)
                {
                    LogBothSkipped(rule.Id, managedError);
                    return;
                }
                shadowOutcome =
                    $"root={shadow.Root} transition={RustEnvelopeMapper.TransitionToWire(shadow.Transition)} auto_resolved={shadow.AutoResolved}";
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (RustAlertEngineException)
            {
                LogBothSkipped(rule.Id, managedError);
                return;
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

    private void LogBothSkipped(Guid ruleId, Exception managedError) =>
        logger.LogDebug(
            "Both alert engines skipped rule {RuleId} (managed threw {ManagedError}); engine={Engine}",
            ruleId, managedError.GetType().Name, shadowEvaluator.Name);

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

        if (managed.Transition.Type != shadow.Transition)
            LogDivergence(ruleId, "transition",
                RustEnvelopeMapper.TransitionToWire(managed.Transition.Type),
                RustEnvelopeMapper.TransitionToWire(shadow.Transition));

        if (managed.Transition.CloseReason != shadow.CloseReason)
            LogDivergence(ruleId, "close_reason",
                RustEnvelopeMapper.CloseReasonToWire(managed.Transition.CloseReason) ?? "(none)",
                RustEnvelopeMapper.CloseReasonToWire(shadow.CloseReason) ?? "(none)");

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
