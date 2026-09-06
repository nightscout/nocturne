using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Nocturne.API.Services.Realtime;

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
/// persistence (sustained timers, tracker state, excursion rows); which engine runs
/// (managed C# evaluators, Rust over FFI, or shadow) is selected by <c>Alerts:Engine</c>.
/// Errors from individual rule evaluations are caught and logged without aborting the rest of
/// the evaluation pass. Escalation chains are no longer first-class — express delayed escalation
/// as a separate alert rule whose tree references the parent via the <c>alert_state</c> condition.
/// </remarks>
/// <seealso cref="IAlertOrchestrator"/>
/// <seealso cref="IAlertEvaluationEngine"/>
/// <seealso cref="IAlertDeliveryService"/>
internal sealed class AlertOrchestrator(
    IAlertEvaluationEngine evaluationEngine,
    IAlertRepository repository,
    ITenantAccessor tenantAccessor,
    IAlertDeliveryService deliveryService,
    ISensorContextEnricher contextEnricher,
    IAlertAcknowledgementService acknowledgementService,
    IExcursionResolutionHandler resolutionHandler,
    TimeProvider timeProvider,
    ILogger<AlertOrchestrator> logger)
    : IAlertOrchestrator
{
    public async Task EvaluateAsync(SensorContext context, CancellationToken ct)
    {
        var tenantId = tenantAccessor.TenantId;
        if (tenantId == Guid.Empty) return;

        var rules = await repository.GetEnabledRulesAsync(tenantId, ct);
        await EvaluateRulesAsync(rules, context, ct);
    }

    public async Task EvaluateRulesAsync(
        IReadOnlyList<AlertRuleSnapshot> rules, SensorContext context, CancellationToken ct)
    {
        var tenantId = tenantAccessor.TenantId;
        if (tenantId == Guid.Empty || rules.Count == 0) return;

        // Drop chained rules whose alert_state references resolve to disabled/deleted parents.
        var evaluable = RuleReferenceResolver.FilterEvaluable(rules);
        if (evaluable.Count == 0) return;

        // One enrichment pass for the whole batch — RuleDataNeeds only fetches what any rule
        // in the surviving set will consult (IOB/COB/predictions/active-alerts/etc.).
        var enriched = await contextEnricher.EnrichAsync(context, evaluable, tenantId, ct);

        foreach (var rule in evaluable)
        {
            try
            {
                await EvaluateRuleAsync(rule, enriched, tenantId, ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error evaluating alert rule {AlertRuleId} for tenant {TenantId}",
                    rule.Id, tenantId);
            }
        }
    }

    private async Task EvaluateRuleAsync(
        AlertRuleSnapshot rule,
        SensorContext context,
        Guid tenantId,
        CancellationToken ct)
    {
        // The engine runs the full per-rule driver sequence (root eval → tracker →
        // unconditional auto-resolve under the auto_resolve path root) and persists all
        // evaluation state; this method applies the side effects its transitions call for.
        var evaluation = await evaluationEngine.EvaluateRuleAsync(rule, context, AlertEngineOptions.Default, ct);
        if (evaluation.Skipped) return;

        switch (evaluation.Transition.Type)
        {
            case ExcursionTransitionType.ExcursionOpened:
                await HandleExcursionOpened(rule, evaluation.Transition, context, tenantId, ct);
                break;

            case ExcursionTransitionType.ExcursionClosed:
                await HandleExcursionClosed(evaluation.Transition, tenantId, ct);
                return;

            case ExcursionTransitionType.ExcursionContinues:
                // Nothing to do per-reading. The dispatch happened at open; subsequent
                // notifications-while-firing are a separate-rule concern (alert_state).
                break;
        }

        // Auto-resolve close: route through the existing close pathway so
        // resolution_reason is stamped and the alert_resolved broadcast fires.
        if (evaluation.AutoResolveTransition is { Type: ExcursionTransitionType.ExcursionClosed } autoResolveClose)
        {
            await HandleExcursionClosed(autoResolveClose, tenantId, ct);
        }
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
            TenantId: tenantId,
            ExcursionId: excursionId,
            Status: "triggered",
            TriggeredAt: now);

        var instance = await repository.CreateInstanceAsync(request, ct);

        // Look up the rule's flat channel list; an empty list is allowed (the user explicitly
        // chose "no delivery channels" — alert still tracked, just not pushed anywhere).
        var channels = await repository.GetChannelsForRuleAsync(tenantId, rule.Id, ct);

        // Active excursion count + tenant subject for payload.
        var activeExcursionCount = await repository.CountActiveExcursionsAsync(tenantId, ct);
        var tenant = await repository.GetTenantAlertContextAsync(tenantId, ct);

        var payload = new AlertPayload
        {
            AlertType = rule.ConditionType,
            RuleName = rule.Name,
            GlucoseValue = context.LatestValue,
            Trend = null,
            TrendRate = context.TrendRate,
            ReadingTimestamp = context.LatestTimestamp ?? now,
            ExcursionId = excursionId,
            InstanceId = instance.Id,
            TenantId = tenantId,
            SubjectName = tenant?.SubjectName ?? tenant?.DisplayName ?? "Unknown",
            ActiveExcursionCount = activeExcursionCount,
            Severity = rule.Severity,
        };

        // Scoped DND suppression (ADR 0004): when an active DND scope covers this rule's class,
        // a non-Critical rule without an explicit "allow through DND" opt-in still gets a history
        // row written (so Replay can show "would have fired but you were in DND"), but the
        // dispatch is skipped. Critical rules implicitly bypass DND regardless of the per-rule
        // flag. The suppressing scope is recorded on the instance (dnd:lows/highs/all).
        var suppressingScope = DndSuppressionGate.SuppressingScope(rule, context.ActiveDndScopes);

        if (suppressingScope is { } scope)
        {
            await repository.MarkInstanceSuppressedAsync(
                tenantId, instance.Id, DndSuppressionGate.SuppressionReason(scope), ct);
            logger.LogInformation(
                "Alert instance {InstanceId} for rule {RuleName} suppressed by DND (scope {Scope})",
                instance.Id, rule.Name, scope);
        }
        else
        {
            await deliveryService.DispatchAsync(instance.Id, channels, payload, ct);
        }

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
                tenantId, excursionId, "system:auto-ack-on-trigger", broadcast: false, ct);
        }
    }

    private Task HandleExcursionClosed(
        ExcursionTransition transition,
        Guid tenantId,
        CancellationToken ct) =>
        resolutionHandler.HandleClosedAsync(transition, tenantId, ct);
}
