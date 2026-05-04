using System.Text.Json;
using Nocturne.API.Services.Alerts.Evaluators;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Nocturne.API.Services.Realtime;

namespace Nocturne.API.Services.Alerts;

/// <summary>
/// Wires <see cref="ConditionEvaluatorRegistry"/>, <see cref="IExcursionTracker"/>, and
/// <see cref="IAlertDeliveryService"/> together into a per-reading alert evaluation pass.
/// Called on every new glucose reading to evaluate all enabled alert rules for the current tenant.
/// </summary>
/// <remarks>
/// For each enabled rule, the orchestrator resolves the appropriate <see cref="IConditionEvaluator"/>,
/// checks whether the condition is met, manages excursion lifecycle (open/resolve), applies
/// engine-level DND suppression, and dispatches delivery to the rule's flat channel list.
/// Errors from individual rule evaluations are caught and logged without aborting the rest of
/// the evaluation pass. Escalation chains are no longer first-class — express delayed escalation
/// as a separate alert rule whose tree references the parent via the <c>alert_state</c> condition.
/// </remarks>
/// <seealso cref="IAlertOrchestrator"/>
/// <seealso cref="ConditionEvaluatorRegistry"/>
/// <seealso cref="IExcursionTracker"/>
/// <seealso cref="IAlertDeliveryService"/>
internal sealed class AlertOrchestrator(
    ConditionEvaluatorRegistry evaluatorRegistry,
    IExcursionTracker excursionTracker,
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

        if (rules.Count == 0) return;

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
        var evaluator = evaluatorRegistry.GetEvaluator(rule.ConditionType);
        if (evaluator is null)
        {
            logger.LogWarning("No evaluator registered for condition type '{ConditionType}'", rule.ConditionType);
            return;
        }

        // Seed CurrentRuleId / CurrentPath so stateful evaluators (sustained) can key persistent
        // timers, and recursive evaluators (composite/not/sustained) can extend the path as they
        // descend. Root path is the rule's condition kind, e.g. "composite" — matching the
        // convention in ConditionPath.Walk.
        var rootContext = context with
        {
            CurrentRuleId = rule.Id,
            CurrentPath = AlertConditionTypeNames.ToWireString(rule.ConditionType),
        };
        var conditionMet = await evaluator.EvaluateAsync(rule.ConditionParams, rootContext, ct);
        var transition = await excursionTracker.ProcessEvaluationAsync(rule.Id, conditionMet, ct);

        switch (transition.Type)
        {
            case ExcursionTransitionType.ExcursionOpened:
                await HandleExcursionOpened(rule, transition, context, tenantId, ct);
                break;

            case ExcursionTransitionType.ExcursionClosed:
                await HandleExcursionClosed(transition, tenantId, ct);
                return;

            case ExcursionTransitionType.ExcursionContinues:
                // Nothing to do per-reading. The dispatch happened at open; subsequent
                // notifications-while-firing are a separate-rule concern (alert_state).
                break;
        }

        await TryAutoResolveAsync(rule, context, tenantId, ct);
    }

    /// <summary>
    /// Out-of-band auto-resolve: evaluates <see cref="AlertRuleSnapshot.AutoResolveParams"/>
    /// against the same enriched context used by the main rule. If true, force-closes the
    /// active excursion via the tracker and routes the resulting transition through the
    /// existing close pathway so <c>resolution_reason</c> is stamped and the
    /// <c>alert_resolved</c> broadcast fires.
    /// </summary>
    private async Task TryAutoResolveAsync(
        AlertRuleSnapshot rule,
        SensorContext context,
        Guid tenantId,
        CancellationToken ct)
    {
        if (!rule.AutoResolveEnabled || string.IsNullOrWhiteSpace(rule.AutoResolveParams))
            return;

        var activeExcursionId = await excursionTracker.GetActiveExcursionIdAsync(rule.Id, ct);
        if (activeExcursionId is null)
            return;

        ConditionNode? node;
        try
        {
            node = JsonSerializer.Deserialize<ConditionNode>(rule.AutoResolveParams, EvaluatorJson.Options);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Failed to parse AutoResolveParams for rule {AlertRuleId}; skipping", rule.Id);
            return;
        }

        if (node is null) return;

        // Path-prefix auto-resolve so any nested sustained timers don't collide with timers
        // owned by the main rule body (which roots at e.g. "composite"). Both per-reading
        // (orchestrator) and periodic (sweep) auto-resolve paths share this root — same
        // (ruleId, path) timer row, by design.
        var autoResolveContext = context with
        {
            CurrentRuleId = rule.Id,
            CurrentPath = AlertConditionTypeNames.AutoResolvePathRoot,
        };

        bool shouldResolve;
        try
        {
            shouldResolve = await evaluatorRegistry.EvaluateNodeAsync(node, autoResolveContext, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Auto-resolve evaluation failed for rule {AlertRuleId}", rule.Id);
            return;
        }

        if (!shouldResolve) return;

        var transition = await excursionTracker.ForceCloseAsync(rule.Id, ExcursionCloseReason.AutoResolve, ct);
        if (transition.Type == ExcursionTransitionType.ExcursionClosed)
        {
            await HandleExcursionClosed(transition, tenantId, ct);
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
        var channels = await repository.GetChannelsForRuleAsync(rule.Id, ct);

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

        // DND suppression: when the tenant is in Do Not Disturb, non-Critical rules without
        // an explicit "allow through DND" opt-in still get a history row written (so Replay
        // can show "would have fired but you were in DND"), but the dispatch is skipped.
        // Critical rules implicitly bypass DND regardless of the per-rule flag.
        var suppressedByDnd =
            context.ActiveDoNotDisturb is not null
            && rule.Severity != AlertRuleSeverity.Critical
            && !rule.AllowThroughDnd;

        if (suppressedByDnd)
        {
            await repository.MarkInstanceSuppressedAsync(tenantId, instance.Id, "dnd", ct);
            logger.LogInformation(
                "Alert instance {InstanceId} for rule {RuleName} suppressed by DND ({Source})",
                instance.Id, rule.Name, context.ActiveDoNotDisturb!.Source);
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
        if (rule.Severity == AlertRuleSeverity.Info && !suppressedByDnd)
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
