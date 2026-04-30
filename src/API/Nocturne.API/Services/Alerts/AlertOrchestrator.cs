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
/// checks whether the condition is met, manages excursion lifecycle (open/resolve), advances
/// escalation steps, and dispatches delivery. Errors from individual rule evaluations are caught
/// and logged without aborting the rest of the evaluation pass.
/// </remarks>
/// <seealso cref="IAlertOrchestrator"/>
/// <seealso cref="ConditionEvaluatorRegistry"/>
/// <seealso cref="IExcursionTracker"/>
/// <seealso cref="IAlertDeliveryService"/>
/// <seealso cref="IEscalationAdvancer"/>
internal sealed class AlertOrchestrator(
    ConditionEvaluatorRegistry evaluatorRegistry,
    IExcursionTracker excursionTracker,
    IAlertRepository repository,
    IEscalationAdvancer escalationAdvancer,
    ITenantAccessor tenantAccessor,
    IAlertDeliveryService deliveryService,
    ISignalRBroadcastService broadcastService,
    ISensorContextEnricher contextEnricher,
    IAlertAcknowledgementService acknowledgementService,
    IExcursionResolutionHandler resolutionHandler,
    TimeProvider timeProvider,
    ILogger<AlertOrchestrator> logger)
    : IAlertOrchestrator
{
    private static readonly JsonSerializerOptions AutoResolveJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

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
                await HandleExcursionContinues(transition, ct);
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
            node = JsonSerializer.Deserialize<ConditionNode>(rule.AutoResolveParams, AutoResolveJsonOptions);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Failed to parse AutoResolveParams for rule {AlertRuleId}; skipping", rule.Id);
            return;
        }

        if (node is null) return;

        // Path-prefix auto-resolve so any nested sustained timers don't collide with timers
        // owned by the main rule body (which roots at e.g. "composite").
        var autoResolveContext = context with
        {
            CurrentRuleId = rule.Id,
            CurrentPath = "auto_resolve",
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

        // Resolve active schedule
        var schedules = await repository.GetSchedulesForRuleAsync(rule.Id, ct);

        if (schedules.Count == 0)
        {
            logger.LogWarning("No schedules found for rule {AlertRuleId}; skipping instance creation", rule.Id);
            return;
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var activeSchedule = ScheduleResolver.Resolve(schedules, now);

        // Get escalation steps for step 0
        var steps = await repository.GetEscalationStepsAsync(activeSchedule.Id, ct);

        // Create alert instance
        var request = new CreateAlertInstanceRequest(
            TenantId: tenantId,
            ExcursionId: excursionId,
            ScheduleId: activeSchedule.Id,
            InitialStepOrder: 0,
            Status: steps.Count > 1 ? "escalating" : "triggered",
            TriggeredAt: now,
            NextEscalationAt: steps.Count > 1 ? now.AddSeconds(steps[0].DelaySeconds) : null);

        var instance = await repository.CreateInstanceAsync(request, ct);

        // Count active excursions for payload
        var activeExcursionCount = await repository.CountActiveExcursionsAsync(tenantId, ct);

        // Get tenant subject name
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
        };

        // Dispatch delivery for step 0
        if (steps.Count > 0)
        {
            await deliveryService.DispatchAsync(instance.Id, 0, payload, ct);
        }

        logger.LogInformation(
            "Alert instance {InstanceId} created for excursion {ExcursionId}, rule {RuleName}",
            instance.Id, excursionId, rule.Name);

        // Info severity is fire-and-forget: deliver once, then auto-acknowledge so escalation
        // halts and the alert renders as acknowledged in the UI. Channel routing for Info is
        // a frontend default (ChannelPicker); the orchestrator does not gate channels by severity.
        if (rule.Severity == AlertRuleSeverity.Info)
        {
            await acknowledgementService.AcknowledgeExcursionAsync(
                excursionId, "system:auto-ack-on-trigger", ct);
        }
    }

    private Task HandleExcursionClosed(
        ExcursionTransition transition,
        Guid tenantId,
        CancellationToken ct) =>
        resolutionHandler.HandleClosedAsync(transition, tenantId, ct);

    private async Task HandleExcursionContinues(
        ExcursionTransition transition,
        CancellationToken ct)
    {
        if (!transition.ExcursionId.HasValue) return;

        // Check for event-driven escalation advancement
        var now = timeProvider.GetUtcNow().UtcDateTime;

        var allDueInstances = await repository.GetEscalatingInstancesDueAsync(now, ct);
        var instances = allDueInstances
            .Where(i => i.AlertExcursionId == transition.ExcursionId.Value)
            .ToList();

        foreach (var instance in instances)
        {
            await escalationAdvancer.AdvanceAsync(instance, ct);
        }
    }

}
