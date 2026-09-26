using System.Text.Json;
using Nocturne.Core.Alerts.Native;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;

namespace Nocturne.API.Services.Alerts.Engines;

/// <summary>
/// <see cref="IAlertEvaluationEngine"/> implementation backed by the Rust engine
/// (crates/nocturne-alerts-core) over the nocturne_alerts FFI. The engine is stateless
/// between calls: this adapter reads the rule's persisted state (sustained timers via
/// <see cref="IConditionTimerStore"/>, tracker state via <see cref="IAlertTrackerRepository"/>),
/// threads it through the envelope, and persists what the engine decided, the tracker through
/// <see cref="ExcursionTransitionWriter"/>. DB rows stay identical to managed mode, so either
/// engine can pick up where the other left off.
/// </summary>
/// <remarks>
/// Excursion identity: the FFI tracker uses opaque ordinals and this host owns the GUIDs, so
/// only "has an active excursion" crosses the boundary (<see cref="RustEnvelopeMapper.BuildTracker"/>).
/// </remarks>
internal sealed class RustBackedAlertEngine(
    IConditionTimerStore timerStore,
    IAlertTrackerRepository trackerRepository,
    AlertRuleEvaluationGate gate,
    AlertEngineErrors errors,
    ConditionVersionLog conditionLog,
    TimeProvider timeProvider,
    ILogger<RustBackedAlertEngine> logger)
    : IAlertEvaluationEngine
{
    /// <inheritdoc/>
    public async Task<AlertEngineEvaluation> EvaluateRuleAsync(
        AlertRuleSnapshot rule,
        SensorContext context,
        AlertEngineOptions options,
        CancellationToken ct)
    {
        // This adapter owns the tracker-state read-modify-write itself (the managed tracker's
        // own lease never covers it), so it takes the same per-rule lease.
        using var lease = await gate.AcquireAsync(rule.Id, ct);

        // The snapshot doesn't carry ConfirmationReadings/HysteresisMinutes; the managed
        // tracker loads the rule row per evaluation, so mirror that here.
        var ruleRow = await trackerRepository.GetRuleAsync(rule.Id, ct);
        if (ruleRow is null)
        {
            // Managed parity (ExcursionTracker.ProcessEvaluationAsync with a missing rule):
            // the root condition still evaluates (mutating sustained timers), the tracker
            // no-ops with a None transition, and auto-resolve never fires (no state).
            logger.LogWarning("Alert rule {AlertRuleId} not found; skipping evaluation", rule.Id);
            var nodeOnly = await EvaluateRuleNodeWithoutTrackerAsync(rule, context, ct);
            return new AlertEngineEvaluation
            {
                ConditionMet = nodeOnly,
                Transition = new ExcursionTransition(ExcursionTransitionType.None),
            };
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var timers = RustEnvelopeMapper.BuildTimers(await timerStore.GetAllForRuleAsync(rule.Id, ct));
        var trackerState = await trackerRepository.GetTrackerStateAsync(rule.Id, ct);

        var (response, result) = errors.Track("evaluate", AlertEngineErrors.RustEngine, () =>
        {
            var response = RustAlertEngine.Evaluate(
                RustEnvelopeMapper.BuildRule(ruleRow),
                RustEnvelopeMapper.BuildContext(context),
                now,
                timers,
                RustEnvelopeMapper.BuildTracker(trackerState),
                options.IncludeLeafValues);
            return (response, RustAlertEngine.GetRuleResult(response, options.IncludeLeafValues));
        });

        if (result.Skipped)
        {
            // The envelope rejects a body that does not parse before evaluating it, so this is
            // reached only if the two checks disagree; the orchestrator logs rejections.
            if (conditionLog.FirstFor(rule.Id, rule.ConditionType, rule.ConditionParams))
            {
                logger.LogWarning(
                    "Rust engine skipped alert rule {AlertRuleId}: its condition tree cannot be evaluated; the rule is skipped until it is edited",
                    rule.Id);
            }
            return new AlertEngineEvaluation { Skipped = true };
        }

        // Timer deltas in execution order reproduce exactly the rows the managed evaluators write.
        await ApplyTimerOpsAsync(rule.Id, result.TimerOps, ct);

        var decision = new TrackerDecision(
            RustEnvelopeMapper.TransitionFromWire(result.Transition!.Value),
            result.CloseReason is { } reason ? RustEnvelopeMapper.CloseReasonFromWire(reason) : null,
            RustEnvelopeMapper.PostStateFromWire(response.Tracker!));
        var (transition, autoResolveTransition) = await ExcursionTransitionWriter.ApplyAsync(
            trackerRepository, logger, rule.Id, trackerState, decision, now, ct, result.AutoResolved);

        return new AlertEngineEvaluation
        {
            ConditionMet = result.Root!.Value,
            Transition = transition,
            AutoResolveTransition = autoResolveTransition,
            LeafValues = options.IncludeLeafValues
                ? result.Leaves!.ToDictionary(l => l.LeafId, l => l.Value)
                : null,
        };
    }

    /// <inheritdoc/>
    public Task<bool> EvaluateNodeAsync(
        Guid ruleId,
        ConditionNode node,
        SensorContext context,
        string pathRoot,
        CancellationToken ct) =>
        EvaluateNodeAsync(ruleId, RustEnvelopeMapper.BuildNode(node), pathRoot, context, ct);

    /// <inheritdoc/>
    public async Task<ExcursionTransition> EvaluateAutoResolveAsync(
        AlertRuleSnapshot rule,
        SensorContext context,
        CancellationToken ct)
    {
        var none = new ExcursionTransition(ExcursionTransitionType.None);
        using var lease = await gate.AcquireAsync(rule.Id, ct);

        var state = await trackerRepository.GetTrackerStateAsync(rule.Id, ct);
        if (!RustAuxiliaryEvaluation.AttemptsAutoResolve(rule, state))
            return none;

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var timers = await timerStore.GetAllForRuleAsync(rule.Id, ct);
        var outcome = RustAuxiliaryEvaluation.AutoResolve(
            errors, AlertEngineErrors.RustEngine, rule, context, now, timers, state, logger);
        if (outcome.Node is { } node)
            await ApplyTimerOpsAsync(rule.Id, node.TimerOps, ct);
        if (outcome.Close is not { } close)
            return none;

        var (transition, _) = await ExcursionTransitionWriter.ApplyAsync(
            trackerRepository, logger, rule.Id, state, close, now, ct);
        return transition;
    }

    /// <summary>
    /// Root-eval-only fallback for a rule whose row vanished mid-evaluation: evaluates the
    /// condition tree (timers still mutate) without touching the tracker.
    /// </summary>
    private Task<bool> EvaluateRuleNodeWithoutTrackerAsync(
        AlertRuleSnapshot rule,
        SensorContext context,
        CancellationToken ct)
    {
        var wire = Evaluators.AlertConditionTypeNames.ToWireString(rule.ConditionType);
        var node = RustEnvelopeMapper.ParseNode(
            RustEnvelopeMapper.WrapPayload(wire, rule.ConditionParams));
        return EvaluateNodeAsync(rule.Id, node, wire, context, ct);
    }

    /// <summary>
    /// Evaluates one full condition node through <c>evaluate_node</c> against the rule's persisted
    /// timers, and persists the timer deltas the engine reports.
    /// </summary>
    private async Task<bool> EvaluateNodeAsync(
        Guid ruleId, JsonElement node, string pathRoot, SensorContext context, CancellationToken ct)
    {
        var response = RustAuxiliaryEvaluation.EvaluateNode(
            errors, AlertEngineErrors.RustEngine, ruleId, node, pathRoot, context,
            timeProvider.GetUtcNow().UtcDateTime, await timerStore.GetAllForRuleAsync(ruleId, ct));
        await ApplyTimerOpsAsync(ruleId, response.TimerOps, ct);
        return response.Value!.Value;
    }

    private async Task ApplyTimerOpsAsync(Guid ruleId, IReadOnlyList<RustTimerOp>? ops, CancellationToken ct)
    {
        foreach (var op in ops ?? [])
        {
            if (op.Op == RustTimerOpKind.Set)
                await timerStore.SetFirstTrueAsync(ruleId, op.Path, op.At!.Value, ct);
            else
                await timerStore.ClearAsync(ruleId, op.Path, ct);
        }
    }
}
