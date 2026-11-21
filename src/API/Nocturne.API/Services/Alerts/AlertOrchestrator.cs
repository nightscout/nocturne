using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Nocturne.API.Services.Realtime;
using Nocturne.Core.Alerts.Native;

namespace Nocturne.API.Services.Alerts;

/// <summary>
/// Wires <see cref="IAlertEvaluationEngine"/> and <see cref="IAlertDeliveryService"/>
/// together into a per-reading alert evaluation pass.
/// Called on every new glucose reading to evaluate all enabled alert rules for the current tenant.
/// </summary>
/// <remarks>
/// For each enabled rule, the orchestrator asks the engine seam for the rule's evaluation
/// (root condition truth, excursion transition, auto-resolve) and applies the side effects:
/// instance creation, engine-level DND suppression, delivery dispatch to the rule's flat
/// channel list, info auto-ack, and close handling. The engine owns evaluation-state
/// persistence (sustained timers, tracker state, excursion rows).
/// Errors from individual rule evaluations are caught and logged without aborting the rest of
/// the evaluation pass. A rule whose stored condition tree cannot be evaluated is logged once
/// per version through <see cref="IUnevaluableRuleNotice.Report"/>; the sweep sends the notice,
/// so evaluation never waits on it. The instants the pass read too far ahead of the clock go to
/// <see cref="IClockAnomalyNotice"/> once, after every rule's side effects. Delayed escalation is a
/// separate rule whose tree references the parent through an <c>alert_state</c> condition.
/// </remarks>
/// <seealso cref="IAlertOrchestrator"/>
/// <seealso cref="IAlertEvaluationEngine"/>
/// <seealso cref="IAlertDeliveryService"/>
internal sealed class AlertOrchestrator(
    IAlertEvaluationEngine evaluationEngine,
    IAlertRepository repository,
    IAlertRuleRepository ruleRepository,
    ITenantAccessor tenantAccessor,
    IAlertDeliveryService deliveryService,
    ISensorContextEnricher contextEnricher,
    IAlertAcknowledgementService acknowledgementService,
    IExcursionResolutionHandler resolutionHandler,
    IUnevaluableRuleNotice unevaluableNotice,
    IClockAnomalyNotice clockAnomalyNotice,
    TimeProvider timeProvider,
    ILogger<AlertOrchestrator> logger)
    : IAlertOrchestrator
{
    public async Task EvaluateAsync(SensorContext context, CancellationToken ct)
    {
        if (tenantAccessor.TenantId == Guid.Empty) return;

        var rules = await repository.GetEnabledRulesAsync(ct);
        await EvaluateRulesAsync(rules, rules.Select(r => r.Id).ToHashSet(), context, ct);
    }

    public async Task EvaluateRulesAsync(
        IReadOnlyList<AlertRuleSnapshot> rules,
        IReadOnlySet<Guid> enabledRuleIds,
        SensorContext context,
        CancellationToken ct)
    {
        var tenantId = tenantAccessor.TenantId;
        if (tenantId == Guid.Empty || rules.Count == 0) return;

        // Before the filter below, which drops a rule whose alert_state names no enabled rule:
        // a malformed alert_state names none, and its rule is still reported.
        unevaluableNotice.Report(rules);

        // Drop chained rules whose alert_state references resolve to disabled/deleted parents.
        var evaluable = RuleReferenceResolver.FilterEvaluable(rules, logger, enabledRuleIds);
        if (evaluable.Count == 0) return;

        // One enrichment pass for the whole batch — RuleDataNeeds only fetches what any rule
        // in the surviving set will consult (IOB/COB/predictions/active-alerts/etc.).
        var enriched = await contextEnricher.EnrichAsync(context, evaluable, ct);

        var anomalies = new Dictionary<string, ClockAnomaly>(StringComparer.Ordinal);
        foreach (var rule in evaluable)
        {
            try
            {
                foreach (var anomaly in await EvaluateRuleAsync(rule, enriched, tenantId, ct))
                {
                    if (!anomalies.TryGetValue(anomaly.Fact, out var seen) || seen.Ahead < anomaly.Ahead)
                        anomalies[anomaly.Fact] = anomaly;
                }
            }
            catch (RustAlertEngineException ex) when (ex.IsConditionRejection)
            {
                // The rule is skipped; IUnevaluableRuleNotice has reported why.
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error evaluating alert rule {AlertRuleId} for tenant {TenantId}",
                    rule.Id, tenantId);
            }
        }

        await clockAnomalyNotice.ReportIsolatedAsync(enriched, anomalies.Values, logger, ct);
    }

    private async Task<IReadOnlyList<ClockAnomaly>> EvaluateRuleAsync(
        AlertRuleSnapshot rule,
        SensorContext context,
        Guid tenantId,
        CancellationToken ct)
    {
        // The engine runs the full per-rule driver sequence (root eval → tracker →
        // unconditional auto-resolve under the auto_resolve path root) and persists all
        // evaluation state; this method applies the side effects its transitions call for.
        var evaluation = await evaluationEngine.EvaluateRuleAsync(rule, context, AlertEngineOptions.Default, ct);
        if (evaluation.Skipped) return [];

        switch (evaluation.Transition.Type)
        {
            case ExcursionTransitionType.ExcursionOpened:
                await HandleExcursionOpened(rule, evaluation.Transition, context, tenantId, ct);
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

    private async Task HandleExcursionOpened(
        AlertRuleSnapshot rule,
        ExcursionTransition transition,
        SensorContext context,
        Guid tenantId,
        CancellationToken ct)
    {
        if (!transition.ExcursionId.HasValue) return;

        var excursionId = transition.ExcursionId.Value;
        var now = timeProvider.GetUtcNow().UtcDateTime;

        // Create alert instance with the simplified, schedule-free shape.
        var request = new CreateAlertInstanceRequest(
            ExcursionId: excursionId,
            Status: "triggered",
            TriggeredAt: now);

        var instance = await repository.CreateInstanceAsync(request, ct);

        // Look up the rule's flat channel list; an empty list is allowed (the user explicitly
        // chose "no delivery channels" — alert still tracked, just not pushed anywhere).
        var channels = (await ruleRepository.GetChannelsAsync([rule.Id], ct)).GetValueOrDefault(rule.Id) ?? [];

        // Active excursion count + tenant subject for payload.
        var activeExcursionCount = await repository.CountActiveExcursionsAsync(ct);
        var tenant = await repository.GetTenantAlertContextAsync(tenantId, ct);

        var payload = AlertPayloads.Build(
            rule, context, tenantId, excursionId, instance.Id, tenant, activeExcursionCount, now);

        var suppressingScope = await DndGatedDispatch.DispatchAsync(
            rule, context.ActiveDndScopes, instance.Id, channels, payload, repository, deliveryService, logger, ct);

        logger.LogInformation(
            "Alert instance {InstanceId} created for excursion {ExcursionId}, rule {RuleName}",
            instance.Id, excursionId, rule.Name);

        // Info severity is fire-and-forget: deliver once, then auto-acknowledge so the alert
        // renders as acknowledged in the UI. broadcast=false avoids racing the
        // alert_acknowledged event against the alert_dispatch we just emitted for an excursion
        // the FE has not yet finished rendering.
        //
        // Skipped when the instance was suppressed by DND: there was no dispatch, so there is
        // no alert_dispatch event to "follow up" with an ack, and emitting an alert_acknowledged
        // for a suppressed alert would race the suppression history row.
        if (rule.Severity == AlertRuleSeverity.Info && suppressingScope is null)
        {
            await acknowledgementService.AcknowledgeExcursionAsync(
                tenantId, excursionId, "system:auto-ack-on-trigger", AlertAcknowledgementAuthority.System,
                broadcast: false, ct);
        }
    }

    private Task HandleExcursionClosed(
        ExcursionTransition transition,
        Guid tenantId,
        CancellationToken ct) =>
        resolutionHandler.HandleClosedAsync(transition, tenantId, ct);
}
