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
/// <see cref="IConditionTimerStore"/>, tracker state via
/// <see cref="IAlertTrackerRepository"/>), threads it through the envelope, and persists
/// the deltas the engine reports — so DB rows stay identical to managed mode and either
/// engine can pick up where the other left off.
/// </summary>
/// <remarks>
/// Excursion identity: the FFI tracker uses opaque ordinals; this host owns the GUIDs.
/// "Has an active excursion" goes in as a sentinel ordinal, and the response
/// <c>transition</c> (+ <c>auto_resolved</c>) drives <see cref="IAlertTrackerRepository"/>
/// excursion lifecycle calls (create/close/set-hysteresis/clear-hysteresis), with the
/// real GUID stored back into tracker state.
/// </remarks>
internal sealed class RustBackedAlertEngine(
    IConditionTimerStore timerStore,
    IAlertTrackerRepository trackerRepository,
    IExcursionTracker excursionTracker,
    AlertRuleEvaluationGate gate,
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
        var priorExcursionId = trackerState?.ActiveExcursionId;

        var response = RustAlertEngine.Evaluate(
            RustEnvelopeMapper.BuildRule(ruleRow),
            RustEnvelopeMapper.BuildContext(context),
            now,
            timers,
            RustEnvelopeMapper.BuildTracker(trackerState));
        var result = RustAlertEngine.GetRuleResult(response);

        if (result.Skipped == true)
        {
            // signal_loss as a root type: state passes through unchanged, nothing persisted.
            logger.LogWarning("No evaluator registered for condition type '{ConditionType}'", rule.ConditionType);
            return new AlertEngineEvaluation { Skipped = true };
        }

        // 1. Sustained-timer deltas, in execution order — reproduces exactly the rows the
        //    managed evaluators would have written.
        await ApplyTimerOpsAsync(rule.Id, result.TimerOps, ct);

        // 2. Excursion lifecycle. The transition is the event source; GUIDs are host-owned.
        var (transition, activeExcursionId) = await ApplyTransitionAsync(
            rule.Id, result, priorExcursionId, now, ct);

        ExcursionTransition? autoResolveTransition = null;
        if (result.AutoResolved == true)
        {
            // The engine force-closed the excursion in its auto-resolve pass (reason auto).
            // Close whichever excursion was active after the main transition (a same-call
            // open + auto-close closes the excursion we just created).
            if (activeExcursionId is { } toClose)
            {
                await trackerRepository.CloseExcursionAsync(toClose, now, ct);
                autoResolveTransition = new ExcursionTransition(
                    ExcursionTransitionType.ExcursionClosed, toClose, ExcursionCloseReason.AutoResolve);
                activeExcursionId = null;

                logger.LogInformation(
                    "Excursion {ExcursionId} force-closed for alert rule {AlertRuleId}, reason={Reason}",
                    toClose, rule.Id, ExcursionCloseReason.AutoResolve);
            }
        }

        // 3. Tracker state upsert from the engine's post-state (UpdatedAt is rewritten on
        //    every evaluation, matching the managed tracker).
        if (response.Tracker is { State: not null } post)
        {
            await trackerRepository.UpsertTrackerStateAsync(new AlertTrackerState
            {
                AlertRuleId = rule.Id,
                State = post.State,
                ConfirmationCount = post.ConfirmationCount,
                ActiveExcursionId = post.ActiveExcursionOrdinal is null ? null : activeExcursionId,
                UpdatedAt = post.UpdatedAt ?? now,
            }, ct);
        }

        return new AlertEngineEvaluation
        {
            ConditionMet = result.Root ?? false,
            Transition = transition,
            AutoResolveTransition = autoResolveTransition,
            LeafValues = options.IncludeLeafValues
                ? (result.Leaves ?? []).ToDictionary(l => l.LeafId, l => l.Value)
                : null,
        };
    }

    /// <inheritdoc/>
    public async Task<bool> EvaluateNodeAsync(
        Guid ruleId,
        ConditionNode node,
        SensorContext context,
        string pathRoot,
        CancellationToken ct)
    {
        var response = RustAlertEngine.EvaluateNode(new RustEvaluateNodeRequest
        {
            RuleId = ruleId,
            Node = RustEnvelopeMapper.BuildNode(node),
            Root = pathRoot,
            Context = RustEnvelopeMapper.BuildContext(context),
            Now = timeProvider.GetUtcNow().UtcDateTime,
            Timers = RustEnvelopeMapper.BuildTimers(await timerStore.GetAllForRuleAsync(ruleId, ct)),
        });

        await ApplyTimerOpsAsync(ruleId, response.TimerOps, ct);
        return response.Value;
    }

    /// <inheritdoc/>
    public async Task<ExcursionTransition> EvaluateAutoResolveAsync(
        AlertRuleSnapshot rule,
        SensorContext context,
        CancellationToken ct)
    {
        var none = new ExcursionTransition(ExcursionTransitionType.None);
        if (!rule.AutoResolveEnabled || string.IsNullOrWhiteSpace(rule.AutoResolveParams))
            return none;

        // Active-excursion gate + force-close stay on the host tracker port — force-close
        // is persistence bookkeeping, not evaluation (engine-semantics §6.2).
        var activeExcursionId = await excursionTracker.GetActiveExcursionIdAsync(rule.Id, ct);
        if (activeExcursionId is null)
            return none;

        JsonElement nodeJson;
        try
        {
            nodeJson = RustEnvelopeMapper.ParseJson(rule.AutoResolveParams);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Failed to parse AutoResolveParams for rule {AlertRuleId}; skipping", rule.Id);
            return none;
        }

        bool shouldResolve;
        try
        {
            var response = RustAlertEngine.EvaluateNode(new RustEvaluateNodeRequest
            {
                RuleId = rule.Id,
                Node = nodeJson,
                Root = Evaluators.AlertConditionTypeNames.AutoResolvePathRoot,
                Context = RustEnvelopeMapper.BuildContext(context),
                Now = timeProvider.GetUtcNow().UtcDateTime,
                Timers = RustEnvelopeMapper.BuildTimers(await timerStore.GetAllForRuleAsync(rule.Id, ct)),
            });
            await ApplyTimerOpsAsync(rule.Id, response.TimerOps, ct);
            shouldResolve = response.Value;
        }
        catch (RustAlertEngineException ex)
        {
            // Structurally malformed trees are skipped silently in the managed path
            // (JsonException on ConditionNode deserialisation); mirror that.
            logger.LogWarning(ex, "Failed to parse AutoResolveParams for rule {AlertRuleId}; skipping", rule.Id);
            return none;
        }

        if (!shouldResolve) return none;

        return await excursionTracker.ForceCloseAsync(rule.Id, ExcursionCloseReason.AutoResolve, ct);
    }

    /// <summary>
    /// Root-eval-only fallback for a rule whose row vanished mid-evaluation: evaluates the
    /// condition tree (timers still mutate) without touching the tracker.
    /// </summary>
    private async Task<bool> EvaluateRuleNodeWithoutTrackerAsync(
        AlertRuleSnapshot rule,
        SensorContext context,
        CancellationToken ct)
    {
        var wire = Evaluators.AlertConditionTypeNames.ToWireString(rule.ConditionType);
        var payload = string.IsNullOrWhiteSpace(rule.ConditionParams) ? "null" : rule.ConditionParams;
        var nodeJson = RustEnvelopeMapper.ParseJson($"{{\"type\":{JsonSerializer.Serialize(wire)},{JsonSerializer.Serialize(wire)}:{payload}}}");

        var response = RustAlertEngine.EvaluateNode(new RustEvaluateNodeRequest
        {
            RuleId = rule.Id,
            Node = nodeJson,
            Root = wire,
            Context = RustEnvelopeMapper.BuildContext(context),
            Now = timeProvider.GetUtcNow().UtcDateTime,
            Timers = RustEnvelopeMapper.BuildTimers(await timerStore.GetAllForRuleAsync(rule.Id, ct)),
        });
        await ApplyTimerOpsAsync(rule.Id, response.TimerOps, ct);
        return response.Value;
    }

    private async Task ApplyTimerOpsAsync(Guid ruleId, IReadOnlyList<RustTimerOp>? ops, CancellationToken ct)
    {
        if (ops is null) return;
        foreach (var op in ops)
        {
            switch (op.Op)
            {
                case "set" when op.At is { } at:
                    await timerStore.SetFirstTrueAsync(ruleId, op.Path, at, ct);
                    break;
                case "clear":
                    await timerStore.ClearAsync(ruleId, op.Path, ct);
                    break;
                default:
                    logger.LogWarning("Unknown timer op '{Op}' for rule {AlertRuleId} path {Path}", op.Op, ruleId, op.Path);
                    break;
            }
        }
    }

    /// <summary>
    /// Applies the engine's tracker transition to the excursion rows and returns the
    /// transition (with host GUIDs) plus the excursion active after the main transition.
    /// </summary>
    private async Task<(ExcursionTransition Transition, Guid? ActiveExcursionId)> ApplyTransitionAsync(
        Guid ruleId,
        RustRuleResult result,
        Guid? priorExcursionId,
        DateTime now,
        CancellationToken ct)
    {
        var type = RustEnvelopeMapper.TransitionFromWire(result.Transition);
        switch (type)
        {
            case ExcursionTransitionType.ExcursionOpened:
            {
                var excursion = await trackerRepository.CreateExcursionAsync(ruleId, now, ct);
                logger.LogInformation(
                    "Excursion {ExcursionId} opened for alert rule {AlertRuleId}", excursion.Id, ruleId);
                return (new ExcursionTransition(type, excursion.Id), excursion.Id);
            }

            case ExcursionTransitionType.HysteresisStarted:
                if (priorExcursionId is { } hsId)
                    await trackerRepository.SetHysteresisStartedAsync(hsId, now, ct);
                return (new ExcursionTransition(type, priorExcursionId), priorExcursionId);

            case ExcursionTransitionType.HysteresisResumed:
                if (priorExcursionId is { } hrId)
                    await trackerRepository.ClearHysteresisAsync(hrId, ct);
                return (new ExcursionTransition(type, priorExcursionId), priorExcursionId);

            case ExcursionTransitionType.ExcursionClosed:
                if (priorExcursionId is { } closeId)
                    await trackerRepository.CloseExcursionAsync(closeId, now, ct);
                return (
                    new ExcursionTransition(type, priorExcursionId,
                        RustEnvelopeMapper.CloseReasonFromWire(result.CloseReason) ?? ExcursionCloseReason.Hysteresis),
                    null);

            case ExcursionTransitionType.ExcursionContinues:
                return (new ExcursionTransition(type, priorExcursionId), priorExcursionId);

            default:
                return (new ExcursionTransition(ExcursionTransitionType.None), priorExcursionId);
        }
    }
}
