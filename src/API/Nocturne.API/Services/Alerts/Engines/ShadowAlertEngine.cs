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

    /// <summary>Evaluates one condition node against explicit pre-state timers. Must not persist anything.</summary>
    Task<ShadowNodeOutcome> EvaluateNodeAsync(
        Guid ruleId,
        ConditionNode node,
        string pathRoot,
        SensorContext context,
        DateTime now,
        IReadOnlyDictionary<string, DateTime> timers,
        CancellationToken ct);

    /// <summary>Runs the sweep's auto-resolve against explicit pre-state. Must not persist anything.</summary>
    Task<ShadowAutoResolveOutcome> EvaluateAutoResolveAsync(
        AlertRuleSnapshot rule,
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

/// <summary>The comparable surface of one shadow node evaluation.</summary>
internal sealed record ShadowNodeOutcome(bool Value, IReadOnlyDictionary<string, DateTime> PostTimers);

/// <summary>
/// The comparable surface of one shadow auto-resolve. <c>PostTracker</c> is <see langword="null"/>
/// when the rule has no tracker state.
/// </summary>
internal sealed record ShadowAutoResolveOutcome(
    ExcursionTransitionType Transition,
    ExcursionCloseReason? CloseReason,
    IReadOnlyDictionary<string, DateTime> PostTimers,
    TrackerPostState? PostTracker);

/// <summary>
/// <see cref="IShadowRuleEvaluator"/> over the Rust FFI. Pure: state goes in as data and
/// the response is only compared, never persisted.
/// </summary>
internal sealed class RustShadowRuleEvaluator(AlertEngineErrors errors, ILogger<RustShadowRuleEvaluator> logger)
    : IShadowRuleEvaluator
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

    public Task<ShadowNodeOutcome> EvaluateNodeAsync(
        Guid ruleId,
        ConditionNode node,
        string pathRoot,
        SensorContext context,
        DateTime now,
        IReadOnlyDictionary<string, DateTime> timers,
        CancellationToken ct)
    {
        var response = RustAuxiliaryEvaluation.EvaluateNode(
            errors, AlertEngineErrors.ShadowEngine, ruleId, RustEnvelopeMapper.BuildNode(node), pathRoot,
            context, now, timers);
        return Task.FromResult(new ShadowNodeOutcome(response.Value!.Value, response.Timers!));
    }

    public Task<ShadowAutoResolveOutcome> EvaluateAutoResolveAsync(
        AlertRuleSnapshot rule,
        SensorContext context,
        DateTime now,
        IReadOnlyDictionary<string, DateTime> timers,
        AlertTrackerState? trackerState,
        CancellationToken ct)
    {
        var unchanged = new ShadowAutoResolveOutcome(
            ExcursionTransitionType.None, null, timers, TrackerPostState.Of(trackerState));
        if (!RustAuxiliaryEvaluation.AttemptsAutoResolve(rule, trackerState))
            return Task.FromResult(unchanged);

        var outcome = RustAuxiliaryEvaluation.AutoResolve(
            errors, AlertEngineErrors.ShadowEngine, rule, context, now, timers, trackerState, logger);
        var postTimers = (IReadOnlyDictionary<string, DateTime>?)outcome.Node?.Timers ?? timers;
        return Task.FromResult(outcome.Close is { } close
            ? new ShadowAutoResolveOutcome(close.Type, close.CloseReason, postTimers, close.Post)
            : unchanged with { PostTimers = postTimers });
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
/// All three operations are shadowed. Node divergences carry the path root as a field prefix
/// (<c>snooze.value</c>), and auto-resolve divergences <c>auto_resolve.</c>.
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
            preTracker = Detach(await trackerRepository.GetTrackerStateAsync(rule.Id, ct));
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
            LogShadowError(snapshotError, rule.Id, "snapshot");
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
            LogShadowError(ex, rule.Id, "evaluate");
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
    public async Task<bool> EvaluateNodeAsync(
        Guid ruleId, ConditionNode node, SensorContext context, string pathRoot, CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        IReadOnlyDictionary<string, DateTime>? preTimers = null;
        try
        {
            preTimers = await timerStore.GetAllForRuleAsync(ruleId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogShadowError(ex, ruleId, "snapshot");
        }

        bool managed;
        try
        {
            managed = await managedEngine.EvaluateNodeAsync(ruleId, node, context, pathRoot, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (preTimers is not null)
                await LogNodeManagedThrewAsync(ruleId, node, pathRoot, context, now, preTimers, ex, ct);
            throw;
        }

        if (preTimers is null)
            return managed;

        try
        {
            var shadow = await shadowEvaluator.EvaluateNodeAsync(ruleId, node, pathRoot, context, now, preTimers, ct);
            var compare = Comparison(ruleId);
            if (managed != shadow.Value)
                compare.Diverged($"{pathRoot}.value", managed, shadow.Value);
            compare.Timers($"{pathRoot}.timers", await timerStore.GetAllForRuleAsync(ruleId, ct), shadow.PostTimers);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogShadowError(ex, ruleId, pathRoot);
        }

        return managed;
    }

    /// <summary>
    /// The node counterpart of <see cref="LogManagedThrewAsync"/>: a managed throw is agreement
    /// when the Rust engine rejects the node too.
    /// </summary>
    private async Task LogNodeManagedThrewAsync(
        Guid ruleId,
        ConditionNode node,
        string pathRoot,
        SensorContext context,
        DateTime now,
        IReadOnlyDictionary<string, DateTime> preTimers,
        Exception managedError,
        CancellationToken ct)
    {
        string shadowOutcome;
        try
        {
            var shadow = await shadowEvaluator.EvaluateNodeAsync(ruleId, node, pathRoot, context, now, preTimers, ct);
            shadowOutcome = $"value={shadow.Value}";
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (RustAlertEngineException)
        {
            LogBothSkipped(ruleId, managedError);
            return;
        }
        catch (Exception ex)
        {
            shadowOutcome = $"threw {ex.GetType().Name}";
        }

        Comparison(ruleId).Diverged(
            $"{pathRoot}.managed_threw", $"threw {managedError.GetType().Name}", shadowOutcome);
    }

    /// <inheritdoc/>
    public async Task<ExcursionTransition> EvaluateAutoResolveAsync(
        AlertRuleSnapshot rule, SensorContext context, CancellationToken ct)
    {
        const string operation = "auto_resolve";
        var now = timeProvider.GetUtcNow().UtcDateTime;
        IReadOnlyDictionary<string, DateTime>? preTimers = null;
        AlertTrackerState? preTracker = null;
        try
        {
            preTimers = await timerStore.GetAllForRuleAsync(rule.Id, ct);
            preTracker = Detach(await trackerRepository.GetTrackerStateAsync(rule.Id, ct));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            preTimers = null;
            LogShadowError(ex, rule.Id, "snapshot");
        }

        var managed = await managedEngine.EvaluateAutoResolveAsync(rule, context, ct);
        if (preTimers is null)
            return managed;

        try
        {
            var shadow = await shadowEvaluator.EvaluateAutoResolveAsync(rule, context, now, preTimers, preTracker, ct);
            var compare = Comparison(rule.Id);
            compare.Transition(operation, managed.Type, managed.CloseReason, shadow.Transition, shadow.CloseReason);
            compare.Timers($"{operation}.timers", await timerStore.GetAllForRuleAsync(rule.Id, ct), shadow.PostTimers);
            compare.PostState(
                operation,
                TrackerPostState.Of(await trackerRepository.GetTrackerStateAsync(rule.Id, ct)),
                shadow.PostTracker,
                exactInstants: false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogShadowError(ex, rule.Id, operation);
        }

        return managed;
    }

    /// <summary>A copy, since the managed tracker's store can hand back the instance it later mutates.</summary>
    private static AlertTrackerState? Detach(AlertTrackerState? state) =>
        state is null ? null : new AlertTrackerState
        {
            AlertRuleId = state.AlertRuleId,
            State = state.State,
            ConfirmationCount = state.ConfirmationCount,
            ActiveExcursionId = state.ActiveExcursionId,
            UpdatedAt = state.UpdatedAt,
            HysteresisStartedAt = state.HysteresisStartedAt,
        };

    private ShadowComparison Comparison(Guid ruleId) => new(logger, shadowEvaluator.Name, ruleId);

    private void LogShadowError(Exception ex, Guid ruleId, string stage) =>
        logger.LogWarning(ex,
            "AlertEngineShadowError rule={RuleId} engine={Engine} stage={Stage}", ruleId, shadowEvaluator.Name, stage);

    private async Task CompareAsync(
        Guid ruleId,
        AlertEngineEvaluation managed,
        ShadowRuleOutcome shadow,
        CancellationToken ct)
    {
        var compare = Comparison(ruleId);
        if (managed.Skipped || shadow.Skipped)
        {
            if (managed.Skipped != shadow.Skipped)
                compare.Diverged("skipped", managed.Skipped, shadow.Skipped);
            return;
        }

        if (managed.ConditionMet != shadow.Root)
            compare.Diverged("condition_met", managed.ConditionMet, shadow.Root);

        if (managed.Transition.Type != shadow.Transition)
            compare.Diverged("transition",
                RustEnvelopeMapper.TransitionToWire(managed.Transition.Type),
                RustEnvelopeMapper.TransitionToWire(shadow.Transition));

        if (managed.Transition.CloseReason != shadow.CloseReason)
            compare.Diverged("close_reason",
                RustEnvelopeMapper.CloseReasonToWire(managed.Transition.CloseReason) ?? "(none)",
                RustEnvelopeMapper.CloseReasonToWire(shadow.CloseReason) ?? "(none)");

        if (managed.AutoResolved != shadow.AutoResolved)
            compare.Diverged("auto_resolved", managed.AutoResolved, shadow.AutoResolved);

        // The managed engine has persisted by now, so the stores hold its post-state.
        compare.Timers("timers", await timerStore.GetAllForRuleAsync(ruleId, ct), shadow.PostTimers);

        var managedPostState = await trackerRepository.GetTrackerStateAsync(ruleId, ct);
        var managedTrackerState = managedPostState?.State;
        if (!string.Equals(managedTrackerState, shadow.PostTrackerState, StringComparison.Ordinal))
            compare.Diverged("tracker_state", managedTrackerState ?? "(none)", shadow.PostTrackerState ?? "(none)");

        var managedConfirmation = managedPostState?.ConfirmationCount ?? 0;
        if (managedConfirmation != shadow.PostConfirmationCount)
            compare.Diverged("confirmation_count", managedConfirmation, shadow.PostConfirmationCount);

        var managedHasExcursion = managedPostState?.ActiveExcursionId is not null;
        if (managedHasExcursion != shadow.PostHasActiveExcursion)
            compare.Diverged("active_excursion", managedHasExcursion, shadow.PostHasActiveExcursion);
    }
}
