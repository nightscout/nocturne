using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.API.Services.Alerts.Evaluators;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;

namespace Nocturne.API.Services.Alerts;

/// <summary>
/// <see cref="BackgroundService"/> that runs every 30 seconds to maintain alert lifecycle state:
/// </summary>
/// <remarks>
/// <list type="number">
///   <item>Close excursions whose hysteresis window has expired.</item>
///   <item>Evaluate signal-loss rules for tenants with stale CGM readings.</item>
///   <item>Check snoozed instances for smart-snooze extension or re-fire.</item>
///   <item>Run periodic auto-resolve for excursions whose conditions don't depend on the latest reading.</item>
/// </list>
/// Each sweep creates a child DI scope so that scoped services (DbContext, tenant repositories)
/// are properly isolated and disposed. Individual tenant failures are caught and logged without
/// aborting the rest of the sweep. The escalation-advancement step that previously lived here
/// went away with the schedule/escalation-step rip-out — express delayed escalation as a
/// separate alert rule whose tree references the parent via the <c>alert_state</c> condition.
/// </remarks>
/// <seealso cref="AlertOrchestrator"/>
/// <seealso cref="ExcursionTracker"/>
public class AlertSweepService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<AlertSweepService> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="AlertSweepService"/>.
    /// </summary>
    /// <param name="serviceProvider">Root service provider for creating per-sweep DI scopes.</param>
    /// <param name="logger">The logger instance.</param>
    public AlertSweepService(
        IServiceProvider serviceProvider,
        ILogger<AlertSweepService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation("Alert Sweep Service started");

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        while (await timer.WaitForNextTickAsync(ct))
        {
            try
            {
                await CloseHysteresisWindowsAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error closing hysteresis windows");
            }

            try
            {
                await EvaluateSignalLossAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error evaluating signal loss");
            }

            try
            {
                await CheckSnoozedInstancesAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking snoozed instances");
            }

            try
            {
                await EvaluateAutoResolveAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error evaluating auto-resolve");
            }
        }

        _logger.LogInformation("Alert Sweep Service stopped");
    }

    /// <summary>
    /// Close excursions that are currently in hysteresis. Routes through the tracker
    /// (single owner of <c>AlertTrackerState.ActiveExcursionId</c>) and the shared
    /// resolution handler so resolution_reason="hysteresis" is stamped, pending deliveries
    /// expire, and <c>alert_resolved</c> broadcasts — same close pathway the orchestrator's
    /// per-reading hysteresis-expiry uses.
    /// </summary>
    private async Task CloseHysteresisWindowsAsync(CancellationToken ct)
    {
        using var lookupScope = _serviceProvider.CreateScope();
        var lookupRepository = lookupScope.ServiceProvider.GetRequiredService<IAlertRepository>();

        var excursions = await lookupRepository.GetExcursionsInHysteresisAsync(ct);
        if (excursions.Count == 0) return;

        var byTenant = excursions.GroupBy(e => e.TenantId);
        var closedCount = 0;

        foreach (var tenantGroup in byTenant)
        {
            var tenantId = tenantGroup.Key;
            var tenantContext = await lookupRepository.GetTenantAlertContextAsync(tenantId, ct);
            if (tenantContext is null || !tenantContext.IsActive) continue;

            using var tenantScope = _serviceProvider.CreateScope();
            var tenantAccessor = tenantScope.ServiceProvider.GetRequiredService<ITenantAccessor>();
            tenantAccessor.SetTenant(new TenantContext(
                tenantContext.TenantId,
                tenantContext.Slug ?? string.Empty,
                tenantContext.DisplayName ?? string.Empty,
                true));

            var tracker = tenantScope.ServiceProvider.GetRequiredService<IExcursionTracker>();
            var resolutionHandler = tenantScope.ServiceProvider.GetRequiredService<IExcursionResolutionHandler>();

            foreach (var excursion in tenantGroup)
            {
                try
                {
                    var transition = await tracker.ForceCloseAsync(
                        excursion.AlertRuleId, ExcursionCloseReason.Hysteresis, ct);
                    if (transition.Type == ExcursionTransitionType.ExcursionClosed)
                    {
                        await resolutionHandler.HandleClosedAsync(transition, tenantId, ct);
                        closedCount++;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "Error closing hysteresis excursion {ExcursionId} for rule {AlertRuleId}",
                        excursion.Id, excursion.AlertRuleId);
                }
            }
        }

        if (closedCount > 0)
        {
            _logger.LogInformation("Closed {Count} excursions in hysteresis", closedCount);
        }
    }

    /// <summary>
    /// Evaluate signal loss rules: for tenants whose last reading is older than the timeout,
    /// feed conditionMet=true into the excursion tracker.
    /// </summary>
    private async Task EvaluateSignalLossAsync(CancellationToken ct)
    {
        using var lookupScope = _serviceProvider.CreateScope();
        var repository = lookupScope.ServiceProvider.GetRequiredService<IAlertRepository>();

        var now = DateTime.UtcNow;

        var signalLossRules = await repository.GetEnabledSignalLossRulesAsync(ct);

        if (signalLossRules.Count == 0) return;

        // Group rules by tenant
        var rulesByTenant = signalLossRules.GroupBy(r => r.TenantId);

        foreach (var tenantGroup in rulesByTenant)
        {
            var tenantId = tenantGroup.Key;

            // Get tenant context
            var tenantContext = await repository.GetTenantAlertContextAsync(tenantId, ct);
            if (tenantContext is null || !tenantContext.IsActive) continue;

            foreach (var rule in tenantGroup)
            {
                try
                {
                    // Parse timeout from condition params
                    var conditionParams = JsonSerializer.Deserialize<SignalLossCondition>(rule.ConditionParams);
                    if (conditionParams is null) continue;

                    var timeout = TimeSpan.FromMinutes(conditionParams.TimeoutMinutes);
                    var lastReading = tenantContext.LastReadingAt ?? DateTime.MinValue;

                    if (now - lastReading < timeout) continue;

                    // Signal loss detected for this rule. Create a scoped service and evaluate.
                    using var tenantScope = _serviceProvider.CreateScope();
                    var tenantAccessor = tenantScope.ServiceProvider.GetRequiredService<ITenantAccessor>();
                    tenantAccessor.SetTenant(new TenantContext(tenantContext.TenantId, tenantContext.Slug ?? string.Empty, tenantContext.DisplayName ?? string.Empty, true));

                    var excursionTracker = tenantScope.ServiceProvider.GetRequiredService<IExcursionTracker>();
                    await excursionTracker.ProcessEvaluationAsync(rule.Id, true, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error evaluating signal loss for rule {RuleId}", rule.Id);
                }
            }
        }
    }

    /// <summary>
    /// Check snoozed instances whose snooze has expired. Per-instance snooze
    /// configuration may include either:
    /// <list type="bullet">
    ///   <item><c>conditions</c> — an array of <see cref="ConditionNode"/> evaluated as
    ///         <c>composite{and, conditions}</c> against an enriched context. If true, extend; else re-fire.</item>
    ///   <item><c>smartSnooze=true</c> with no conditions — fall back to glucose-trend
    ///         heuristic <see cref="IsTrendFavorable"/>.</item>
    /// </list>
    /// Otherwise clear the snooze so the alert re-fires and escalation resumes.
    /// </summary>
    private async Task CheckSnoozedInstancesAsync(CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IAlertRepository>();

        var now = DateTime.UtcNow;

        var instances = await repository.GetExpiredSnoozedInstancesAsync(now, ct);

        if (instances.Count == 0) return;

        _logger.LogDebug("Processing {Count} expired snoozed instances", instances.Count);

        // Parse per-instance snooze configuration once.
        var configsByInstance = instances.ToDictionary(i => i.InstanceId, i => ParseSnoozeConfig(i));
        var modifiedCount = 0;

        // Group by tenant so we batch-load trend rates and (if any conditions are configured)
        // build a single enriched context per tenant.
        var instancesByTenant = instances.GroupBy(i => i.TenantId);

        foreach (var tenantGroup in instancesByTenant)
        {
            var tenantId = tenantGroup.Key;
            var latestTrend = await repository.GetLatestTrendRateAsync(tenantId, ct);
            var anyConditions = tenantGroup.Any(i => configsByInstance[i.InstanceId].Conditions is { Count: > 0 });

            SensorContext? enrichedContext = null;
            if (anyConditions)
            {
                enrichedContext = await BuildSnoozeContextAsync(tenantId, latestTrend, tenantGroup, configsByInstance, ct);
            }

            foreach (var instance in tenantGroup)
            {
                var cfg = configsByInstance[instance.InstanceId];
                var canExtend = cfg.SmartSnooze && instance.SnoozeCount < cfg.MaxCount;

                bool extend;
                string? extendReason = null;
                if (!canExtend)
                {
                    extend = false;
                }
                else if (cfg.Conditions is { Count: > 0 } conditions && enrichedContext is not null)
                {
                    extend = await EvaluateSnoozeConditionsAsync(instance, conditions, enrichedContext, ct);
                    extendReason = extend ? "conditions" : "conditions-failed";
                }
                else
                {
                    extend = IsTrendFavorable(instance.ConditionType, instance.ConditionParams, latestTrend);
                    extendReason = extend ? "trend-favorable" : "trend-unfavorable";
                }

                if (extend)
                {
                    await repository.UpdateInstanceAsync(new UpdateAlertInstanceRequest(
                        instance.InstanceId,
                        SnoozedUntil: now.AddMinutes(cfg.ExtendMinutes),
                        SnoozeCount: instance.SnoozeCount + 1), ct);

                    _logger.LogDebug(
                        "Smart snooze extended instance {InstanceId} by {Minutes}m (count: {Count}, reason: {Reason})",
                        instance.InstanceId, cfg.ExtendMinutes, instance.SnoozeCount + 1, extendReason);
                }
                else
                {
                    await repository.UpdateInstanceAsync(new UpdateAlertInstanceRequest(
                        instance.InstanceId,
                        SnoozedUntil: DateTime.MinValue), ct);

                    _logger.LogDebug(
                        "Snooze cleared for instance {InstanceId} (smartSnooze={Smart}, count={Count}/{Max}, reason: {Reason})",
                        instance.InstanceId, cfg.SmartSnooze, instance.SnoozeCount, cfg.MaxCount, extendReason ?? "max-count");
                }

                modifiedCount++;
            }
        }

        if (modifiedCount > 0)
        {
            _logger.LogInformation("Processed {Count} expired snoozed instances", modifiedCount);
        }
    }

    private async Task<SensorContext?> BuildSnoozeContextAsync(
        Guid tenantId,
        double? latestTrend,
        IEnumerable<SnoozedInstanceSnapshot> tenantInstances,
        Dictionary<Guid, SnoozeConfig> configsByInstance,
        CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IAlertRepository>();
        var enricher = scope.ServiceProvider.GetRequiredService<ISensorContextEnricher>();
        var tenantAccessor = scope.ServiceProvider.GetRequiredService<ITenantAccessor>();

        var tenantContext = await repository.GetTenantAlertContextAsync(tenantId, ct);
        if (tenantContext is null) return null;

        tenantAccessor.SetTenant(new TenantContext(
            tenantContext.TenantId,
            tenantContext.Slug ?? string.Empty,
            tenantContext.DisplayName ?? string.Empty,
            true));

        // Wrap each instance's snooze conditions in a synthetic composite{and, conditions}
        // rule. RuleDataNeeds.Walk inspects the trees to decide what to enrich; rule identity
        // is not used during enrichment.
        var syntheticRules = new List<AlertRuleSnapshot>();
        foreach (var instance in tenantInstances)
        {
            var conditions = configsByInstance[instance.InstanceId].Conditions;
            if (conditions is null || conditions.Count == 0) continue;

            var composite = new CompositeCondition("and", conditions);
            var json = JsonSerializer.Serialize(composite, EvaluatorJson.Options);
            syntheticRules.Add(new AlertRuleSnapshot(
                instance.AlertRuleId,
                tenantId,
                "<snooze>",
                AlertConditionType.Composite,
                json,
                AlertRuleSeverity.Info,
                "{}",
                0,
                AutoResolveEnabled: false,
                AutoResolveParams: null));
        }

        var baseContext = new SensorContext
        {
            LatestValue = null,
            LatestTimestamp = tenantContext.LastReadingAt,
            TrendRate = (decimal?)latestTrend,
            LastReadingAt = tenantContext.LastReadingAt ?? DateTime.MinValue,
        };

        try
        {
            return await enricher.EnrichAsync(baseContext, syntheticRules, tenantId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to enrich sensor context for snooze evaluation on tenant {TenantId}", tenantId);
            return null;
        }
    }

    private async Task<bool> EvaluateSnoozeConditionsAsync(
        SnoozedInstanceSnapshot instance,
        List<ConditionNode> conditions,
        SensorContext enrichedContext,
        CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var registry = scope.ServiceProvider.GetRequiredService<ConditionEvaluatorRegistry>();

        var node = new ConditionNode(
            "composite",
            Composite: new CompositeCondition("and", conditions));

        var ruleContext = enrichedContext with
        {
            CurrentRuleId = instance.AlertRuleId,
            CurrentPath = AlertConditionTypeNames.SnoozePathRoot,
        };

        try
        {
            return await registry.EvaluateNodeAsync(node, ruleContext, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Snooze conditions evaluation failed for instance {InstanceId}", instance.InstanceId);
            return false;
        }
    }

    private SnoozeConfig ParseSnoozeConfig(SnoozedInstanceSnapshot instance)
    {
        var smartSnooze = false;
        var extendMinutes = 15;
        var maxCount = 3;
        List<ConditionNode>? conditions = null;

        try
        {
            using var doc = JsonDocument.Parse(instance.ClientConfiguration);
            if (doc.RootElement.TryGetProperty("snooze", out var snoozeEl))
            {
                if (snoozeEl.TryGetProperty("smartSnooze", out var smartEl))
                    smartSnooze = smartEl.GetBoolean();
                if (snoozeEl.TryGetProperty("smartSnoozeExtendMinutes", out var extendEl))
                    extendMinutes = extendEl.GetInt32();
                if (snoozeEl.TryGetProperty("maxCount", out var maxEl))
                    maxCount = maxEl.GetInt32();
                if (snoozeEl.TryGetProperty("conditions", out var conditionsEl)
                    && conditionsEl.ValueKind == JsonValueKind.Array)
                {
                    conditions = JsonSerializer.Deserialize<List<ConditionNode>>(
                        conditionsEl.GetRawText(), EvaluatorJson.Options);
                }
            }
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse client configuration for rule {RuleId}", instance.AlertRuleId);
        }

        return new SnoozeConfig(smartSnooze, extendMinutes, maxCount, conditions);
    }

    private readonly record struct SnoozeConfig(
        bool SmartSnooze,
        int ExtendMinutes,
        int MaxCount,
        List<ConditionNode>? Conditions);

    /// <summary>
    /// Periodic counterpart to the orchestrator's per-reading auto-resolve. Catches
    /// auto-resolve conditions that don't depend on the latest glucose reading
    /// (time-of-day, IOB, sensor age) — those would never fire from the per-reading
    /// path because no new reading triggers re-evaluation.
    /// </summary>
    /// <remarks>
    /// LatestValue is left null on the synthesised <see cref="SensorContext"/>: any
    /// LatestValue-dependent auto-resolve params (e.g. threshold-based) are still the
    /// orchestrator's job and will have been evaluated on the most recent reading.
    /// The enricher fills in IOB/COB/predictions/etc. as needed.
    /// </remarks>
    private async Task EvaluateAutoResolveAsync(CancellationToken ct)
    {
        using var lookupScope = _serviceProvider.CreateScope();
        var lookupRepository = lookupScope.ServiceProvider.GetRequiredService<IAlertRepository>();

        var openExcursions = await lookupRepository.GetAutoResolveExcursionsAsync(ct);
        if (openExcursions.Count == 0) return;

        var byTenant = openExcursions.GroupBy(x => x.TenantId);
        var now = DateTime.UtcNow;

        foreach (var tenantGroup in byTenant)
        {
            var tenantId = tenantGroup.Key;
            var tenantContext = await lookupRepository.GetTenantAlertContextAsync(tenantId, ct);
            if (tenantContext is null || !tenantContext.IsActive) continue;

            using var tenantScope = _serviceProvider.CreateScope();
            var tenantAccessor = tenantScope.ServiceProvider.GetRequiredService<ITenantAccessor>();
            tenantAccessor.SetTenant(new TenantContext(
                tenantContext.TenantId,
                tenantContext.Slug ?? string.Empty,
                tenantContext.DisplayName ?? string.Empty,
                true));

            var registry = tenantScope.ServiceProvider.GetRequiredService<ConditionEvaluatorRegistry>();
            var enricher = tenantScope.ServiceProvider.GetRequiredService<ISensorContextEnricher>();
            var tracker = tenantScope.ServiceProvider.GetRequiredService<IExcursionTracker>();
            var resolutionHandler = tenantScope.ServiceProvider.GetRequiredService<IExcursionResolutionHandler>();

            // Build a baseline context from tenant freshness; enricher fills the rest.
            var baseContext = new SensorContext
            {
                LatestValue = null,
                LatestTimestamp = tenantContext.LastReadingAt,
                TrendRate = null,
                LastReadingAt = tenantContext.LastReadingAt ?? DateTime.MinValue,
            };

            var rules = tenantGroup.Select(x => x.Rule).ToList();
            SensorContext enriched;
            try
            {
                enriched = await enricher.EnrichAsync(baseContext, rules, tenantId, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to enrich sensor context for auto-resolve sweep on tenant {TenantId}", tenantId);
                continue;
            }

            foreach (var entry in tenantGroup)
            {
                if (string.IsNullOrWhiteSpace(entry.Rule.AutoResolveParams)) continue;

                ConditionNode? node;
                try
                {
                    node = JsonSerializer.Deserialize<ConditionNode>(entry.Rule.AutoResolveParams, EvaluatorJson.Options);
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "Failed to parse AutoResolveParams for rule {AlertRuleId}", entry.Rule.Id);
                    continue;
                }
                if (node is null) continue;

                var ruleContext = enriched with
                {
                    CurrentRuleId = entry.Rule.Id,
                    CurrentPath = AlertConditionTypeNames.AutoResolvePathRoot,
                };

                bool shouldResolve;
                try
                {
                    shouldResolve = await registry.EvaluateNodeAsync(node, ruleContext, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Auto-resolve evaluation failed for rule {AlertRuleId}", entry.Rule.Id);
                    continue;
                }
                if (!shouldResolve) continue;

                var transition = await tracker.ForceCloseAsync(entry.Rule.Id, ExcursionCloseReason.AutoResolve, ct);
                if (transition.Type == ExcursionTransitionType.ExcursionClosed)
                {
                    await resolutionHandler.HandleClosedAsync(transition, tenantId, ct);
                }
            }
        }
    }

    /// <summary>
    /// Determines whether the current glucose trend is favorable for extending a snooze.
    /// For "below" (low alerts): favorable if BG is rising (trend rate > 0).
    /// For "above" (high alerts): favorable if BG is falling (trend rate &lt; 0).
    /// For other condition types: not favorable (don't extend).
    /// </summary>
    private static bool IsTrendFavorable(AlertConditionType conditionType, string conditionParams, double? trendRate)
    {
        if (trendRate is null) return false;
        if (conditionType != AlertConditionType.Threshold) return false;

        try
        {
            var condition = JsonSerializer.Deserialize<ThresholdCondition>(conditionParams);
            if (condition is null) return false;

            return condition.Direction.ToLowerInvariant() switch
            {
                "below" => trendRate > 0,  // Low alert: favorable if BG rising
                "above" => trendRate < 0,  // High alert: favorable if BG falling
                _ => false
            };
        }
        catch
        {
            return false;
        }
    }
}
