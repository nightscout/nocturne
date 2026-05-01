using System.Text.Json;
using Nocturne.API.Services.Alerts.Evaluators;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.Alerts;

/// <summary>
/// Replays the tenant's enabled rule set over a historical glucose window using a
/// self-contained <see cref="ConditionEvaluatorRegistry"/> so the live tenant timer table
/// is never touched. Sustained timers, time-of-day, staleness, alert_state, and the
/// looping conditions (loop staleness/enaction, pump suspended/battery, temp basal,
/// uploader battery, override active, sensitivity ratio) are reconstructed via the same
/// <see cref="ISensorContextEnricher"/> the live engine uses, pinned per tick to the
/// replay timestamp via <see cref="ISensorContextEnricher.EnrichAsOfAsync"/>. Predictions
/// remain unmodelled (the prediction service is forward-from-now only) and surface as
/// <see cref="AlertReplayResult.Limitations"/>.
/// </summary>
internal sealed class AlertReplayService(
    IAlertRepository alertRepository,
    ISensorGlucoseRepository glucoseRepository,
    ISensorContextEnricher enricher,
    ITenantAccessor tenantAccessor,
    ILogger<AlertReplayService> logger)
    : IAlertReplayService
{
    /// <summary>
    /// Replay tick cadence. Glucose is reported every ~5 minutes; finer resolution would
    /// re-evaluate rules without new data.
    /// </summary>
    private static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(5);

    private const string LimitationsBanner =
        "Replay simulates threshold, rate-of-change, trend, time-of-day, staleness, sustained, " +
        "alert_state references, and looping conditions (loop staleness, pump suspended/battery, " +
        "temp basal, uploader battery, override active, sensitivity ratio) by reconstructing " +
        "tenant state at each replay tick. Predictions are not reconstructed (the prediction " +
        "service is forward-from-now only). Auto-resolve, escalation, quiet hours, and " +
        "smart-snooze are not modelled.";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    public async Task<AlertReplayResult> ReplayAsync(
        DateOnly? localDate, string? timezone, CancellationToken ct)
    {
        var tenantId = tenantAccessor.TenantId;
        if (tenantId == Guid.Empty)
        {
            return new AlertReplayResult(DateTime.UtcNow, DateTime.UtcNow, [], LimitationsBanner);
        }

        var (windowStart, windowEnd) = ResolveWindow(localDate, timezone);

        var rules = await alertRepository.GetEnabledRulesAsync(tenantId, ct);
        if (rules.Count == 0)
        {
            return new AlertReplayResult(windowStart, windowEnd, [], LimitationsBanner);
        }

        // Topo-sort by alert_state edges so a rule's parents have always been evaluated for
        // the same tick before the rule itself runs. Cycles (already prevented at write time
        // by AlertReferenceService) would short-circuit to insertion order.
        var ordered = TopologicallySort(rules);

        var readings = (await glucoseRepository.GetAsync(
                from: windowStart, to: windowEnd, device: null, source: null,
                limit: int.MaxValue, offset: 0, descending: false, nativeOnly: false, ct: ct))
            .OrderBy(r => r.Timestamp)
            .ToList();

        var fakeTime = new ReplayTimeProvider();
        var timerStore = new InMemoryConditionTimerStore();
        var registry = BuildReplayRegistry(timerStore, fakeTime);

        // Per-rule firing state across the replay so a continuously-true condition produces
        // one event at its leading edge rather than one per tick.
        var firing = new Dictionary<Guid, bool>(rules.Count);
        // ActiveAlerts snapshot threaded into the SensorContext so alert_state references
        // resolve against rules that already fired earlier in the replay's timeline. The
        // enricher's EnrichAsOfAsync skips its own active-alerts repo fetch and reads this
        // dict from baseContext.ActiveAlerts — letting the walker own the running tally.
        var activeAlerts = new Dictionary<Guid, ActiveAlertSnapshot>();

        var events = new List<AlertReplayEvent>();
        var readingIndex = 0;

        for (var tick = windowStart; tick < windowEnd; tick += TickInterval)
        {
            ct.ThrowIfCancellationRequested();

            // Advance the replay clock so any TimeProvider-aware evaluator (staleness,
            // time_of_day, site_age, sensor_age, alert_state for-minutes, loop_stale,
            // pump_suspended, override_active) sees `tick` as "now".
            fakeTime.SetUtcNow(DateTime.SpecifyKind(tick, DateTimeKind.Utc));

            // Walk the readings list once across the whole replay rather than re-scanning per
            // tick. Snap to the most recent reading at-or-before tick; trailing readings
            // (those after tick) stay queued for later ticks.
            while (readingIndex < readings.Count - 1
                   && readings[readingIndex + 1].Timestamp <= tick)
            {
                readingIndex++;
            }
            var current = readings.Count == 0 ? null : readings[readingIndex];
            var hasReadingForTick = current is not null && current.Timestamp <= tick;

            var baseContext = new SensorContext
            {
                LatestValue = hasReadingForTick ? (decimal)current!.Mgdl : null,
                LatestTimestamp = hasReadingForTick ? current!.Timestamp : null,
                TrendRate = hasReadingForTick && current!.TrendRate is { } tr ? (decimal)tr : null,
                // No-data ticks: clamp LastReadingAt to `tick` so staleness evaluators report
                // zero staleness rather than ~9999 years (DateTime.MinValue) and spuriously
                // fire on every leading tick. Active alerts dict is the same instance per pass
                // so a parent rule's mid-tick fire is visible to children later in the loop.
                LastReadingAt = hasReadingForTick ? current!.Timestamp : tick,
                ActiveAlerts = activeAlerts,
            };

            // Pin every fact in the per-tick context to `tick` — APS / pump / uploader /
            // state-span / temp-basal / device-event repos all support an as-of cutoff.
            var enrichedBase = await enricher.EnrichAsOfAsync(
                baseContext, ordered, tenantId, DateTime.SpecifyKind(tick, DateTimeKind.Utc), ct);

            foreach (var rule in ordered)
            {
                var node = BuildNodeForRule(rule);
                if (node is null) continue;

                var ruleContext = enrichedBase with
                {
                    CurrentRuleId = rule.Id,
                    CurrentPath = AlertConditionTypeNames.ToWireString(rule.ConditionType),
                };

                bool met;
                try
                {
                    met = await registry.EvaluateNodeAsync(node, ruleContext, ct);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex,
                        "Replay evaluation failed for rule {RuleId} at {Tick}; treating as not-met",
                        rule.Id, tick);
                    met = false;
                }

                var wasFiring = firing.GetValueOrDefault(rule.Id);
                if (met && !wasFiring)
                {
                    events.Add(new AlertReplayEvent(tick, rule.Id, rule.Name, rule.Severity));
                    activeAlerts[rule.Id] = new ActiveAlertSnapshot("firing", tick, null);
                }
                else if (!met && wasFiring)
                {
                    activeAlerts.Remove(rule.Id);
                    timerStore.ClearAllForRuleAsync(rule.Id, ct).GetAwaiter().GetResult();
                }
                firing[rule.Id] = met;
            }
        }

        return new AlertReplayResult(windowStart, windowEnd, events, LimitationsBanner);
    }

    /// <summary>
    /// Resolves the requested window in UTC. <paramref name="localDate"/> null → rolling 24 h
    /// ending at "now" (timezone irrelevant for a rolling window — both endpoints are absolute
    /// UTC instants). Set → that calendar day in <paramref name="timezone"/>,
    /// midnight-to-midnight, converted to UTC. On DST-transition days the resulting UTC
    /// window is 23 or 25 hours wide rather than exactly 24.
    /// </summary>
    private static (DateTime Start, DateTime End) ResolveWindow(DateOnly? localDate, string? timezone)
    {
        if (localDate is null)
        {
            var now = DateTime.UtcNow;
            return (now.AddHours(-24), now);
        }

        TimeZoneInfo tz;
        try
        {
            tz = string.IsNullOrWhiteSpace(timezone)
                ? TimeZoneInfo.Utc
                : TimeZoneInfo.FindSystemTimeZoneById(timezone);
        }
        catch (TimeZoneNotFoundException)
        {
            tz = TimeZoneInfo.Utc;
        }

        var localStart = localDate.Value.ToDateTime(TimeOnly.MinValue);
        var localEnd = localStart.AddDays(1);
        var startUtc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(localStart, DateTimeKind.Unspecified), tz);
        var endUtc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(localEnd, DateTimeKind.Unspecified), tz);
        return (startUtc, endUtc);
    }

    /// <summary>
    /// Reconstructs a <see cref="ConditionNode"/> from a rule's stored type+payload. Mirrors
    /// the helper in <see cref="AlertReferenceService"/> but lives here to keep replay
    /// self-contained.
    /// </summary>
    private ConditionNode? BuildNodeForRule(AlertRuleSnapshot rule)
    {
        try
        {
            return rule.ConditionType switch
            {
                AlertConditionType.Composite => new ConditionNode("composite",
                    Composite: JsonSerializer.Deserialize<CompositeCondition>(rule.ConditionParams, JsonOptions)),
                AlertConditionType.Not => new ConditionNode("not",
                    Not: JsonSerializer.Deserialize<NotCondition>(rule.ConditionParams, JsonOptions)),
                AlertConditionType.Sustained => new ConditionNode("sustained",
                    Sustained: JsonSerializer.Deserialize<SustainedCondition>(rule.ConditionParams, JsonOptions)),
                AlertConditionType.AlertState => new ConditionNode("alert_state",
                    AlertState: JsonSerializer.Deserialize<AlertStateCondition>(rule.ConditionParams, JsonOptions)),
                AlertConditionType.Threshold => new ConditionNode("threshold",
                    Threshold: JsonSerializer.Deserialize<ThresholdCondition>(rule.ConditionParams, JsonOptions)),
                AlertConditionType.RateOfChange => new ConditionNode("rate_of_change",
                    RateOfChange: JsonSerializer.Deserialize<RateOfChangeCondition>(rule.ConditionParams, JsonOptions)),
                AlertConditionType.Staleness => new ConditionNode("staleness",
                    Staleness: JsonSerializer.Deserialize<StalenessCondition>(rule.ConditionParams, JsonOptions)),
                AlertConditionType.Predicted => new ConditionNode("predicted",
                    Predicted: JsonSerializer.Deserialize<PredictedCondition>(rule.ConditionParams, JsonOptions)),
                AlertConditionType.Trend => new ConditionNode("trend",
                    Trend: JsonSerializer.Deserialize<TrendCondition>(rule.ConditionParams, JsonOptions)),
                AlertConditionType.TimeOfDay => new ConditionNode("time_of_day",
                    TimeOfDay: JsonSerializer.Deserialize<TimeOfDayCondition>(rule.ConditionParams, JsonOptions)),
                AlertConditionType.Iob => new ConditionNode("iob",
                    Iob: JsonSerializer.Deserialize<IobCondition>(rule.ConditionParams, JsonOptions)),
                AlertConditionType.Cob => new ConditionNode("cob",
                    Cob: JsonSerializer.Deserialize<CobCondition>(rule.ConditionParams, JsonOptions)),
                AlertConditionType.Reservoir => new ConditionNode("reservoir",
                    Reservoir: JsonSerializer.Deserialize<ReservoirCondition>(rule.ConditionParams, JsonOptions)),
                AlertConditionType.SiteAge => new ConditionNode("site_age",
                    SiteAge: JsonSerializer.Deserialize<SiteAgeCondition>(rule.ConditionParams, JsonOptions)),
                AlertConditionType.SensorAge => new ConditionNode("sensor_age",
                    SensorAge: JsonSerializer.Deserialize<SensorAgeCondition>(rule.ConditionParams, JsonOptions)),
                AlertConditionType.LoopStale => new ConditionNode("loop_stale",
                    LoopStale: JsonSerializer.Deserialize<LoopStaleCondition>(rule.ConditionParams, JsonOptions)),
                AlertConditionType.LoopEnactionStale => new ConditionNode("loop_enaction_stale",
                    LoopEnactionStale: JsonSerializer.Deserialize<LoopEnactionStaleCondition>(rule.ConditionParams, JsonOptions)),
                AlertConditionType.PumpSuspended => new ConditionNode("pump_suspended",
                    PumpSuspended: JsonSerializer.Deserialize<PumpSuspendedCondition>(rule.ConditionParams, JsonOptions)),
                AlertConditionType.PumpBattery => new ConditionNode("pump_battery",
                    PumpBattery: JsonSerializer.Deserialize<PumpBatteryCondition>(rule.ConditionParams, JsonOptions)),
                AlertConditionType.TempBasal => new ConditionNode("temp_basal",
                    TempBasal: JsonSerializer.Deserialize<TempBasalCondition>(rule.ConditionParams, JsonOptions)),
                AlertConditionType.UploaderBattery => new ConditionNode("uploader_battery",
                    UploaderBattery: JsonSerializer.Deserialize<UploaderBatteryCondition>(rule.ConditionParams, JsonOptions)),
                AlertConditionType.OverrideActive => new ConditionNode("override_active",
                    OverrideActive: JsonSerializer.Deserialize<OverrideActiveCondition>(rule.ConditionParams, JsonOptions)),
                AlertConditionType.SensitivityRatio => new ConditionNode("sensitivity_ratio",
                    SensitivityRatio: JsonSerializer.Deserialize<SensitivityRatioCondition>(rule.ConditionParams, JsonOptions)),
                _ => null,
            };
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Replay: malformed ConditionParams for rule {RuleId}", rule.Id);
            return null;
        }
    }

    /// <summary>
    /// Builds a self-contained registry whose evaluators share the supplied in-memory timer
    /// store and replay clock. The <see cref="ReplayServiceProvider"/> only resolves the
    /// registry itself — recursive evaluators (Composite/Not/Sustained) ask for it
    /// lazily, and the registry exposes <see cref="ConditionEvaluatorRegistry.EvaluateNodeAsync"/>
    /// as the single dispatch entrypoint.
    /// </summary>
    private static ConditionEvaluatorRegistry BuildReplayRegistry(
        IConditionTimerStore timerStore, TimeProvider time)
    {
        var sp = new ReplayServiceProvider();
        var evaluators = new IConditionEvaluator[]
        {
            new ThresholdEvaluator(),
            new RateOfChangeEvaluator(),
            new StalenessEvaluator(time),
            new PredictedEvaluator(),
            new TrendEvaluator(),
            new TimeOfDayEvaluator(time),
            new IobEvaluator(),
            new CobEvaluator(),
            new ReservoirEvaluator(),
            new SiteAgeEvaluator(time),
            new SensorAgeEvaluator(time),
            new AlertStateEvaluator(time),
            new LoopStaleEvaluator(time),
            new LoopEnactionStaleEvaluator(time),
            new PumpSuspendedEvaluator(time),
            new PumpBatteryEvaluator(),
            new TempBasalEvaluator(),
            new UploaderBatteryEvaluator(),
            new OverrideActiveEvaluator(time),
            new SensitivityRatioEvaluator(),
            new CompositeEvaluator(sp),
            new NotEvaluator(sp),
            new SustainedEvaluator(sp, timerStore, time),
        };
        var registry = new ConditionEvaluatorRegistry(evaluators);
        sp.Registry = registry;
        return registry;
    }

    /// <summary>
    /// Topologically sort rules so each rule appears after every rule it depends on via
    /// <c>alert_state</c>. Falls back to insertion order on cycle (cycles are blocked at
    /// write time, but defence-in-depth keeps replay alive on stale data).
    /// </summary>
    private IReadOnlyList<AlertRuleSnapshot> TopologicallySort(IReadOnlyList<AlertRuleSnapshot> rules)
    {
        var byId = rules.ToDictionary(r => r.Id);
        var dependencies = new Dictionary<Guid, HashSet<Guid>>(rules.Count);
        foreach (var rule in rules)
        {
            var deps = new HashSet<Guid>();
            var node = BuildNodeForRule(rule);
            if (node is not null)
            {
                foreach (var refId in ExtractAlertStateRefs(node))
                {
                    if (byId.ContainsKey(refId)) deps.Add(refId);
                }
            }
            dependencies[rule.Id] = deps;
        }

        var visited = new HashSet<Guid>();
        var result = new List<AlertRuleSnapshot>(rules.Count);

        bool Visit(Guid id, HashSet<Guid> stack)
        {
            if (visited.Contains(id)) return true;
            if (!stack.Add(id)) return false; // cycle
            foreach (var dep in dependencies[id])
            {
                if (!Visit(dep, stack)) return false;
            }
            stack.Remove(id);
            visited.Add(id);
            result.Add(byId[id]);
            return true;
        }

        foreach (var rule in rules)
        {
            if (!Visit(rule.Id, new HashSet<Guid>()))
            {
                logger.LogWarning("Replay topo-sort hit a cycle for tenant rules; falling back to insertion order");
                return rules;
            }
        }

        return result;
    }

    private static IEnumerable<Guid> ExtractAlertStateRefs(ConditionNode node)
    {
        if (node.AlertState is { } alertState) yield return alertState.AlertId;
        if (node.Composite is { } composite)
        {
            foreach (var child in composite.Conditions)
                foreach (var id in ExtractAlertStateRefs(child)) yield return id;
        }
        if (node.Not is { Child: { } notChild })
        {
            foreach (var id in ExtractAlertStateRefs(notChild)) yield return id;
        }
        if (node.Sustained is { Child: { } sustainedChild })
        {
            foreach (var id in ExtractAlertStateRefs(sustainedChild)) yield return id;
        }
    }

    private sealed class ReplayServiceProvider : IServiceProvider
    {
        public ConditionEvaluatorRegistry? Registry { get; set; }

        public object? GetService(Type serviceType) =>
            serviceType == typeof(ConditionEvaluatorRegistry) ? Registry : null;
    }

    /// <summary>
    /// Manual <see cref="TimeProvider"/> used in replay so each tick can advance "now"
    /// without taking a dependency on <c>Microsoft.Extensions.TimeProvider.Testing</c>
    /// in production code.
    /// </summary>
    private sealed class ReplayTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void SetUtcNow(DateTime utc)
        {
            _now = new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc));
        }
    }
}
