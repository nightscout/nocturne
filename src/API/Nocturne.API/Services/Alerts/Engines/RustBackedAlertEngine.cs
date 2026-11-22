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
/// between calls: this adapter reads the rule's persisted state (sustained timers and tracker state
/// via <see cref="IAlertTrackerRepository"/>) and threads it through the envelope. A rule
/// evaluation and an auto-resolve pass persist their tracker decision and timer deltas together
/// through <see cref="ExcursionTransitionWriter"/>, which drops both when the rule changed since it
/// was read. <see cref="EvaluateSnoozeAsync"/> makes no tracker decision, and the writer drops its
/// timer deltas when the rule's snooze conditions changed since it read them.
/// </summary>
/// <remarks>
/// Excursion identity: the FFI tracker uses opaque ordinals and this host owns the GUIDs, so
/// only "has an active excursion" crosses the boundary (<see cref="RustEnvelopeMapper.BuildTracker"/>).
/// </remarks>
internal sealed class RustBackedAlertEngine(
    IAlertTrackerRepository trackerRepository,
    AlertRuleEvaluationGate gate,
    AlertEngineErrors errors,
    ConditionVersionLog conditionLog,
    NotTrueRecordingFloor notTrueFloor,
    TimeProvider timeProvider,
    ILogger<RustBackedAlertEngine> logger)
    : IAlertEvaluationEngine
{
    /// <summary>
    /// The rule's stored sustained state, as every evaluation here reads it: through
    /// <see cref="NotTrueRecordingFloor.Raise"/>.
    /// </summary>
    private async Task<ConditionTimers> ReadTimersAsync(Guid ruleId, CancellationToken ct) =>
        notTrueFloor.Raise(await trackerRepository.Timers.GetAllForRuleAsync(ruleId, ct));

    /// <inheritdoc/>
    public async Task<AlertEngineEvaluation> EvaluateRuleAsync(
        AlertRuleSnapshot rule,
        SensorContext context,
        AlertEngineOptions options,
        CancellationToken ct)
    {
        using var lease = await gate.AcquireAsync(rule.Id, ct);

        // The snapshot does not carry HysteresisMinutes.
        var ruleRow = await trackerRepository.GetRuleAsync(rule.Id, ct);
        if (ruleRow is null)
        {
            logger.LogWarning("Alert rule {AlertRuleId} not found; skipping evaluation", rule.Id);
            var nodeOnly = EvaluateRuleNodeWithoutTracker(rule, context);
            return new AlertEngineEvaluation
            {
                ConditionMet = nodeOnly,
                Transition = new ExcursionTransition(ExcursionTransitionType.None),
            };
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var timers = await ReadTimersAsync(rule.Id, ct);
        var trackerState = await trackerRepository.GetTrackerStateAsync(rule.Id, ct);

        var (response, result) = errors.Track("evaluate", () =>
        {
            var response = RustAlertEngine.Evaluate(
                RustEnvelopeMapper.BuildRule(ruleRow),
                RustEnvelopeMapper.BuildContext(context),
                now,
                RustEnvelopeMapper.BuildTimers(timers.FirstTrue),
                RustEnvelopeMapper.BuildTracker(trackerState),
                options.IncludeLeafValues,
                RustEnvelopeMapper.BuildTimers(timers.NotTrue));
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

        var heldFacts = HeldFacts.Format(result.UnknownFacts?.Select(f => new HeldFact(f.Fact, f.Reason)));
        var decision = new TrackerDecision(
            RustEnvelopeMapper.TransitionFromWire(result.Transition!.Value),
            result.CloseReason is { } reason ? RustEnvelopeMapper.CloseReasonFromWire(reason) : null,
            RustEnvelopeMapper.PostStateFromWire(response.Tracker!, heldFacts));
        // The engine decided on the row read under the lease, which an edit can have changed
        // since the snapshot was loaded.
        var (transition, autoResolveTransition) = await ExcursionTransitionWriter.ApplyAsync(
            trackerRepository, logger, rule.Id, trackerState, decision, AlertRuleConditions.Of(ruleRow), now, ct,
            result.AutoResolved, result.TimerOps, RecordedNextChange(rule, result, context), result.HeldTooLong);

        return new AlertEngineEvaluation
        {
            ConditionMet = result.Root!.Value,
            ConditionUnknown = result.Unknown,
            UnknownFacts = result.UnknownFacts?.Select(f => new HeldFact(f.Fact, f.Reason)).ToList() ?? [],
            HeldTooLong = result.HeldTooLong,
            Transition = transition,
            AutoResolveTransition = autoResolveTransition,
            LeafValues = options.IncludeLeafValues
                ? result.Leaves!.ToDictionary(l => l.LeafId, l => l.Unknown ? null : (bool?)l.Value)
                : null,
            NextChangeAt = result.NextChangeAt,
            ClockAnomalies = result.ClockAnomaly?
                .Select(a => new ClockAnomaly(a.Fact, TimeSpan.FromMilliseconds(a.AheadMs)))
                .ToList() ?? [],
            MetBy = RustEnvelopeMapper.MetByFromWire(result.MetBy),
        };
    }

    /// <summary>
    /// The next change to record for <paramref name="rule"/>: only one that
    /// <see cref="RuleTrees.WaitsForNextChange"/> is ever read, so no other is written, and none
    /// from a library that does not report it (<see cref="RustAlertEngine.ReportsNextChange()"/>).
    /// </summary>
    private NextChange? RecordedNextChange(AlertRuleSnapshot rule, RustRuleResult result, SensorContext context)
    {
        try
        {
            return LibraryReportsNextChange() && RuleTrees.WaitsForNextChange(rule)
                ? new NextChange(result.NextChangeAt, NextChange.GlucoseOf(context))
                : null;
        }
        catch (RustAlertEngineException ex)
        {
            logger.LogWarning(ex,
                "The engine could not read the condition trees of rule {AlertRuleId}; its next change is not recorded",
                rule.Id);
            return null;
        }
    }

    private static int _warnedOfNoNextChange;

    /// <summary>
    /// <see cref="RustAlertEngine.ReportsNextChange()"/>, warning the first time in the process
    /// that it does not.
    /// </summary>
    private bool LibraryReportsNextChange()
    {
        if (RustAlertEngine.ReportsNextChange())
            return true;
        if (Interlocked.Exchange(ref _warnedOfNoNextChange, 1) == 0)
        {
            logger.LogWarning(
                "The alert engine library {Version} does not report next changes; the sweep evaluates every wall-clock rule on every run",
                RustAlertEngine.Version());
        }
        return false;
    }

    /// <inheritdoc/>
    public async Task<bool> EvaluateSnoozeAsync(
        Guid ruleId,
        JsonElement conditions,
        SensorContext context,
        CancellationToken ct)
    {
        using var lease = await gate.AcquireAsync(ruleId, ct);

        var response = RustAuxiliaryEvaluation.EvaluateNode(
            errors, ruleId, SmartSnoozeConfig.ConditionTree(conditions), Conditions.AlertConditionTypeNames.SnoozePathRoot,
            context, timeProvider.GetUtcNow().UtcDateTime, await ReadTimersAsync(ruleId, ct));
        var held = await ExcursionTransitionWriter.ApplyTimerOpsAsync(
            trackerRepository, logger, ruleId,
            rule => SmartSnoozeConfig.SameConditions(SmartSnoozeConfig.StoredConditions(rule.ClientConfiguration), conditions),
            response.TimerOps, ct);
        return held && response.Value!.Value;
    }

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
        var timers = await ReadTimersAsync(rule.Id, ct);
        var outcome = RustAuxiliaryEvaluation.AutoResolve(
            errors, rule, context, now, timers, state, logger);

        var decision = outcome.Close ?? new TrackerDecision(ExcursionTransitionType.None, null, null);
        var (transition, _) = await ExcursionTransitionWriter.ApplyAsync(
            trackerRepository, logger, rule.Id, state, decision, AlertRuleConditions.Of(rule), now, ct,
            timerOps: outcome.Node?.TimerOps);
        return transition;
    }

    /// <summary>
    /// Evaluates the condition tree of a rule whose row is gone, without touching the tracker or
    /// persisting timer deltas: the rule's timers were deleted with it, and a timer row written
    /// for it would have no rule to belong to.
    /// </summary>
    private bool EvaluateRuleNodeWithoutTracker(AlertRuleSnapshot rule, SensorContext context)
    {
        var wire = Conditions.AlertConditionTypeNames.ToWireString(rule.ConditionType);
        var node = RustEnvelopeMapper.ParseJson(
            RustEnvelopeMapper.WrapPayload(wire, rule.ConditionParams));
        var response = RustAuxiliaryEvaluation.EvaluateNode(
            errors, rule.Id, node, wire, context, timeProvider.GetUtcNow().UtcDateTime,
            ConditionTimers.None);
        return response.Value!.Value;
    }
}
