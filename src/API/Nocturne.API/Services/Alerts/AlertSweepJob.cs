using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Nocturne.API.Services.Alerts.Engines;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Jobs;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.Alerts;

/// <summary>
/// Every <see cref="Interval"/>, keeps an active tenant's alert lifecycle moving where no reading
/// arrives to move it:
/// </summary>
/// <remarks>
/// <list type="number">
///   <item>Close excursions whose hysteresis window has elapsed.</item>
///   <item>Check snoozed instances for smart-snooze extension or re-fire.</item>
///   <item>Evaluate rules with a wall-clock-sensitive condition (<see cref="RuleTrees.ReferencesWallClock"/>).</item>
///   <item>Run periodic auto-resolve for excursions whose conditions don't depend on the latest reading.</item>
/// </list>
/// The wall-clock pass evaluates every such rule of the tenant, so it runs after the two cheap,
/// time-critical passes rather than delaying them. A pass that throws does not stop the passes
/// after it; the run then fails with the first error, and is retried after <see cref="SweepMaxBackoff"/>.
/// An inactive tenant's row is parked for <see cref="IScheduledJob.InactiveTenantRecheck"/>.
/// <para>
/// The job keeps two facts per tenant in memory, <see cref="_wallClock"/> and
/// <see cref="_usableLookbacks"/>, and neither is a write or a notification. A restart, or a run in
/// another replica, reads each again from the database: the same wall-clock verdict, and a look back
/// that may find a usable reading backfilled behind an unchanged newest one sooner than a kept look
/// back would.
/// </para>
/// </remarks>
/// <seealso cref="AlertOrchestrator"/>
/// <seealso cref="ExcursionTracker"/>
public sealed class AlertSweepJob(ILogger<AlertSweepJob> logger) : IScheduledJob
{
    internal const string JobName = "alert-sweep";

    internal static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// A failing run is retried at the sweep's own cadence, since any wait longer than that holds back
    /// the tenant's snooze re-fires, hysteresis closes and signal-loss alerts.
    /// </summary>
    internal static readonly TimeSpan SweepMaxBackoff = Interval;

    /// <summary>
    /// Per tenant, <see cref="RuleTrees.ReferencesWallClock"/> per enabled rule, kept while the rule's
    /// body and auto-resolve configuration are unchanged, so a rule is asked about, and a failure
    /// reported, once per version rather than every run.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, IReadOnlyDictionary<Guid, WallClockVerdict>> _wallClock = new();

    private sealed record WallClockVerdict(
        AlertConditionType Type, string Params, bool AutoResolveEnabled, string? AutoResolveParams, bool References);

    /// <summary>
    /// Per tenant whose newest canonical reading has no glucose value, what
    /// <see cref="LastUsableReadingAsync"/> found looking back from it. The look back reads up to
    /// <see cref="UsableReadingLookback"/> of readings, and each run would repeat it every
    /// <see cref="Interval"/> for as long as the outage lasts; it is repeated only once a newer
    /// reading arrives.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, UsableLookback> _usableLookbacks = new();

    private sealed record UsableLookback(Guid NewestId, DateTime NewestAt, UsableReading Found);

    public string Name => JobName;

    public JobEnrolment Enrolment => JobEnrolment.PerTenant;

    public TimeSpan MaxBackoff => SweepMaxBackoff;

    public TimeSpan Spread => Interval;

    public async Task<JobOutcome> RunAsync(JobRun run, CancellationToken ct)
    {
        var tenantId = run.Tenant!.TenantId;
        if (!run.Tenant.IsActive)
        {
            _wallClock.TryRemove(tenantId, out _);
            _usableLookbacks.TryRemove(tenantId, out _);
            return JobOutcome.Resume(null, run.StartedAt + IScheduledJob.InactiveTenantRecheck);
        }

        var sweep = new Sweep(run.Services, run.Services.GetRequiredService<IAlertRepository>(), tenantId, run.StartedAt.UtcDateTime);
        Exception? failed = null;
        foreach (var (pass, what) in new (Func<Sweep, CancellationToken, Task>, string)[]
                 {
                     (CloseHysteresisWindowsAsync, "closing hysteresis windows"),
                     (CheckSnoozedInstancesAsync, "checking snoozed instances"),
                     (EvaluateWallClockRulesAsync, "evaluating wall-clock rules"),
                     (EvaluateAutoResolveAsync, "evaluating auto-resolve"),
                 })
        {
            try
            {
                await pass(sweep, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogError(ex, "Error {Pass} for tenant {TenantId}", what, tenantId);
                failed ??= ex;
            }
        }

        if (failed is not null)
            ExceptionDispatchInfo.Throw(failed);

        return JobOutcome.Resume(null, run.StartedAt + Interval);
    }

    /// <summary>One run's tenant, its scope and its clock.</summary>
    private sealed class Sweep(IServiceProvider services, IAlertRepository repository, Guid tenantId, DateTime now)
    {
        private TenantAlertContext? _tenant;

        public IServiceProvider Services => services;

        public IAlertRepository Repository => repository;

        public Guid TenantId => tenantId;

        public DateTime Now => now;

        /// <summary>The tenant's alert context, read once and only by a run that needs it.</summary>
        public async Task<TenantAlertContext> TenantAsync(CancellationToken ct) =>
            _tenant ??= await repository.GetTenantAlertContextAsync(tenantId, ct)
                        ?? throw new InvalidOperationException($"Tenant {tenantId} has no alert context.");
    }

    /// <summary>
    /// Closes excursions whose hysteresis window has elapsed, so a window still expires when no
    /// evaluation arrives. Routes through the tracker (single owner of
    /// <c>AlertTrackerState.ActiveExcursionId</c>, and of the window check) and the shared
    /// resolution handler so resolution_reason="hysteresis" is stamped, pending deliveries
    /// expire, and <c>alert_resolved</c> broadcasts — same close pathway the orchestrator's
    /// per-reading hysteresis-expiry uses.
    /// </summary>
    private async Task CloseHysteresisWindowsAsync(Sweep sweep, CancellationToken ct)
    {
        var excursions = await sweep.Repository.GetExcursionsInHysteresisAsync(ct);
        if (excursions.Count == 0) return;

        var tracker = sweep.Services.GetRequiredService<IExcursionTracker>();
        var resolutionHandler = sweep.Services.GetRequiredService<IExcursionResolutionHandler>();
        var closedCount = 0;

        foreach (var excursion in excursions)
        {
            try
            {
                var transition = await tracker.CloseElapsedHysteresisAsync(excursion.AlertRuleId, ct);
                if (transition.Type == ExcursionTransitionType.ExcursionClosed)
                {
                    await resolutionHandler.HandleClosedAsync(transition, sweep.TenantId, ct);
                    closedCount++;
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "Error closing hysteresis excursion {ExcursionId} for rule {AlertRuleId}",
                    excursion.Id, excursion.AlertRuleId);
            }
        }

        if (closedCount > 0)
        {
            logger.LogInformation("Closed {Count} excursions in hysteresis", closedCount);
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
    private async Task CheckSnoozedInstancesAsync(Sweep sweep, CancellationToken ct)
    {
        var now = sweep.Now;
        var instances = await sweep.Repository.GetExpiredSnoozedInstancesAsync(now, ct);
        if (instances.Count == 0) return;

        logger.LogDebug("Processing {Count} expired snoozed instances", instances.Count);

        var configsByInstance = instances.ToDictionary(i => i.InstanceId, ParseSnoozeConfig);
        var tenantId = sweep.TenantId;

        var readings = await LoadRecentReadingsAsync(sweep.Services, tenantId, now, ct);
        var points = readings.Select(r => new GlucosePoint(r.Timestamp, r.Mgdl)).ToList();

        SensorContext? enrichedContext = null;
        if (instances.Any(i => configsByInstance[i.InstanceId].Conditions is not null))
        {
            enrichedContext = await BuildSnoozeContextAsync(sweep, instances, configsByInstance, ct);
        }

        foreach (var instance in instances)
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
            else if (cfg.ConditionsUnreadable)
            {
                extend = false;
                reason = "conditions-unreadable";
            }
            else if (cfg.Conditions is { } conditions)
            {
                extend = enrichedContext is not null
                         && await EvaluateSnoozeConditionsAsync(
                             sweep.Services, instance, conditions, enrichedContext, ct);
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
                await sweep.Repository.UpdateInstanceAsync(new UpdateAlertInstanceRequest(
                    tenantId,
                    instance.InstanceId,
                    SnoozedUntil: now.AddMinutes(cfg.ExtendMinutes),
                    SnoozeCount: instance.SnoozeCount + 1), ct);

                logger.LogInformation(
                    "Smart snooze extended instance {InstanceId} by {Minutes}m (count: {Count}/{Max}, reason: {Reason})",
                    instance.InstanceId, cfg.ExtendMinutes, instance.SnoozeCount + 1, cfg.MaxCount, reason);
            }
            else
            {
                await sweep.Repository.UpdateInstanceAsync(new UpdateAlertInstanceRequest(
                    tenantId,
                    instance.InstanceId,
                    SnoozedUntil: DateTime.MinValue), ct);

                logger.LogInformation(
                    "Snooze cleared for instance {InstanceId} (count: {Count}/{Max}, reason: {Reason})",
                    instance.InstanceId, instance.SnoozeCount, cfg.MaxCount, reason);

                await ResumeAsync(sweep.Services, instance.InstanceId, ct);
            }
        }

        logger.LogInformation("Processed {Count} expired snoozed instances", instances.Count);
    }

    /// <summary>
    /// Re-notifies a cleared snooze through <see cref="IAlertSnoozeService.ResumeAsync"/>. One
    /// instance's failure is logged, not rethrown, so the rest of the tenant's pass still runs.
    /// </summary>
    private async Task ResumeAsync(IServiceProvider services, Guid instanceId, CancellationToken ct)
    {
        try
        {
            await services.GetRequiredService<IAlertSnoozeService>().ResumeAsync(instanceId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Failed to resume alert instance {InstanceId} after its snooze lapsed", instanceId);
        }
    }

    private async Task<IReadOnlyList<SensorGlucose>> LoadRecentReadingsAsync(
        IServiceProvider services, Guid tenantId, DateTime now, CancellationToken ct)
    {
        try
        {
            var canonical = services.GetRequiredService<ICanonicalGlucoseService>();
            return await canonical.GetRecentAsync(now - SmartSnoozeTrendGate.RequiredHistory, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Failed to load recent glucose for snooze evaluation on tenant {TenantId}", tenantId);
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
        Sweep sweep,
        IEnumerable<SnoozedInstanceSnapshot> instances,
        Dictionary<Guid, SmartSnoozeConfig> configsByInstance,
        CancellationToken ct)
    {
        var enricher = sweep.Services.GetRequiredService<ISensorContextEnricher>();

        // Each instance's conditions become a synthetic composite{and, conditions} rule so
        // RuleDataNeeds.Walk sees what to enrich; rule identity is not used during enrichment.
        var syntheticRules = new List<AlertRuleSnapshot>();
        var withConditions = instances
            .Select(i => (Instance: i, configsByInstance[i.InstanceId].Conditions))
            .Where(x => x.Conditions is not null);
        foreach (var (instance, conditions) in withConditions)
        {
            var json = SmartSnoozeConfig.AndBody(conditions!.Value);
            syntheticRules.Add(new AlertRuleSnapshot(
                instance.AlertRuleId,
                sweep.TenantId,
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
            var last = await LastUsableReadingAsync(sweep, ct);
            var fresh = last.Reading is { } usable && sweep.Now - usable.Timestamp <= SmartSnoozeTrendGate.MaxLatestAge
                ? usable
                : null;
            var baseContext = new SensorContext
            {
                LatestValue = fresh is null ? null : (decimal)fresh.Mgdl,
                LatestTimestamp = last.At,
                TrendRate = (decimal?)fresh?.TrendRate,
                LastReadingAt = last.At,
            };
            return await enricher.EnrichAsync(baseContext, syntheticRules, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to enrich sensor context for snooze evaluation on tenant {TenantId}", sweep.TenantId);
            return null;
        }
    }

    private async Task<bool> EvaluateSnoozeConditionsAsync(
        IServiceProvider services,
        SnoozedInstanceSnapshot instance,
        JsonElement conditions,
        SensorContext enrichedContext,
        CancellationToken ct)
    {
        var engine = services.GetRequiredService<IAlertEvaluationEngine>();

        try
        {
            return await engine.EvaluateSnoozeAsync(instance.AlertRuleId, conditions, enrichedContext, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Snooze conditions evaluation failed for instance {InstanceId}", instance.InstanceId);
            return false;
        }
    }

    private SmartSnoozeConfig ParseSnoozeConfig(SnoozedInstanceSnapshot instance)
    {
        var config = SmartSnoozeConfig.Parse(instance.ClientConfiguration);
        if (config.Malformed)
        {
            logger.LogWarning(
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
    private async Task EvaluateAutoResolveAsync(Sweep sweep, CancellationToken ct)
    {
        var openExcursions = await sweep.Repository.GetAutoResolveExcursionsAsync(ct);
        if (openExcursions.Count == 0) return;

        var engine = sweep.Services.GetRequiredService<IAlertEvaluationEngine>();
        var enricher = sweep.Services.GetRequiredService<ISensorContextEnricher>();
        var resolutionHandler = sweep.Services.GetRequiredService<IExcursionResolutionHandler>();

        var last = await LastUsableReadingAsync(sweep, ct);
        var baseContext = new SensorContext
        {
            LatestValue = null,
            LatestTimestamp = last.At,
            TrendRate = null,
            LastReadingAt = last.At,
        };
        var enriched = await enricher.EnrichAsync(baseContext, openExcursions.Select(x => x.Rule).ToList(), ct);

        foreach (var entry in openExcursions)
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
                logger.LogError(ex, "Auto-resolve evaluation failed for rule {AlertRuleId}", entry.Rule.Id);
                continue;
            }

            if (transition.Type == ExcursionTransitionType.ExcursionClosed)
            {
                await resolutionHandler.HandleClosedAsync(transition, sweep.TenantId, ct);
            }
        }
    }

    /// <summary>
    /// Evaluates every enabled rule that <see cref="RuleTrees.ReferencesWallClock"/> selects
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
    private async Task EvaluateWallClockRulesAsync(Sweep sweep, CancellationToken ct)
    {
        var enabled = await sweep.Repository.GetEnabledRulesAsync(ct);
        _wallClock.TryGetValue(sweep.TenantId, out var kept);
        var verdicts = new Dictionary<Guid, WallClockVerdict>(enabled.Count);
        var wallClockRules = enabled.Where(rule => ReferencesWallClock(rule, kept, verdicts)).ToList();
        _wallClock[sweep.TenantId] = verdicts;
        if (wallClockRules.Count == 0) return;

        var last = await LastUsableReadingAsync(sweep, ct);
        var context = last.Reading is { } usable
            ? CanonicalAlertEvaluator.ContextFor(usable)
            : new SensorContext
            {
                LatestValue = null,
                LatestTimestamp = last.At,
                TrendRate = null,
                LastReadingAt = last.At,
            };

        var orchestrator = sweep.Services.GetRequiredService<IAlertOrchestrator>();
        await orchestrator.EvaluateRulesAsync(
            wallClockRules, enabled.Select(r => r.Id).ToHashSet(), context, ct);
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
    private async Task<UsableReading> LastUsableReadingAsync(Sweep sweep, CancellationToken ct)
    {
        var canonical = sweep.Services.GetRequiredService<ICanonicalGlucoseService>();
        var latest = await canonical.GetLatestAsync(ct);
        if (latest is null || latest.Mgdl > 0)
        {
            _usableLookbacks.TryRemove(sweep.TenantId, out _);
            return latest is null
                ? new UsableReading(null, (await sweep.TenantAsync(ct)).LastReadingAt)
                : new UsableReading(latest, latest.Timestamp);
        }

        if (_usableLookbacks.TryGetValue(sweep.TenantId, out var kept)
            && kept.NewestId == latest.Id && kept.NewestAt == latest.Timestamp)
        {
            return kept.Found;
        }

        var recent = await canonical.GetRecentAsync(latest.Timestamp - UsableReadingLookback, ct);
        var found = recent.Where(r => r.Mgdl > 0).MaxBy(r => r.Timestamp) is { } usable
            ? new UsableReading(usable, usable.Timestamp)
            : new UsableReading(null, recent.Count > 0 ? recent.Min(r => r.Timestamp) : latest.Timestamp);
        _usableLookbacks[sweep.TenantId] = new UsableLookback(latest.Id, latest.Timestamp, found);
        return found;
    }

    /// <param name="Reading">The last usable reading, or null when none was found.</param>
    /// <param name="At">What <see cref="SensorContext.LastReadingAt"/> is.</param>
    private sealed record UsableReading(SensorGlucose? Reading, DateTime? At);

    /// <summary>
    /// Whether <paramref name="rule"/> references the wall clock, reusing its verdict in
    /// <paramref name="kept"/> while the rule is unchanged, and recording it in <paramref name="verdicts"/>.
    /// </summary>
    private bool ReferencesWallClock(
        AlertRuleSnapshot rule,
        IReadOnlyDictionary<Guid, WallClockVerdict>? kept,
        Dictionary<Guid, WallClockVerdict> verdicts)
    {
        if (kept is not null
            && kept.TryGetValue(rule.Id, out var cached)
            && cached.Type == rule.ConditionType
            && string.Equals(cached.Params, rule.ConditionParams, StringComparison.Ordinal)
            && cached.AutoResolveEnabled == rule.AutoResolveEnabled
            && string.Equals(cached.AutoResolveParams, rule.AutoResolveParams, StringComparison.Ordinal))
        {
            verdicts[rule.Id] = cached;
            return cached.References;
        }

        bool references;
        try
        {
            references = RuleTrees.ReferencesWallClock(rule);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "The engine could not read the condition trees of rule {AlertRuleId}; it evaluates per reading only",
                rule.Id);
            references = false;
        }

        verdicts[rule.Id] = new WallClockVerdict(
            rule.ConditionType, rule.ConditionParams, rule.AutoResolveEnabled, rule.AutoResolveParams, references);
        return references;
    }
}
