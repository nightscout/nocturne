using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Nocturne.API.Services.Realtime;
using Nocturne.Core.Alerts.Native;

namespace Nocturne.API.Services.Alerts;

/// <summary>
/// Wires <see cref="IAlertEvaluationEngine"/> and the transition handlers
/// (<see cref="IExcursionOpenedHandler"/>, <see cref="IExcursionResolutionHandler"/>) together
/// into an alert evaluation pass over a set of the current tenant's enabled rules.
/// </summary>
/// <remarks>
/// For each enabled rule, the orchestrator asks the engine seam for the rule's evaluation
/// (root condition truth, excursion transition, auto-resolve) and hands each opening and close to
/// its handler. The engine owns evaluation-state persistence (sustained timers, tracker state,
/// excursion rows), and records with an opening or close the work it owes, so a handler that
/// does not finish is run again by <see cref="AlertSweepJob"/>.
/// Errors from individual rule evaluations are caught and logged without aborting the rest of
/// the evaluation pass. A rule whose stored condition tree cannot be evaluated is logged once
/// per version through <see cref="IUnevaluableRuleNotice.Report"/>; the sweep sends the notice,
/// so evaluation never waits on it. The instants the pass read too far ahead of the clock go to
/// <see cref="IClockAnomalyNotice"/> once, after every rule's side effects. Delayed escalation is a
/// separate rule whose tree references the parent through an <c>alert_state</c> condition.
/// </remarks>
/// <seealso cref="IAlertOrchestrator"/>
/// <seealso cref="IAlertEvaluationEngine"/>
internal sealed class AlertOrchestrator(
    IAlertEvaluationEngine evaluationEngine,
    AlertRuleEvaluationGate gate,
    AlertRuleDecisions decisions,
    ITenantAccessor tenantAccessor,
    ISensorContextEnricher contextEnricher,
    IExcursionOpenedHandler openedHandler,
    IExcursionResolutionHandler resolutionHandler,
    IUnevaluableRuleNotice unevaluableNotice,
    IClockAnomalyNotice clockAnomalyNotice,
    ILogger<AlertOrchestrator> logger)
    : IAlertOrchestrator
{
    public async Task<AlertPassOutcome> EvaluateRulesAsync(
        IReadOnlyList<AlertRuleSnapshot> rules,
        IReadOnlySet<Guid> enabledRuleIds,
        SensorContext context,
        long? readAt,
        CancellationToken ct)
    {
        var tenantId = tenantAccessor.TenantId;
        if (tenantId == Guid.Empty || rules.Count == 0) return AlertPassOutcome.Decided;

        // Before the filter below, which drops a rule whose alert_state names no enabled rule:
        // a malformed alert_state names none, and its rule is still reported.
        unevaluableNotice.Report(rules);

        // Drop chained rules whose alert_state references resolve to disabled/deleted parents.
        var evaluable = RuleReferenceResolver.FilterEvaluable(rules, logger, enabledRuleIds);
        if (evaluable.Count == 0) return AlertPassOutcome.Decided;

        // One enrichment pass for the whole batch — RuleDataNeeds only fetches what any rule
        // in the surviving set will consult (IOB/COB/predictions/active-alerts/etc.).
        var enriched = await contextEnricher.EnrichAsync(context, evaluable, ct);
        var stamp = readAt is { } read ? AlertRuleDecisions.StampOf(enriched, read) : null;

        var anomalies = new Dictionary<string, ClockAnomaly>(StringComparer.Ordinal);
        var outcome = AlertPassOutcome.Decided;
        foreach (var rule in evaluable)
        {
            try
            {
                if (await EvaluateRuleAsync(rule, enriched, tenantId, stamp, ct) is not { } evaluated)
                {
                    if (outcome == AlertPassOutcome.Decided)
                        outcome = AlertPassOutcome.Superseded;
                    continue;
                }
                foreach (var anomaly in evaluated)
                {
                    if (!anomalies.TryGetValue(anomaly.Fact, out var seen) || seen.Ahead < anomaly.Ahead)
                        anomalies[anomaly.Fact] = anomaly;
                }
            }
            catch (RustAlertEngineException ex) when (ex.IsConditionRejection)
            {
                // The rule is skipped; IUnevaluableRuleNotice has reported why.
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                outcome = AlertPassOutcome.Failed;
                logger.LogError(ex, "Error evaluating alert rule {AlertRuleId} for tenant {TenantId}",
                    rule.Id, tenantId);
            }
        }

        await clockAnomalyNotice.ReportIsolatedAsync(enriched, anomalies.Values, logger, ct);
        return outcome;
    }

    /// <remarks>
    /// The check against <see cref="AlertRuleDecisions"/>, the engine's evaluation and its record are
    /// one section of the rule's lock, which every pass over the rule takes, so no other decision
    /// lands between them. A rule a newer context decided is skipped with nothing recorded; an
    /// evaluation that throws records nothing. Delivery runs after the section.
    /// </remarks>
    /// <returns>The clock anomalies the evaluation read, or null when a newer context had decided the rule.</returns>
    private async Task<IReadOnlyList<ClockAnomaly>?> EvaluateRuleAsync(
        AlertRuleSnapshot rule,
        SensorContext context,
        Guid tenantId,
        ContextStamp? stamp,
        CancellationToken ct)
    {
        // The engine runs the full per-rule driver sequence (root eval → tracker →
        // unconditional auto-resolve under the auto_resolve path root) and persists all
        // evaluation state; this method applies the side effects its transitions call for.
        var evaluation = await gate.RunExclusiveAsync<AlertEngineEvaluation?>(
            rule.Id,
            async () =>
            {
                if (stamp is { } checking && decisions.DecidedByNewer(rule.Id, checking))
                    return null;
                var evaluated = await evaluationEngine.EvaluateRuleAsync(rule, context, AlertEngineOptions.Default, ct);
                if (stamp is { } deciding)
                    decisions.Decided(rule.Id, deciding);
                return evaluated;
            },
            ct);
        if (evaluation is null)
        {
            logger.LogDebug(
                "Alert rule {AlertRuleId} of tenant {TenantId} was decided by a newer reading; this pass skips it",
                rule.Id, tenantId);
            return null;
        }
        if (evaluation.Skipped) return [];

        switch (evaluation.Transition.Type)
        {
            case ExcursionTransitionType.ExcursionOpened when evaluation.Transition.ExcursionId is { } opened:
                await openedHandler.HandleOpenedAsync(
                    rule, opened, context, tenantId, late: false, evaluation.MetBy,
                    callerCloses: evaluation.AutoResolveTransition is { Type: ExcursionTransitionType.ExcursionClosed } closing
                        && closing.ExcursionId == opened,
                    ct);
                break;

            case ExcursionTransitionType.ExcursionClosed:
                await HandleExcursionClosed(evaluation.Transition, tenantId, ct);
                break;

            case ExcursionTransitionType.ExcursionContinues:
                // Nothing to do per-reading. The dispatch happened at open; subsequent
                // notifications-while-firing are a separate-rule concern (alert_state), and
                // re-notifying after a snooze is AlertSnoozeService.ResumeAsync's.
                break;
        }

        // Auto-resolve close: route through the existing close pathway so
        // resolution_reason is stamped and the alert_resolved broadcast fires.
        if (evaluation.AutoResolveTransition is { Type: ExcursionTransitionType.ExcursionClosed } autoResolveClose)
        {
            await HandleExcursionClosed(autoResolveClose, tenantId, ct);
        }

        return evaluation.ClockAnomalies;
    }

    private Task HandleExcursionClosed(
        ExcursionTransition transition,
        Guid tenantId,
        CancellationToken ct) =>
        resolutionHandler.HandleClosedAsync(transition, tenantId, ct);
}
