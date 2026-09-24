using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.API.Services.Alerts.Evaluators;
using Nocturne.API.Services.Audit;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.Alerts;

/// <summary>
/// <see cref="BackgroundService"/> that runs every 30 seconds to maintain alert lifecycle state:
/// </summary>
/// <remarks>
/// <list type="number">
///   <item>Close excursions whose hysteresis window has expired.</item>
///   <item>Evaluate signal-loss rules on the wall clock.</item>
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
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Audit endpoint recorded for the sweep's writes. The sweep runs with no request and no
    /// actor, and <see cref="AlertAcknowledgementService"/> — reached through the orchestrator's
    /// Info-severity auto-acknowledgement — stamps the scope's ambient context onto its own
    /// contexts, so without an explicit push those acks are recorded as user mutations with every
    /// actor field null. The other scopes' writers fall to the null-context system default today;
    /// they carry the push so attribution is explicit rather than an accident of which context
    /// path a writer uses.
    /// </summary>
    private const string AuditEndpoint = "service:alert-sweep";

    /// <summary>
    /// Initializes a new instance of <see cref="AlertSweepService"/>.
    /// </summary>
    /// <param name="serviceProvider">Root service provider for creating per-sweep DI scopes.</param>
    /// <param name="logger">The logger instance.</param>
    /// <param name="timeProvider">Clock for snooze expiry; the system clock when omitted.</param>
    public AlertSweepService(
        IServiceProvider serviceProvider,
        ILogger<AlertSweepService> logger,
        TimeProvider? timeProvider = null)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
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

            try
            {
                await EvaluateTrackerAgeRulesAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error evaluating tracker-age rules");
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
                true,
                IsDemo: false));

            using var systemScope = SystemAuditScope.PushForScope(
                tenantScope.ServiceProvider, AuditEndpoint);

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
    /// Evaluates enabled <c>signal_loss</c> rules through the orchestrator's full pipeline. The
    /// condition only holds between readings, so without this pass it would never fire; the
    /// excursion tracker dedupes a continuing outage, and once readings resume this pass (or
    /// the per-reading path) feeds false and the excursion closes through hysteresis.
    /// </summary>
    /// <remarks>
    /// <see cref="SensorContext.LastReadingAt"/> stays null for a tenant that has never had a
    /// reading, which <see cref="SignalLossEvaluator"/> treats as cold start rather than an outage.
    /// </remarks>
    internal Task EvaluateSignalLossAsync(CancellationToken ct) =>
        EvaluateRulesOnWallClockAsync(
            AlertConditionType.SignalLoss,
            tenant => new SensorContext
            {
                LatestValue = null,
                LatestTimestamp = tenant.LastReadingAt,
                TrendRate = null,
                LastReadingAt = tenant.LastReadingAt,
            },
            ct);

    /// <summary>
    /// Extends or clears each snoozed instance whose snooze has expired. Extension needs the rule's
    /// <see cref="SmartSnoozeConfig"/> to enable it with count to spare, and then either its
    /// <see cref="SmartSnoozeConfig.Conditions"/> to hold against a context built from the tenant's
    /// fresh canonical reading, or, when it configures none, <see cref="SmartSnoozeTrendGate"/> to
    /// pass. Anything else clears the snooze so the alert re-fires.
    /// </summary>
    internal async Task CheckSnoozedInstancesAsync(CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IAlertRepository>();

        var now = _timeProvider.GetUtcNow().UtcDateTime;

        var instances = await repository.GetExpiredSnoozedInstancesAsync(now, ct);

        if (instances.Count == 0) return;

        _logger.LogDebug("Processing {Count} expired snoozed instances", instances.Count);

        var configsByInstance = instances.ToDictionary(i => i.InstanceId, i => ParseSnoozeConfig(i));
        var modifiedCount = 0;

        foreach (var tenantGroup in instances.GroupBy(i => i.TenantId))
        {
            try
            {
                modifiedCount += await ProcessTenantSnoozesAsync(
                    repository, tenantGroup.Key, tenantGroup.ToList(), configsByInstance, now, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing expired snoozes for tenant {TenantId}", tenantGroup.Key);
            }
        }

        if (modifiedCount > 0)
        {
            _logger.LogInformation("Processed {Count} expired snoozed instances", modifiedCount);
        }
    }

    private async Task<int> ProcessTenantSnoozesAsync(
        IAlertRepository repository,
        Guid tenantId,
        IReadOnlyList<SnoozedInstanceSnapshot> tenantGroup,
        Dictionary<Guid, SmartSnoozeConfig> configsByInstance,
        DateTime now,
        CancellationToken ct)
    {
        var modifiedCount = 0;

        var tenantContext = await repository.GetTenantAlertContextAsync(tenantId, ct);

        using var tenantScope = _serviceProvider.CreateScope();
        if (tenantContext is not null)
        {
            tenantScope.ServiceProvider.GetRequiredService<ITenantAccessor>().SetTenant(new TenantContext(
                tenantContext.TenantId,
                tenantContext.Slug ?? string.Empty,
                tenantContext.DisplayName ?? string.Empty,
                true,
                IsDemo: false));
        }

        using var systemScope = SystemAuditScope.PushForScope(
            tenantScope.ServiceProvider, AuditEndpoint);

        var readings = tenantContext is null
            ? []
            : await LoadRecentReadingsAsync(tenantScope.ServiceProvider, tenantId, now, ct);
        var points = readings.Select(r => new GlucosePoint(r.Timestamp, r.Mgdl)).ToList();

        SensorContext? enrichedContext = null;
        if (tenantContext is not null
            && tenantGroup.Any(i => configsByInstance[i.InstanceId].Conditions is { Count: > 0 }))
        {
            enrichedContext = await BuildSnoozeContextAsync(
                tenantScope.ServiceProvider, tenantContext, readings, now, tenantGroup, configsByInstance, ct);
        }

        foreach (var instance in tenantGroup)
        {
            var cfg = configsByInstance[instance.InstanceId];

            bool extend;
            string reason;
            if (!cfg.SmartSnooze)
            {
                extend = false;
                reason = "smart-snooze-off";
            }
            else if (instance.SnoozeCount >= cfg.MaxCount)
            {
                extend = false;
                reason = "max-count";
            }
            else if (cfg.Conditions is { Count: > 0 } conditions)
            {
                extend = enrichedContext is not null
                         && await EvaluateSnoozeConditionsAsync(
                             tenantScope.ServiceProvider, instance, conditions, enrichedContext, ct);
                reason = extend ? "conditions" : "conditions-failed";
            }
            else
            {
                var outcome = SmartSnoozeTrendGate.Evaluate(
                    instance.ConditionType, instance.ConditionParams, points, now);
                extend = outcome == TrendGateOutcome.Favorable;
                reason = outcome switch
                {
                    TrendGateOutcome.Favorable => "trend-favorable",
                    TrendGateOutcome.NotFavorable => "trend-unfavorable",
                    TrendGateOutcome.InsufficientData => "trend-insufficient-data",
                    _ => "trend-not-applicable",
                };
            }

            if (extend)
            {
                await repository.UpdateInstanceAsync(new UpdateAlertInstanceRequest(
                    tenantId,
                    instance.InstanceId,
                    SnoozedUntil: now.AddMinutes(cfg.ExtendMinutes),
                    SnoozeCount: instance.SnoozeCount + 1), ct);

                _logger.LogInformation(
                    "Smart snooze extended instance {InstanceId} by {Minutes}m (count: {Count}/{Max}, reason: {Reason})",
                    instance.InstanceId, cfg.ExtendMinutes, instance.SnoozeCount + 1, cfg.MaxCount, reason);
            }
            else
            {
                await repository.UpdateInstanceAsync(new UpdateAlertInstanceRequest(
                    tenantId,
                    instance.InstanceId,
                    SnoozedUntil: DateTime.MinValue), ct);

                _logger.LogInformation(
                    "Snooze cleared for instance {InstanceId} (count: {Count}/{Max}, reason: {Reason})",
                    instance.InstanceId, instance.SnoozeCount, cfg.MaxCount, reason);
            }

            modifiedCount++;
        }

        return modifiedCount;
    }

    private async Task<IReadOnlyList<SensorGlucose>> LoadRecentReadingsAsync(
        IServiceProvider tenantServices, Guid tenantId, DateTime now, CancellationToken ct)
    {
        try
        {
            var canonical = tenantServices.GetRequiredService<ICanonicalGlucoseService>();
            return await canonical.GetRecentAsync(now - SmartSnoozeTrendGate.RequiredHistory, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to load recent glucose for snooze evaluation on tenant {TenantId}", tenantId);
            return [];
        }
    }

    /// <summary>
    /// Context for snooze conditions. Glucose facts come from the newest canonical reading only
    /// when it is within <see cref="SmartSnoozeTrendGate.MaxLatestAge"/>: a stale value would let a
    /// <c>threshold</c> or <c>trend</c> condition hold a snooze on data that no longer describes
    /// the patient, so it is left null and those conditions read false.
    /// </summary>
    private async Task<SensorContext?> BuildSnoozeContextAsync(
        IServiceProvider tenantServices,
        TenantAlertContext tenantContext,
        IReadOnlyList<SensorGlucose> readings,
        DateTime now,
        IEnumerable<SnoozedInstanceSnapshot> tenantInstances,
        Dictionary<Guid, SmartSnoozeConfig> configsByInstance,
        CancellationToken ct)
    {
        var enricher = tenantServices.GetRequiredService<ISensorContextEnricher>();

        // Each instance's conditions become a synthetic composite{and, conditions} rule so
        // RuleDataNeeds.Walk sees what to enrich; rule identity is not used during enrichment.
        var syntheticRules = new List<AlertRuleSnapshot>();
        var withConditions = tenantInstances
            .Select(i => (Instance: i, configsByInstance[i.InstanceId].Conditions))
            .Where(x => x.Conditions is { Count: > 0 });
        foreach (var (instance, conditions) in withConditions)
        {
            var composite = new CompositeCondition("and", conditions!.ToList());
            var json = JsonSerializer.Serialize(composite, EvaluatorJson.Options);
            syntheticRules.Add(new AlertRuleSnapshot(
                instance.AlertRuleId,
                tenantContext.TenantId,
                "<snooze>",
                AlertConditionType.Composite,
                json,
                AlertRuleSeverity.Info,
                "{}",
                0,
                AutoResolveEnabled: false,
                AutoResolveParams: null));
        }

        var newest = readings.Count > 0 ? readings.MaxBy(r => r.Timestamp) : null;
        var fresh = newest is not null && now - newest.Timestamp <= SmartSnoozeTrendGate.MaxLatestAge
            ? newest
            : null;

        var baseContext = new SensorContext
        {
            LatestValue = fresh is null ? null : (decimal)fresh.Mgdl,
            LatestTimestamp = fresh?.Timestamp ?? tenantContext.LastReadingAt,
            TrendRate = (decimal?)fresh?.TrendRate,
            LastReadingAt = tenantContext.LastReadingAt ?? newest?.Timestamp ?? DateTime.MinValue,
        };

        try
        {
            return await enricher.EnrichAsync(baseContext, syntheticRules, tenantContext.TenantId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to enrich sensor context for snooze evaluation on tenant {TenantId}", tenantContext.TenantId);
            return null;
        }
    }

    private async Task<bool> EvaluateSnoozeConditionsAsync(
        IServiceProvider tenantServices,
        SnoozedInstanceSnapshot instance,
        IReadOnlyList<ConditionNode> conditions,
        SensorContext enrichedContext,
        CancellationToken ct)
    {
        var engine = tenantServices.GetRequiredService<IAlertEvaluationEngine>();

        var node = new ConditionNode(
            "composite",
            Composite: new CompositeCondition("and", conditions.ToList()));

        try
        {
            // The engine seeds CurrentRuleId/CurrentPath; snooze conditions evaluate under
            // the reserved "snooze" path root so nested sustained timers don't collide
            // with rule-body or auto-resolve timers.
            return await engine.EvaluateNodeAsync(
                instance.AlertRuleId, node, enrichedContext, AlertConditionTypeNames.SnoozePathRoot, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Snooze conditions evaluation failed for instance {InstanceId}", instance.InstanceId);
            return false;
        }
    }

    private SmartSnoozeConfig ParseSnoozeConfig(SnoozedInstanceSnapshot instance)
    {
        var config = SmartSnoozeConfig.Parse(instance.ClientConfiguration);
        if (config.Malformed)
        {
            _logger.LogWarning(
                "Snooze configuration for rule {RuleId} is partly unreadable; unreadable fields use their defaults",
                instance.AlertRuleId);
        }
        return config;
    }

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
                true,
                IsDemo: false));

            using var systemScope = SystemAuditScope.PushForScope(
                tenantScope.ServiceProvider, AuditEndpoint);

            var engine = tenantScope.ServiceProvider.GetRequiredService<IAlertEvaluationEngine>();
            var enricher = tenantScope.ServiceProvider.GetRequiredService<ISensorContextEnricher>();
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

                // The engine evaluates the auto-resolve tree under the reserved
                // "auto_resolve" path root (parse failures and evaluation errors are
                // logged + skipped inside) and force-closes the active excursion when it
                // fires; this loop only applies the close side effects.
                ExcursionTransition transition;
                try
                {
                    transition = await engine.EvaluateAutoResolveAsync(entry.Rule, enriched, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Auto-resolve evaluation failed for rule {AlertRuleId}", entry.Rule.Id);
                    continue;
                }

                if (transition.Type == ExcursionTransitionType.ExcursionClosed)
                {
                    await resolutionHandler.HandleClosedAsync(transition, tenantId, ct);
                }
            }
        }
    }

    /// <summary>
    /// Periodically evaluates enabled <c>tracker_age</c> rules through the orchestrator's
    /// full pipeline. Tracker ages advance with the wall clock, not with readings — the
    /// per-reading path alone would delay (or, with a dead sensor, entirely drop) a
    /// "sensor expired" alert. The excursion state machine dedupes: once opened, further
    /// sweep passes are ExcursionContinues no-ops.
    /// </summary>
    /// <remarks>
    /// LatestValue stays null: tracker_age doesn't read it, and the most recent
    /// reading-dependent evaluation already ran on the per-reading path.
    /// </remarks>
    internal Task EvaluateTrackerAgeRulesAsync(CancellationToken ct) =>
        EvaluateRulesOnWallClockAsync(
            AlertConditionType.TrackerAge,
            tenant => new SensorContext
            {
                LatestValue = null,
                LatestTimestamp = tenant.LastReadingAt,
                TrendRate = null,
                LastReadingAt = tenant.LastReadingAt ?? DateTime.MinValue,
            },
            ct);

    private async Task EvaluateRulesOnWallClockAsync(
        AlertConditionType conditionType,
        Func<TenantAlertContext, SensorContext> buildContext,
        CancellationToken ct)
    {
        using var lookupScope = _serviceProvider.CreateScope();
        var lookupRepository = lookupScope.ServiceProvider.GetRequiredService<IAlertRepository>();

        var rules = await lookupRepository.GetEnabledRulesByConditionTypeAsync(conditionType, ct);
        if (rules.Count == 0) return;

        foreach (var tenantGroup in rules.GroupBy(r => r.TenantId))
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
                true,
                IsDemo: false));

            using var systemScope = SystemAuditScope.PushForScope(
                tenantScope.ServiceProvider, AuditEndpoint);

            var orchestrator = tenantScope.ServiceProvider.GetRequiredService<IAlertOrchestrator>();

            try
            {
                await orchestrator.EvaluateRulesAsync(tenantGroup.ToList(), buildContext(tenantContext), ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "{ConditionType} rule evaluation failed for tenant {TenantId}", conditionType, tenantId);
            }
        }
    }
}
