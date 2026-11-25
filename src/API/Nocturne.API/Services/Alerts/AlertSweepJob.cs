using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Nocturne.API.Services.Alerts.Engines;
using Nocturne.Core.Alerts.Native;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Jobs;
using Nocturne.Core.Contracts.Repositories;
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
///   <item>Finish the work an excursion's opening or close still owes (<see cref="ActOnOwedTransitionsAsync"/>).</item>
///   <item>Close excursions whose hysteresis window has elapsed.</item>
///   <item>Check snoozed instances for smart-snooze extension or re-fire.</item>
///   <item>Check each enabled rule's condition for a version that cannot be evaluated (<see cref="IUnevaluableRuleNotice"/>).</item>
///   <item>Tell the owners of each channel of an enabled rule that does not send (<see cref="IAlertChannelNotice"/>).</item>
///   <item>Archive a clock-anomaly notification once no stored reading is ahead of the clock (<see cref="IClockAnomalyNotice.NeedsReadingReport"/>).</item>
///   <item>Evaluate every enabled rule against a newest reading no evaluation completed for (<see cref="EvaluateLostReadingAsync"/>).</item>
///   <item>Evaluate rules with a wall-clock-sensitive condition (<see cref="RuleTrees.ReferencesWallClock"/>).</item>
///   <item>Run periodic auto-resolve for excursions whose conditions don't depend on the latest reading.</item>
/// </list>
/// The wall-clock pass evaluates every such rule of the tenant, so it runs after the cheap,
/// time-critical passes rather than delaying them. A pass that throws does not stop the passes
/// after it; the run then fails with the first error, and is retried after <see cref="SweepMaxBackoff"/>.
/// An inactive tenant's row is parked for <see cref="IScheduledJob.InactiveTenantRecheck"/>.
/// <para>
/// The job keeps three facts per tenant in memory, <see cref="_wallClock"/>,
/// <see cref="_usableLookbacks"/> and <see cref="_lostReported"/>, and none is a write or a
/// notification. A restart, or a run in another replica, reads the first two again from the
/// database: the same wall-clock verdict, and a look back that may find a usable reading backfilled
/// behind an unchanged newest one sooner than a kept look back would. The third only keeps a log line
/// to once per reading, so such a run logs once more.
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
    /// Per tenant, <see cref="RuleTrees.ReferencesWallClock"/> and <see cref="RuleTrees.WaitsForNextChange"/>
    /// per enabled rule, kept while the rule's body and auto-resolve configuration are unchanged, so a
    /// rule is asked about, and a failure reported, once per version rather than every run.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, IReadOnlyDictionary<Guid, WallClockVerdict>> _wallClock = new();

    private sealed record WallClockVerdict(
        AlertConditionType Type,
        string Params,
        bool AutoResolveEnabled,
        string? AutoResolveParams,
        string OnUnknown,
        bool References,
        bool WaitsForNextChange);

    /// <summary>
    /// Per tenant whose newest canonical reading has no glucose value, what
    /// <see cref="LastUsableReadingAsync"/> found looking back from it. The look back reads up to
    /// <see cref="AlertReadings.UsableReadingLookback"/> of readings, and each run would repeat it
    /// every <see cref="Interval"/> for as long as the outage lasts; it is repeated only once a newer
    /// reading arrives.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, KeptLookBack> _usableLookbacks = new();

    private sealed record KeptLookBack(Guid NewestId, DateTime NewestAt, UsableReading Found);

    /// <summary>
    /// Per tenant, the reading <see cref="EvaluateLostReadingAsync"/> last logged, so a reading
    /// whose evaluation keeps failing is logged once rather than every run.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, EvaluatedReading> _lostReported = new();

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
            _lostReported.TryRemove(tenantId, out _);
            return JobOutcome.Resume(null, run.StartedAt + IScheduledJob.InactiveTenantRecheck);
        }

        var sweep = new Sweep(run.Services, run.Services.GetRequiredService<IAlertRepository>(), tenantId, run.StartedAt.UtcDateTime);
        Exception? failed = null;
        foreach (var (pass, what) in new (Func<Sweep, CancellationToken, Task>, string)[]
                 {
                     (ActOnOwedTransitionsAsync, "finishing owed transitions"),
                     (CloseHysteresisWindowsAsync, "closing hysteresis windows"),
                     (CheckSnoozedInstancesAsync, "checking snoozed instances"),
                     (CheckRuleConditionsAsync, "checking rule conditions"),
                     (CheckAlertChannelsAsync, "checking alert channels"),
                     (CheckClockAnomaliesAsync, "checking clock-anomaly notifications"),
                     (EvaluateLostReadingAsync, "evaluating a reading no evaluation completed for"),
                     (EvaluateWallClockRulesAsync, "evaluating wall-clock rules"),
                     (CheckHeldRulesAsync, "checking held rules"),
                     (EvaluateAutoResolveAsync, "evaluating auto-resolve"),
                 })
        {
            try
            {
                await pass(sweep, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
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
        private EnabledAlertRules? _enabledRules;
        private AlertReadingSelection? _selection;
        private bool _readingReported;
        private (Guid ReadingId, DateTime At, ReadingTrend Trend)? _trend;

        public IServiceProvider Services => services;

        public IAlertRepository Repository => repository;

        public Guid TenantId => tenantId;

        public DateTime Now => now;

        /// <summary>The tenant's alert context, read once and only by a run that needs it.</summary>
        public async Task<TenantAlertContext> TenantAsync(CancellationToken ct) =>
            _tenant ??= await repository.GetTenantAlertContextAsync(tenantId, ct)
                        ?? throw new InvalidOperationException($"Tenant {tenantId} has no alert context.");

        /// <summary>The tenant's enabled rules and those it cannot read, read once per run.</summary>
        public async Task<EnabledAlertRules> ReadEnabledRulesAsync(CancellationToken ct) =>
            _enabledRules ??= await repository.ReadEnabledRulesAsync(ct);

        /// <summary>The tenant's enabled rules that can be read, read once per run.</summary>
        public async Task<IReadOnlyList<AlertRuleSnapshot>> EnabledRulesAsync(CancellationToken ct) =>
            (await ReadEnabledRulesAsync(ct)).Rules;

        /// <summary>The reading the tenant's alert contexts are built from (<see cref="AlertReadings"/>), read once per run.</summary>
        public async Task<AlertReadingSelection> SelectionAsync(CancellationToken ct) =>
            _selection ??= await AlertReadings.SelectAsync(services.GetRequiredService<ICanonicalGlucoseService>(), now, ct);

        /// <summary>
        /// Reports <see cref="SelectionAsync"/> to <see cref="IClockAnomalyNotice"/> the first time a
        /// pass measures from it, and never again in the run.
        /// </summary>
        public async Task ReportReadingAsync(ILogger logger, CancellationToken ct)
        {
            if (_readingReported) return;
            var selection = await SelectionAsync(ct);
            _readingReported = true;
            await services.GetRequiredService<IClockAnomalyNotice>().ReportReadingIsolatedAsync(selection, now, logger, ct);
        }

        /// <summary>The rules <see cref="EvaluateLostReadingAsync"/> evaluated in this run.</summary>
        public HashSet<Guid> EvaluatedByBackstop { get; } = [];

        /// <summary>
        /// The trend of <paramref name="reading"/>, read once per run: the snooze and wall-clock
        /// contexts both measure the same usable reading.
        /// </summary>
        public async Task<ReadingTrend> TrendOfAsync(SensorGlucose reading, CancellationToken ct)
        {
            if (_trend is { } kept && kept.ReadingId == reading.Id && kept.At == reading.Timestamp)
                return kept.Trend;

            var trend = await ReadingTrend.ForAsync(reading, services.GetRequiredService<ICanonicalGlucoseService>(), ct);
            _trend = (reading.Id, reading.Timestamp, trend);
            return trend;
        }
    }

    /// <summary>
    /// How long after the work of an opening or close was last taken up the sweep takes it up
    /// again. The evaluation that made the transition takes it up as it commits it and normally
    /// settles it within seconds, so only work whose taker died, or failed, is this old.
    /// </summary>
    internal static readonly TimeSpan OwedTransitionLease = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Finishes the work each opening or close still owes once its <see cref="OwedTransitionLease"/>
    /// has run out: a process that died, or a handler that threw, after the transition committed
    /// (docs/alerts/engine-semantics.md §7). The first run after a start is the start's pass, since
    /// the ledger finds the tenant's run overdue. Each is taken up first
    /// (<see cref="IAlertRepository.TakeUpOwedTransitionAsync"/>), so of two sweeps one acts, and a
    /// handler still running past the lease can be overlapped: its pending sends may then go twice.
    /// </summary>
    /// <remarks>
    /// An opening is dispatched while its excursion is open, acknowledged or not: an acknowledgement
    /// can be an acknowledge-all made for another alarm, and must not consume an alert nobody
    /// received. Exactly two cutoffs settle it without a send, since an alert for a condition that
    /// has ended misleads: the excursion has closed (checked again under the instance lock,
    /// <see cref="IAlertRepository.EnsureInstanceAsync"/>), or the rule is disabled or deleted. An
    /// enabled rule whose stored row cannot be read keeps its opening owed, taken up again each
    /// lease, until one of the two applies. A close is finished whatever its age. A handler that
    /// throws leaves its work owed and stops the pass; the work it took up waits out a new lease,
    /// so the next run goes on with the rest.
    /// </remarks>
    private async Task ActOnOwedTransitionsAsync(Sweep sweep, CancellationToken ct)
    {
        var owed = await sweep.Repository.GetOwedTransitionsAsync(sweep.Now - OwedTransitionLease, ct);
        if (owed.Count == 0) return;

        var resolutionHandler = sweep.Services.GetRequiredService<IExcursionResolutionHandler>();
        var openedHandler = sweep.Services.GetRequiredService<IExcursionOpenedHandler>();
        var enabled = await sweep.ReadEnabledRulesAsync(ct);
        var rules = enabled.Rules.ToDictionary(r => r.Id);
        var unreadable = enabled.Unreadable.Select(r => r.Id).ToHashSet();
        SensorContext? context = null;

        foreach (var transition in owed)
        {
            if (!await sweep.Repository.TakeUpOwedTransitionAsync(
                    transition.ExcursionId, transition.Kind, transition.TakenUpAt, sweep.Now, ct))
                continue;

            if (transition.Kind == ExcursionTransitionType.ExcursionClosed)
            {
                logger.LogWarning(
                    "Finishing the close of excursion {ExcursionId}, which its evaluation left unfinished",
                    transition.ExcursionId);
                await resolutionHandler.HandleClosedAsync(
                    new ExcursionTransition(ExcursionTransitionType.ExcursionClosed, transition.ExcursionId),
                    sweep.TenantId, ct);
                continue;
            }

            if (transition.EndedAt is null && unreadable.Contains(transition.AlertRuleId))
            {
                logger.LogWarning(
                    "Excursion {ExcursionId} opened without being dispatched, and its rule {AlertRuleId} cannot be read; it stays owed",
                    transition.ExcursionId, transition.AlertRuleId);
                continue;
            }

            if (transition.EndedAt is not null || !rules.TryGetValue(transition.AlertRuleId, out var rule))
            {
                logger.LogWarning(
                    "Excursion {ExcursionId} opened without being dispatched and has since closed or lost its rule; it is not sent",
                    transition.ExcursionId);
                if (transition.EndedAt is not null)
                    await resolutionHandler.CloseAgainIfEndedAsync(transition.ExcursionId, sweep.TenantId, ct);
                await sweep.Repository.SettleTransitionAsync(transition.ExcursionId, transition.Kind, ct);
                continue;
            }

            logger.LogWarning(
                "Dispatching excursion {ExcursionId} of rule {AlertRuleId}, which its evaluation opened without finishing its dispatch",
                transition.ExcursionId, rule.Id);
            context ??= await OwedDispatchContextAsync(sweep, owed, rules, ct);
            await openedHandler.HandleOpenedAsync(
                rule, transition.ExcursionId, context, sweep.TenantId, late: true, metBy: [], callerCloses: false, ct);
        }
    }

    /// <summary>
    /// The context a late dispatch is built from: the last usable reading when it is at most
    /// <see cref="SmartSnoozeTrendGate.MaxLatestAge"/> old, the bar a snooze's re-notification
    /// sets, and otherwise no glucose value with the last reading's time, so the payload never
    /// presents an old value or the dispatch time as a current reading. Enriched for the owed
    /// rules, which gives the Do Not Disturb scopes active now.
    /// </summary>
    private async Task<SensorContext> OwedDispatchContextAsync(
        Sweep sweep,
        IReadOnlyList<OwedTransition> owed,
        IReadOnlyDictionary<Guid, AlertRuleSnapshot> rules,
        CancellationToken ct)
    {
        var last = await LastUsableReadingAsync(sweep, ct);
        var context = last.Reading is { } usable && sweep.Now - usable.Timestamp <= SmartSnoozeTrendGate.MaxLatestAge
            ? CanonicalAlertEvaluator.ContextFor(usable, await sweep.TrendOfAsync(usable, ct))
            : new SensorContext
            {
                LatestValue = null,
                LatestTimestamp = last.At,
                TrendRate = null,
                LastReadingAt = last.At,
            };
        var owedRules = owed
            .Select(t => rules.GetValueOrDefault(t.AlertRuleId))
            .OfType<AlertRuleSnapshot>()
            .DistinctBy(r => r.Id)
            .ToList();
        return await sweep.Services.GetRequiredService<ISensorContextEnricher>().EnrichAsync(context, owedRules, ct);
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
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
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
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
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
            var bound = AlertReadings.Bound(now);
            return (await canonical.GetRecentAsync(now - SmartSnoozeTrendGate.RequiredHistory, ct))
                .Where(r => r.Timestamp <= bound)
                .ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
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
    /// the patient, so it is left null and those conditions read unknown, which clears the snooze.
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
            var trend = fresh is null ? default : await sweep.TrendOfAsync(fresh, ct);
            var baseContext = new SensorContext
            {
                LatestValue = fresh?.Mgdl.ToDecimal(),
                LatestTimestamp = last.At,
                TrendRate = trend.Rate,
                TrendBucket = trend.Bucket,
                LastReadingAt = last.At,
            };
            return await enricher.EnrichAsync(baseContext, syntheticRules, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
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
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
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
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
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
    /// Hands every enabled rule to <see cref="IUnevaluableRuleNotice"/>, the only sender of the
    /// notice, so a rule whose condition cannot be evaluated is told within one run of being stored
    /// rather than at the next reading, and a notice whose dispatch did not finish is sent by the
    /// next run. It runs with no enabled rule too: that is when every standing notice is one the
    /// run must resolve, and then it costs one read and no validation.
    /// </summary>
    private static async Task CheckRuleConditionsAsync(Sweep sweep, CancellationToken ct) =>
        await sweep.Services.GetRequiredService<IUnevaluableRuleNotice>()
            .CheckAsync(await sweep.ReadEnabledRulesAsync(ct), ct);

    private static async Task CheckAlertChannelsAsync(Sweep sweep, CancellationToken ct) =>
        await sweep.Services.GetRequiredService<IAlertChannelNotice>()
            .CheckAsync(await sweep.EnabledRulesAsync(ct), sweep.Now, ct);

    private async Task CheckClockAnomaliesAsync(Sweep sweep, CancellationToken ct)
    {
        if (sweep.Services.GetRequiredService<IClockAnomalyNotice>().NeedsReadingReport(sweep.Now))
            await sweep.ReportReadingAsync(logger, ct);
    }

    /// <summary>
    /// Hands every enabled rule to <see cref="IHeldRuleNotice"/>, after the evaluation pass has
    /// evaluated each hold that reached its bound, so a reported hold is told within the run.
    /// </summary>
    private static async Task CheckHeldRulesAsync(Sweep sweep, CancellationToken ct) =>
        await sweep.Services.GetRequiredService<IHeldRuleNotice>()
            .CheckAsync(await sweep.EnabledRulesAsync(ct), ct);

    /// <summary>
    /// Evaluates every enabled rule that <see cref="RuleTrees.ReferencesWallClock"/> selects
    /// through the orchestrator's full pipeline. Those conditions change truth with the clock
    /// alone, so between readings, or with no readings at all, only this pass moves them. The
    /// excursion tracker dedupes: a condition that stays true is <c>ExcursionContinues</c>.
    /// <c>alert_state</c> references resolve against every enabled rule of the tenant, since an
    /// escalation's parent is usually reading-driven and so not in the swept set.
    /// </summary>
    /// <remarks>
    /// The context is the one the per-reading path builds for the selected reading
    /// (<see cref="CanonicalAlertEvaluator.ContextFor"/>, or
    /// <see cref="CanonicalAlertEvaluator.ContextForValueless"/> for one with no value), so reading-driven leaves in the same
    /// tree judge what its last pass judged until the engine finds that reading stale
    /// (docs/alerts/engine-semantics.md §3). A context without the glucose facts would read them
    /// unknown before then, and a rule whose <c>on_unknown</c> is <c>not_met</c> would close an
    /// excursion they hold open.
    /// <para>
    /// A rule that <see cref="RuleTrees.WaitsForNextChange"/> is skipped while the next change its
    /// last evaluation recorded is still ahead, or it recorded none, and that evaluation read the
    /// glucose facts this context carries (<see cref="NextChange.GlucoseOf"/>): every write that moves
    /// a fact such a rule reads evaluates it (<see cref="ContextFactWrites"/>), so between them only
    /// the clock moves it. That holds only while the newest canonical reading is usable, the one a
    /// write's evaluation measures (<see cref="CanonicalAlertEvaluator"/>); with none, or with one
    /// that has no value, every rule is evaluated: the context then rests on a reading found by
    /// looking back, which a write behind it can change without moving the newest reading.
    /// </para>
    /// <para>
    /// Every enabled rule whose hold has reached the engine's bound unreported is evaluated as well
    /// (<see cref="IHeldRuleNotice.DueAsync"/>), wall-clock or not and whatever its next change, so
    /// the bound is reached with no reading (docs/alerts/engine-semantics.md §6.4).
    /// </para>
    /// <para>
    /// A rule <see cref="EvaluateLostReadingAsync"/> evaluated in the same run is skipped: it was just
    /// decided against the same reading. One that threw there is not retried here but by the next
    /// run's backstop, since its reading is neither recorded as evaluated nor kept claimed.
    /// </para>
    /// </remarks>
    private async Task EvaluateWallClockRulesAsync(Sweep sweep, CancellationToken ct)
    {
        var enabled = await sweep.EnabledRulesAsync(ct);
        _wallClock.TryGetValue(sweep.TenantId, out var kept);
        var verdicts = new Dictionary<Guid, WallClockVerdict>(enabled.Count);
        var wallClockRules = enabled
            .Where(rule => ReferencesWallClock(rule, kept, verdicts) && !sweep.EvaluatedByBackstop.Contains(rule.Id))
            .ToList();
        _wallClock[sweep.TenantId] = verdicts;
        var heldDue = (await sweep.Services.GetRequiredService<IHeldRuleNotice>().DueAsync(enabled, sweep.Now, ct))
            .Where(rule => !sweep.EvaluatedByBackstop.Contains(rule.Id))
            .ToList();
        if (wallClockRules.Count == 0 && heldDue.Count == 0) return;

        var last = await LastUsableReadingAsync(sweep, ct);
        var context = last switch
        {
            { Valueless: var (newest, back) } => CanonicalAlertEvaluator.ContextForValueless(newest, back),
            { Reading: { } usable } => CanonicalAlertEvaluator.ContextFor(usable, await sweep.TrendOfAsync(usable, ct)),
            _ => new SensorContext
            {
                LatestValue = null,
                LatestTimestamp = last.At,
                TrendRate = null,
                LastReadingAt = last.At,
            },
        };
        if (last.Newest)
            wallClockRules = await NotYetDueAsync(sweep, wallClockRules, verdicts, NextChange.GlucoseOf(context), ct);
        wallClockRules.AddRange(heldDue.Where(rule => wallClockRules.All(r => r.Id != rule.Id)));
        if (wallClockRules.Count == 0) return;

        var orchestrator = sweep.Services.GetRequiredService<IAlertOrchestrator>();
        await orchestrator.EvaluateRulesAsync(
            wallClockRules, enabled.Select(r => r.Id).ToHashSet(), context, ct);
    }

    /// <summary>
    /// How long after a reading was stored the sweep takes its evaluation as lost. A glucose write's
    /// evaluation starts at once, with no debounce (<see cref="AlertFactEvaluationScheduler"/>), and is
    /// one canonical read, one enrichment and one engine call per rule, so it normally completes well
    /// within a second; the rest leaves room for a slow delivery inside the pass. How late a lost
    /// evaluation is then made good is bounded in docs/alerts/engine-semantics.md §5.2.
    /// </summary>
    internal static readonly TimeSpan LostEvaluationGrace = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The oldest arrival a reading the sweep makes good can have: the largest glucose freshness bound
    /// a rule can have. A reading that arrived longer ago is stale to every glucose leaf, which ages a
    /// reading from its arrival as the sweep does here (docs/alerts/engine-semantics.md §3), so its
    /// evaluation could open nothing through glucose and would only start holds nothing started before.
    /// </summary>
    internal static readonly TimeSpan LostEvaluationReach = TimeSpan.FromMinutes(AlertRuleGlucoseFreshness.MaxMinutes);

    /// <summary>
    /// Evaluates every enabled rule, through <see cref="ICanonicalAlertEvaluator"/> as a glucose write
    /// does, when the newest reading <see cref="AlertReadings"/> selects, with a value or not, is not the
    /// one the tenant's last completed pass recorded (<see cref="IEvaluatedReadingStore"/>), was
    /// stored at least <see cref="LostEvaluationGrace"/> ago and at most <see cref="LostEvaluationReach"/>
    /// ago: its write's evaluation was dropped at shutdown, died with its process, threw, or left a rule
    /// undecided (docs/alerts/engine-semantics.md §5.2).
    /// </summary>
    /// <remarks>
    /// The evaluation claims the reading (<see cref="AlertEvaluationWatermark.TryClaim"/>), so in one
    /// process it does not run while the write's own pass on the reading runs, or after one completed
    /// within <see cref="AlertEvaluationWatermark.MaxSkipWindow"/>. A pass of another process on the
    /// reading that outlasts the grace is evaluated twice; the transition lock re-reads the tracker and
    /// drops the stale decision (<see cref="ExcursionTransitionWriter"/>), so an alert opens once.
    /// <para>
    /// The reading's stored time and <see cref="Sweep.Now"/> are both read from the API's clock. A
    /// stored time ahead of the sweep's clock is another process's clock running ahead, which cannot
    /// say how long the reading has been stored, so the reading is evaluated: a second evaluation is
    /// harmless and a missed one is not.
    /// </para>
    /// <para>
    /// A tenant with no record has had no evaluation recorded since the record began, which is no
    /// sign of a loss, so its first is logged as information. A record that names another reading is
    /// such a sign, so it is logged as a warning. Each is logged only once the pass has run, and once per
    /// reading.
    /// </para>
    /// </remarks>
    private async Task EvaluateLostReadingAsync(Sweep sweep, CancellationToken ct)
    {
        var enabled = await sweep.EnabledRulesAsync(ct);
        if (enabled.Count == 0) return;
        if ((await sweep.SelectionAsync(ct)).Selected is not { } newest) return;

        var stored = sweep.Now - newest.CreatedAt;
        if (stored > LostEvaluationReach) return;

        var reading = EvaluatedReading.Of(newest);
        var recorded = await sweep.Services.GetRequiredService<IEvaluatedReadingStore>().GetAsync(ct);
        if (recorded == reading)
        {
            _lostReported.TryRemove(sweep.TenantId, out _);
            return;
        }

        if (stored >= TimeSpan.Zero && stored < LostEvaluationGrace) return;

        var ran = await sweep.Services.GetRequiredService<ICanonicalAlertEvaluator>().EvaluateAsync(
            new MovedFacts(new HashSet<string>(StringComparer.Ordinal) { ContextFactWrites.Glucose }),
            ReadingReport.ByCaller,
            ct);
        await sweep.ReportReadingAsync(logger, ct);
        if (ran)
            sweep.EvaluatedByBackstop.UnionWith(enabled.Select(rule => rule.Id));
        if (!ran || (_lostReported.TryGetValue(sweep.TenantId, out var reported) && reported == reading)) return;

        _lostReported[sweep.TenantId] = reading;
        if (recorded is null)
        {
            logger.LogInformation(
                "The sweep evaluated every enabled rule of tenant {TenantId} against its newest reading, the one at {ReadingTimestamp}: no evaluation was recorded for the tenant before",
                sweep.TenantId, newest.Timestamp);
        }
        else
        {
            logger.LogWarning(
                "No evaluation of the reading at {ReadingTimestamp} was recorded as complete for tenant {TenantId}; the sweep evaluated every enabled rule against it, and does so each run until one is",
                newest.Timestamp, sweep.TenantId);
        }
    }

    /// <summary>
    /// <paramref name="rules"/> without each that waits for its next change and whose last
    /// evaluation, from the <paramref name="glucose"/> facts of the run's context, recorded one
    /// still ahead of the run, or none.
    /// </summary>
    private static async Task<List<AlertRuleSnapshot>> NotYetDueAsync(
        Sweep sweep,
        List<AlertRuleSnapshot> rules,
        Dictionary<Guid, WallClockVerdict> verdicts,
        string glucose,
        CancellationToken ct)
    {
        var tracker = sweep.Services.GetRequiredService<IAlertTrackerRepository>();
        var due = new List<AlertRuleSnapshot>(rules.Count);
        foreach (var rule in rules)
        {
            if (verdicts[rule.Id].WaitsForNextChange
                && await tracker.GetTrackerStateAsync(rule.Id, ct) is { NextChangeKnown: true } state
                && state.NextChangeGlucose == glucose
                && (state.NextChangeAt is not { } next || next > sweep.Now))
                continue;
            due.Add(rule);
        }
        return due;
    }

    /// <summary>
    /// The reading every swept context measures <see cref="SensorContext.LastReadingAt"/> from:
    /// the newest canonical reading with a glucose value that <see cref="AlertReadings"/> selects,
    /// so <c>signal_loss</c> counts readings with no value as no signal. When the selected reading
    /// has none it is <see cref="UsableReading.Valueless"/>, and the reading with a value is looked
    /// for behind it (<see cref="AlertReadings.LookBackAsync"/>).
    /// </summary>
    /// <remarks>
    /// With no canonical reading at all <see cref="UsableReading.At"/> is the tenant's newest
    /// reading of any source, null for a tenant that has never had one, which <c>signal_loss</c>
    /// and <c>staleness</c> treat as cold start.
    /// <para>
    /// The look back is kept per tenant (<see cref="_usableLookbacks"/>) until the newest
    /// canonical reading changes, so a usable reading backfilled behind an unchanged newest one
    /// is seen once the next reading arrives.
    /// </para>
    /// </remarks>
    private async Task<UsableReading> LastUsableReadingAsync(Sweep sweep, CancellationToken ct)
    {
        var selection = await sweep.SelectionAsync(ct);
        await sweep.ReportReadingAsync(logger, ct);
        var latest = selection.Selected;
        if (latest is null || AlertReadings.HasValue(latest))
        {
            _usableLookbacks.TryRemove(sweep.TenantId, out _);
            return latest is null
                ? new UsableReading(null, (await sweep.TenantAsync(ct)).LastReadingAt)
                : new UsableReading(latest, latest.Timestamp, Newest: true);
        }

        if (_usableLookbacks.TryGetValue(sweep.TenantId, out var kept)
            && kept.NewestId == latest.Id && kept.NewestAt == latest.Timestamp)
        {
            return kept.Found;
        }

        var back = await AlertReadings.LookBackAsync(sweep.Services.GetRequiredService<ICanonicalGlucoseService>(), latest, ct);
        var found = new UsableReading(back.Usable, back.At, Valueless: (latest, back));
        _usableLookbacks[sweep.TenantId] = new KeptLookBack(latest.Id, latest.Timestamp, found);
        return found;
    }

    /// <param name="Reading">The last usable reading, or null when none was found.</param>
    /// <param name="At">What <see cref="SensorContext.LastReadingAt"/> is.</param>
    /// <param name="Newest">Whether <paramref name="Reading"/> is the newest canonical reading selected, not one looked back for.</param>
    /// <param name="Valueless">The selected reading when it has no glucose value, with what was found behind it.</param>
    private sealed record UsableReading(
        SensorGlucose? Reading,
        DateTime? At,
        bool Newest = false,
        (SensorGlucose Newest, UsableLookBack Back)? Valueless = null);

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
            && string.Equals(cached.AutoResolveParams, rule.AutoResolveParams, StringComparison.Ordinal)
            && string.Equals(cached.OnUnknown, rule.OnUnknown, StringComparison.Ordinal))
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

        var waits = false;
        try
        {
            waits = references && RuleTrees.WaitsForNextChange(rule);
        }
        catch (RustAlertEngineException ex)
        {
            logger.LogWarning(ex,
                "The engine could not read the facts rule {AlertRuleId} reads; the sweep evaluates it every run",
                rule.Id);
        }

        verdicts[rule.Id] = new WallClockVerdict(
            rule.ConditionType, rule.ConditionParams, rule.AutoResolveEnabled, rule.AutoResolveParams, rule.OnUnknown,
            references, waits);
        return references;
    }
}
