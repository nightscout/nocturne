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
///   <item>Advance escalations whose configured delay has elapsed.</item>
///   <item>Close excursions whose hysteresis window has expired.</item>
///   <item>Evaluate signal-loss rules for tenants with stale CGM readings.</item>
///   <item>Check snoozed instances for smart-snooze extension or re-fire.</item>
/// </list>
/// Each sweep creates a child DI scope so that scoped services (DbContext, tenant repositories)
/// are properly isolated and disposed. Individual tenant failures are caught and logged without
/// aborting the rest of the sweep.
/// </remarks>
/// <seealso cref="AlertOrchestrator"/>
/// <seealso cref="IEscalationAdvancer"/>
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
                await AdvanceEscalationsAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error advancing escalations");
            }

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
    /// Query instances with status "escalating" whose NextEscalationAt has passed.
    /// Advance each to the next step.
    /// </summary>
    private async Task AdvanceEscalationsAsync(CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IAlertRepository>();
        var advancer = scope.ServiceProvider.GetRequiredService<IEscalationAdvancer>();

        var now = DateTime.UtcNow;

        var instances = await repository.GetEscalatingInstancesDueAsync(now, ct);

        if (instances.Count == 0) return;

        _logger.LogDebug("Advancing {Count} escalations", instances.Count);

        foreach (var instance in instances)
        {
            try
            {
                await advancer.AdvanceAsync(instance, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error advancing escalation for instance {InstanceId}", instance.Id);
            }
        }
    }

    /// <summary>
    /// Close excursions that are currently in hysteresis. Hysteresis is a single tick:
    /// an excursion marked on one sweep is closed on the next.
    /// </summary>
    private async Task CloseHysteresisWindowsAsync(CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IAlertRepository>();

        var now = DateTime.UtcNow;

        var excursions = await repository.GetExcursionsInHysteresisAsync(ct);

        if (excursions.Count == 0) return;

        foreach (var excursion in excursions)
        {
            await repository.CloseHysteresisExcursionAsync(excursion.Id, excursion.AlertRuleId, now, ct);
        }

        _logger.LogInformation("Closed {Count} excursions in hysteresis", excursions.Count);
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
    /// Check snoozed instances whose snooze has expired.
    /// If smart snooze is enabled and the glucose trend is favorable, extend the snooze.
    /// Otherwise, clear the snooze so the alert re-fires and escalation resumes.
    /// </summary>
    private async Task CheckSnoozedInstancesAsync(CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IAlertRepository>();

        var now = DateTime.UtcNow;

        var instances = await repository.GetExpiredSnoozedInstancesAsync(now, ct);

        if (instances.Count == 0) return;

        _logger.LogDebug("Processing {Count} expired snoozed instances", instances.Count);

        // Gather distinct tenant IDs so we can batch-load latest trend rates
        var tenantIds = instances.Select(i => i.TenantId).Distinct().ToList();
        var latestTrendByTenant = new Dictionary<Guid, double?>();

        foreach (var tenantId in tenantIds)
        {
            latestTrendByTenant[tenantId] = await repository.GetLatestTrendRateAsync(tenantId, ct);
        }

        var modifiedCount = 0;

        foreach (var instance in instances)
        {
            // Parse client configuration for snooze settings
            var smartSnooze = false;
            var smartSnoozeExtendMinutes = 15;
            var maxCount = 3;

            try
            {
                using var doc = JsonDocument.Parse(instance.ClientConfiguration);
                if (doc.RootElement.TryGetProperty("snooze", out var snoozeEl))
                {
                    if (snoozeEl.TryGetProperty("smartSnooze", out var smartEl))
                        smartSnooze = smartEl.GetBoolean();
                    if (snoozeEl.TryGetProperty("smartSnoozeExtendMinutes", out var extendEl))
                        smartSnoozeExtendMinutes = extendEl.GetInt32();
                    if (snoozeEl.TryGetProperty("maxCount", out var maxEl))
                        maxCount = maxEl.GetInt32();
                }
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Failed to parse client configuration for rule {RuleId}", instance.AlertRuleId);
            }

            if (smartSnooze && instance.SnoozeCount < maxCount)
            {
                // Determine if glucose trend is favorable
                var favorable = IsTrendFavorable(
                    instance.ConditionType, instance.ConditionParams,
                    latestTrendByTenant.GetValueOrDefault(instance.TenantId));

                if (favorable)
                {
                    await repository.UpdateInstanceAsync(new UpdateAlertInstanceRequest(
                        instance.InstanceId,
                        SnoozedUntil: now.AddMinutes(smartSnoozeExtendMinutes),
                        SnoozeCount: instance.SnoozeCount + 1), ct);

                    _logger.LogDebug(
                        "Smart snooze extended instance {InstanceId} by {Minutes}m (count: {Count})",
                        instance.InstanceId, smartSnoozeExtendMinutes, instance.SnoozeCount + 1);
                }
                else
                {
                    await repository.UpdateInstanceAsync(new UpdateAlertInstanceRequest(
                        instance.InstanceId,
                        SnoozedUntil: DateTime.MinValue), ct);

                    _logger.LogDebug(
                        "Smart snooze cleared for instance {InstanceId} — trend not favorable",
                        instance.InstanceId);
                }
            }
            else
            {
                // Smart snooze disabled or max count reached — clear snooze
                await repository.UpdateInstanceAsync(new UpdateAlertInstanceRequest(
                    instance.InstanceId,
                    SnoozedUntil: DateTime.MinValue), ct);

                _logger.LogDebug(
                    "Snooze expired for instance {InstanceId} (smartSnooze={Smart}, count={Count}/{Max})",
                    instance.InstanceId, smartSnooze, instance.SnoozeCount, maxCount);
            }

            modifiedCount++;
        }

        if (modifiedCount > 0)
        {
            _logger.LogInformation("Processed {Count} expired snoozed instances", modifiedCount);
        }
    }

    private static readonly JsonSerializerOptions AutoResolveJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

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
                    node = JsonSerializer.Deserialize<ConditionNode>(entry.Rule.AutoResolveParams, AutoResolveJsonOptions);
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
                    CurrentPath = "auto_resolve",
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
