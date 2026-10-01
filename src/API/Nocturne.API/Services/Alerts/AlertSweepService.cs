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
    /// Per tenant whose newest canonical reading has no glucose value, what
    /// <see cref="LastUsableReadingAsync"/> found looking back from it. The look back reads up to
    /// <see cref="UsableReadingLookback"/> of readings, and each sweep pass would repeat it every
    /// 30 s for as long as the outage lasts; it is repeated only once a newer reading arrives.
    /// </summary>
    private readonly Dictionary<Guid, UsableLookback> _usableLookbacks = [];

    private sealed record UsableLookback(Guid NewestId, DateTime NewestAt, UsableReading Found);

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
    /// pass. Anything else clears the snooze and hands the instance to
    /// <see cref="IAlertSnoozeService.ResumeAsync"/>, which re-notifies if the alert still stands.
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
                tenantScope.Services, tenantContext, now, tenantGroup, configsByInstance, ct);
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

                if (tenantContext is not null)
                    await ResumeAsync(tenantScope.Services, instance.InstanceId, ct);
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

    /// <summary>
    /// Re-notifies a cleared snooze through <see cref="IAlertSnoozeService.ResumeAsync"/>. One
    /// instance's failure is logged, not rethrown, so the rest of the tenant's pass still runs.
    /// </summary>
    private async Task ResumeAsync(IServiceProvider tenantServices, Guid instanceId, CancellationToken ct)
    {
        try
        {
            await tenantServices.GetRequiredService<IAlertSnoozeService>().ResumeAsync(instanceId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to resume alert instance {InstanceId} after its snooze lapsed", instanceId);
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
    /// Context for snooze conditions, measured from <see cref="LastUsableReadingAsync"/>. Its
    /// glucose facts are taken only when that reading is within
    /// <see cref="SmartSnoozeTrendGate.MaxLatestAge"/>: a stale value would let a
    /// <c>threshold</c> or <c>trend</c> condition hold a snooze on data that no longer describes
    /// the patient, so it is left null and those conditions read false.
    /// </summary>
    private async Task<SensorContext?> BuildSnoozeContextAsync(
        IServiceProvider tenantServices,
        TenantAlertContext tenantContext,
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

        try
        {
            var last = await LastUsableReadingAsync(
                tenantServices.GetRequiredService<ICanonicalGlucoseService>(), tenantContext, ct);
            var fresh = last.Reading is { } usable && now - usable.Timestamp <= SmartSnoozeTrendGate.MaxLatestAge
                ? usable
                : null;
            var baseContext = new SensorContext
            {
                LatestValue = fresh is null ? null : (decimal)fresh.Mgdl,
                LatestTimestamp = last.At,
                TrendRate = (decimal?)fresh?.TrendRate,
                LastReadingAt = last.At,
            };
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
    /// Reading times come from <see cref="LastUsableReadingAsync"/>; the enricher fills in
    /// IOB/COB/predictions/etc. as needed.
    /// </remarks>
    internal async Task EvaluateAutoResolveAsync(CancellationToken ct)
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

            var rules = tenantGroup.Select(x => x.Rule).ToList();
            SensorContext enriched;
            try
            {
                var last = await LastUsableReadingAsync(
                    tenantScope.Services.GetRequiredService<ICanonicalGlucoseService>(), tenantContext, ct);
                var baseContext = new SensorContext
                {
                    LatestValue = null,
                    LatestTimestamp = last.At,
                    TrendRate = null,
                    LastReadingAt = last.At,
                };
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
    /// The context is the one the per-reading path builds for <see cref="LastUsableReadingAsync"/>
    /// (<see cref="CanonicalAlertEvaluator.ContextFor"/>), so reading-driven leaves in the same
    /// tree judge exactly what its last pass judged. A context without the glucose facts would
    /// read them false and close an excursion they hold open.
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
                var last = await LastUsableReadingAsync(
                    tenantScope.Services.GetRequiredService<ICanonicalGlucoseService>(), tenantContext, ct);
                var context = last.Reading is { } usable
                    ? CanonicalAlertEvaluator.ContextFor(usable)
                    : new SensorContext
                    {
                        LatestValue = null,
                        LatestTimestamp = last.At,
                        TrendRate = null,
                        LastReadingAt = last.At,
                    };

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
    /// The reading every swept context measures <see cref="SensorContext.LastReadingAt"/> from:
    /// the newest canonical reading with a glucose value, the only kind the per-reading path
    /// evaluates, so <c>signal_loss</c> counts sensor-error and warm-up readings as no signal.
    /// </summary>
    /// <remarks>
    /// It is looked for at most <see cref="UsableReadingLookback"/> before the newest canonical
    /// reading. With none there, <see cref="UsableReading.At"/> is the oldest reading looked at:
    /// the outage is at least that old. With no canonical reading at all it is the tenant's
    /// newest reading of any source, null for a tenant that has never had one, which
    /// <c>signal_loss</c> and <c>staleness</c> treat as cold start.
    /// <para>
    /// The look back is kept per tenant (<see cref="_usableLookbacks"/>) until the newest
    /// canonical reading changes, so a usable reading backfilled behind an unchanged newest one
    /// is seen once the next reading arrives.
    /// </para>
    /// </remarks>
    internal async Task<UsableReading> LastUsableReadingAsync(
        ICanonicalGlucoseService canonical, TenantAlertContext tenant, CancellationToken ct)
    {
        var latest = await canonical.GetLatestAsync(ct);
        if (latest is null || latest.Mgdl > 0)
        {
            _usableLookbacks.Remove(tenant.TenantId);
            return latest is null
                ? new UsableReading(null, tenant.LastReadingAt)
                : new UsableReading(latest, latest.Timestamp);
        }

        if (_usableLookbacks.TryGetValue(tenant.TenantId, out var kept)
            && kept.NewestId == latest.Id && kept.NewestAt == latest.Timestamp)
        {
            return kept.Found;
        }

        var recent = await canonical.GetRecentAsync(latest.Timestamp - UsableReadingLookback, ct);
        var found = recent.Where(r => r.Mgdl > 0).MaxBy(r => r.Timestamp) is { } usable
            ? new UsableReading(usable, usable.Timestamp)
            : new UsableReading(null, recent.Count > 0 ? recent.Min(r => r.Timestamp) : latest.Timestamp);
        _usableLookbacks[tenant.TenantId] = new UsableLookback(latest.Id, latest.Timestamp, found);
        return found;
    }

    /// <param name="Reading">The last usable reading, or null when none was found.</param>
    /// <param name="At">What <see cref="SensorContext.LastReadingAt"/> is.</param>
    internal sealed record UsableReading(SensorGlucose? Reading, DateTime? At);

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
