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
///   <item>Close excursions whose hysteresis window has elapsed.</item>
///   <item>Check snoozed instances for smart-snooze extension or re-fire.</item>
///   <item>Evaluate rules with a wall-clock-sensitive condition (<see cref="WallClockConditions"/>).</item>
///   <item>Run periodic auto-resolve for excursions whose conditions don't depend on the latest reading.</item>
/// </list>
/// The wall-clock pass evaluates every such rule of every tenant, so it runs after the two
/// cheap, time-critical passes rather than delaying them.
/// Each sweep creates a child DI scope so that scoped services (DbContext, tenant repositories)
/// are properly isolated and disposed. Individual tenant failures are caught and logged without
/// aborting the rest of the sweep.
/// </remarks>
/// <seealso cref="AlertOrchestrator"/>
/// <seealso cref="ExcursionTracker"/>
public class AlertSweepService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<AlertSweepService> _logger;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Audit endpoint recorded for the sweep's writes, which have no request and no actor.
    /// <see cref="AlertAcknowledgementService"/>, reached through the orchestrator's Info-severity
    /// auto-acknowledgement, stamps the scope's ambient context onto its own contexts. Without
    /// the push those acks would be recorded as user mutations with every actor field null.
    /// </summary>
    private const string AuditEndpoint = "service:alert-sweep";

    /// <summary>
    /// <see cref="WallClockConditions.ReferencesWallClock"/> per enabled rule, kept while the
    /// rule's condition is unchanged, so a tree is walked, and an unwalkable one reported, once
    /// per version rather than every tick.
    /// </summary>
    private readonly Dictionary<Guid, WallClockVerdict> _wallClock = [];

    private sealed record WallClockVerdict(AlertConditionType Type, string Params, bool References);

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
                await CheckSnoozedInstancesAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking snoozed instances");
            }

            try
            {
                await EvaluateWallClockRulesAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error evaluating wall-clock rules");
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
    /// Closes excursions whose hysteresis window has elapsed, so a window still expires when no
    /// evaluation arrives. Routes through the tracker (single owner of
    /// <c>AlertTrackerState.ActiveExcursionId</c>, and of the window check) and the shared
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

            using var tenantScope = BeginTenantScope(tenantContext);
            var tracker = tenantScope.Services.GetRequiredService<IExcursionTracker>();
            var resolutionHandler = tenantScope.Services.GetRequiredService<IExcursionResolutionHandler>();

            foreach (var excursion in tenantGroup)
            {
                try
                {
                    var transition = await tracker.CloseElapsedHysteresisAsync(excursion.AlertRuleId, ct);
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

        using var tenantScope = BeginTenantScope(tenantContext);

        var readings = tenantContext is null
            ? []
            : await LoadRecentReadingsAsync(tenantScope.Services, tenantId, now, ct);
        var points = readings.Select(r => new GlucosePoint(r.Timestamp, r.Mgdl)).ToList();

        SensorContext? enrichedContext = null;
        if (tenantContext is not null
            && tenantGroup.Any(i => configsByInstance[i.InstanceId].Conditions is { Count: > 0 }))
        {
            enrichedContext = await BuildSnoozeContextAsync(
                tenantScope.Services, tenantContext, readings, now, tenantGroup, configsByInstance, ct);
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
                             tenantScope.Services, instance, conditions, enrichedContext, ct);
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

    /// <summary>
    /// A child DI scope running as <paramref name="tenant"/>, when there is one, with
    /// <see cref="AuditEndpoint"/> pushed for its writes.
    /// </summary>
    private TenantScope BeginTenantScope(TenantAlertContext? tenant)
    {
        var scope = _serviceProvider.CreateScope();
        try
        {
            if (tenant is not null)
            {
                scope.ServiceProvider.GetRequiredService<ITenantAccessor>().SetTenant(new TenantContext(
                    tenant.TenantId,
                    tenant.Slug ?? string.Empty,
                    tenant.DisplayName ?? string.Empty,
                    true,
                    IsDemo: false));
            }
            return new TenantScope(scope, SystemAuditScope.PushForScope(scope.ServiceProvider, AuditEndpoint));
        }
        catch
        {
            scope.Dispose();
            throw;
        }
    }

    private sealed class TenantScope(IServiceScope scope, IDisposable audit) : IDisposable
    {
        public IServiceProvider Services => scope.ServiceProvider;

        public void Dispose()
        {
            audit.Dispose();
            scope.Dispose();
        }
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

        foreach (var tenantGroup in byTenant)
        {
            var tenantId = tenantGroup.Key;
            var tenantContext = await lookupRepository.GetTenantAlertContextAsync(tenantId, ct);
            if (tenantContext is null || !tenantContext.IsActive) continue;

            using var tenantScope = BeginTenantScope(tenantContext);
            var engine = tenantScope.Services.GetRequiredService<IAlertEvaluationEngine>();
            var enricher = tenantScope.Services.GetRequiredService<ISensorContextEnricher>();
            var resolutionHandler = tenantScope.Services.GetRequiredService<IExcursionResolutionHandler>();

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
    /// Evaluates every enabled rule whose tree contains a <see cref="WallClockConditions"/> kind
    /// through the orchestrator's full pipeline. Those conditions change truth with the clock
    /// alone, so between readings, or with no readings at all, only this pass moves them. The
    /// excursion tracker dedupes: a condition that stays true is <c>ExcursionContinues</c>.
    /// <c>alert_state</c> references resolve against every enabled rule of the tenant, since an
    /// escalation's parent is usually reading-driven and so not in the swept set.
    /// </summary>
    /// <remarks>
    /// The context is the one the per-reading path builds for the tenant's newest usable
    /// canonical reading, one with a glucose value (<see cref="CanonicalAlertEvaluator.ContextFor"/>).
    /// The per-reading path evaluates no other, so reading-driven leaves in the same tree judge
    /// exactly what its last pass judged, and <c>signal_loss</c> counts sensor-error readings as
    /// no signal. A context without the glucose facts would read them false and close an
    /// excursion they hold open. With no canonical reading, <see cref="SensorContext.LastReadingAt"/>
    /// falls back to the tenant's newest reading of any source. It stays null for a tenant that
    /// has never had one, which <c>signal_loss</c> and <c>staleness</c> treat as cold start.
    /// </remarks>
    internal async Task EvaluateWallClockRulesAsync(CancellationToken ct)
    {
        using var lookupScope = _serviceProvider.CreateScope();
        var lookupRepository = lookupScope.ServiceProvider.GetRequiredService<IAlertRepository>();

        var enabled = await lookupRepository.GetAllEnabledRulesAsync(ct);
        var enabledIds = enabled.Select(r => r.Id).ToHashSet();
        foreach (var gone in _wallClock.Keys.Where(id => !enabledIds.Contains(id)).ToList())
            _wallClock.Remove(gone);

        foreach (var tenantRules in enabled.GroupBy(r => r.TenantId))
        {
            var wallClockRules = tenantRules.Where(ReferencesWallClock).ToList();
            if (wallClockRules.Count == 0) continue;

            var tenantId = tenantRules.Key;
            var tenantContext = await lookupRepository.GetTenantAlertContextAsync(tenantId, ct);
            if (tenantContext is null || !tenantContext.IsActive) continue;

            using var tenantScope = BeginTenantScope(tenantContext);

            try
            {
                var canonical = tenantScope.Services.GetRequiredService<ICanonicalGlucoseService>();
                var latest = await canonical.GetLatestAsync(ct);
                var context = latest is null
                    ? new SensorContext
                    {
                        LatestValue = null,
                        LatestTimestamp = tenantContext.LastReadingAt,
                        TrendRate = null,
                        LastReadingAt = tenantContext.LastReadingAt,
                    }
                    : await UsableContextAsync(canonical, latest, ct);

                var orchestrator = tenantScope.Services.GetRequiredService<IAlertOrchestrator>();
                await orchestrator.EvaluateRulesAsync(
                    wallClockRules, tenantRules.Select(r => r.Id).ToHashSet(), context, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Wall-clock rule evaluation failed for tenant {TenantId}", tenantId);
            }
        }
    }

    /// <summary>How far before an unusable newest reading the sweep looks for a usable one.</summary>
    internal static readonly TimeSpan UsableReadingLookback = TimeSpan.FromHours(24);

    /// <summary>
    /// The context for the newest canonical reading with a glucose value at or after
    /// <see cref="UsableReadingLookback"/> before <paramref name="latest"/>. When there is none,
    /// the glucose facts are absent and <see cref="SensorContext.LastReadingAt"/> is the oldest
    /// reading looked at: the outage is at least that old.
    /// </summary>
    private static async Task<SensorContext> UsableContextAsync(
        ICanonicalGlucoseService canonical, SensorGlucose latest, CancellationToken ct)
    {
        if (latest.Mgdl > 0)
            return CanonicalAlertEvaluator.ContextFor(latest);

        var recent = await canonical.GetRecentAsync(latest.Timestamp - UsableReadingLookback, ct);
        if (recent.Where(r => r.Mgdl > 0).MaxBy(r => r.Timestamp) is { } usable)
            return CanonicalAlertEvaluator.ContextFor(usable);

        var since = recent.Count > 0 ? recent.Min(r => r.Timestamp) : latest.Timestamp;
        return new SensorContext
        {
            LatestValue = null,
            LatestTimestamp = since,
            TrendRate = null,
            LastReadingAt = since,
        };
    }

    private bool ReferencesWallClock(AlertRuleSnapshot rule)
    {
        if (_wallClock.TryGetValue(rule.Id, out var cached)
            && cached.Type == rule.ConditionType
            && string.Equals(cached.Params, rule.ConditionParams, StringComparison.Ordinal))
        {
            return cached.References;
        }

        bool references;
        try
        {
            references = WallClockConditions.ReferencesWallClock(rule);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Condition tree of rule {AlertRuleId} could not be walked; it evaluates per reading only",
                rule.Id);
            references = false;
        }

        _wallClock[rule.Id] = new WallClockVerdict(rule.ConditionType, rule.ConditionParams, references);
        return references;
    }
}
