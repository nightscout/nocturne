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

        foreach (var rule in rules)
        {
            try
            {
                await EvaluateRuleAsync(rule, context, tenantId, ct);
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
                break;

            case ExcursionTransitionType.ExcursionContinues:
                await HandleExcursionContinues(transition, ct);
                break;
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
    }

    private async Task HandleExcursionClosed(
        ExcursionTransition transition,
        Guid tenantId,
        CancellationToken ct)
    {
        if (!transition.ExcursionId.HasValue) return;

        var excursionId = transition.ExcursionId.Value;
        var now = timeProvider.GetUtcNow().UtcDateTime;

        // Get instance IDs before resolving so we can expire deliveries
        var instances = await repository.GetInstancesForExcursionAsync(excursionId, ct);
        var instanceIds = instances.Select(i => i.Id).ToList();

        // Resolve instances for this excursion
        await repository.ResolveInstancesForExcursionAsync(excursionId, now, ct);

        // Cancel pending deliveries
        if (instanceIds.Count > 0)
        {
            await repository.ExpirePendingDeliveriesAsync(instanceIds, ct);
        }

        try
        {
            await broadcastService.BroadcastAlertEventAsync("alert_resolved", new
            {
                excursionId,
                tenantId,
                resolvedAt = now,
            });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to broadcast alert_resolved for excursion {ExcursionId}", excursionId);
        }

        logger.LogInformation("Excursion {ExcursionId} resolved, {Count} instances closed", excursionId, instances.Count);
    }

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
