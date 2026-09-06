using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Nocturne.API.Configuration;
using Nocturne.API.Extensions;
using Nocturne.API.Services.Alerts.Evaluators;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Nocturne.Core.Models.Alerts.Conditions;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.Alerts;

/// <summary>
/// Replays the tenant's enabled rule set over a historical glucose window using a
/// self-contained <see cref="ConditionEvaluatorRegistry"/> so the live tenant timer table
/// is never touched. Sustained timers, time-of-day, staleness, alert_state, the
/// looping conditions (loop staleness/enaction, pump suspended/battery, temp basal,
/// uploader battery, override active, sensitivity ratio), and predictions are reconstructed
/// via the same <see cref="ISensorContextEnricher"/> the live engine uses, pinned per tick
/// to the replay timestamp via <see cref="ISensorContextEnricher.EnrichAsOfAsync"/>.
/// Auto-resolve (mirroring <c>AlertOrchestrator.TryAutoResolveAsync</c>) and DND suppression
/// (mirroring <c>HandleExcursionOpened</c>'s suppressed-by-DND gate) surface as dedicated
/// <see cref="AlertReplayEventKind"/> values.
/// </summary>
internal sealed class AlertReplayService(
    IAlertRepository alertRepository,
    ISensorGlucoseRepository glucoseRepository,
    ICanonicalGlucoseService canonicalGlucose,
    ISensorContextEnricher enricher,
    ITenantAccessor tenantAccessor,
    IOptions<AlertEvaluationOptions> evaluationOptions,
    ILogger<AlertReplayService> logger)
    : IAlertReplayService
{
    /// <summary>
    /// Replay tick cadence. Glucose is reported every ~5 minutes; finer resolution would
    /// re-evaluate rules without new data.
    /// </summary>
    private static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(5);

    public Task<AlertReplayResult> ReplayAsync(
        DateOnly? localDate, string? timezone, DateTime? fromUtc, DateTime? toUtc, CancellationToken ct)
        => ReplayInternalAsync(localDate, timezone, fromUtc, toUtc, ruleOverride: null, ct);

    public Task<AlertReplayResult> ReplayDryRunAsync(
        DateOnly? localDate, string? timezone, DateTime? fromUtc, DateTime? toUtc,
        ReplayRuleOverride ruleOverride, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ruleOverride);
        return ReplayInternalAsync(localDate, timezone, fromUtc, toUtc, ruleOverride, ct);
    }

    /// <summary>
    /// Shared replay body. The optional <paramref name="ruleOverride"/> is applied to the
    /// rule list before evaluation: when its <c>Id</c> matches an existing rule, the override
    /// replaces it; otherwise the override is appended to the list (with a server-assigned
    /// id so any <c>alert_state</c> references the editor seeded resolve correctly).
    /// </summary>
    private async Task<AlertReplayResult> ReplayInternalAsync(
        DateOnly? localDate,
        string? timezone,
        DateTime? fromUtc,
        DateTime? toUtc,
        ReplayRuleOverride? ruleOverride,
        CancellationToken ct)
    {
        // Window validation is pure input checking, so it runs before tenant resolution and any
        // DB work — an over-wide window is the same client error whatever the tenant state is.
        var (windowStart, windowEnd) = ResolveWindow(localDate, timezone, fromUtc, toUtc);
        var maxWindow = evaluationOptions.Value.MaxReplayWindow;
        if (windowEnd - windowStart > maxWindow)
        {
            throw new ReplayWindowTooLargeException(windowEnd - windowStart, maxWindow);
        }

        var tenantId = tenantAccessor.TenantId;
        if (tenantId == Guid.Empty)
        {
            return new AlertReplayResult(DateTime.UtcNow, DateTime.UtcNow, []);
        }

        var stored = await alertRepository.GetEnabledRulesAsync(tenantId, ct);
        var rules = ApplyOverride(stored, ruleOverride, tenantId);
        if (rules.Count == 0)
        {
            return new AlertReplayResult(windowStart, windowEnd, []);
        }

        // Topo-sort by alert_state edges so a rule's parents have always been evaluated for
        // the same tick before the rule itself runs. Cycles (already prevented at write time
        // by AlertReferenceService) would short-circuit to insertion order.
        var ordered = TopologicallySort(rules);

        // Replay walks the canonical stream — the same series the live engine alarms on. The
        // window is keyset-paged and consumed by the tick loop as a left-to-right fold, so the
        // whole series is never resident.
        await using var readings = await ReadingCursor.CreateAsync(
            StreamCanonicalReadingsAsync(windowStart, windowEnd, ct), ct);

        // Scoped DND (ADR 0004 D5): the tenant's windows received by the replay's end, resolved
        // per tick with WasActiveAt (receipt-gated) so replay reproduces what the live engine
        // saw and never rewrites the offline-authoring gap. Both the suppression scopes and the
        // do_not_disturb leaf's ActiveDoNotDisturb snapshot re-source from these windows per
        // tick. Scheduled DND is not reconstructible historically (the schedule row keeps no
        // change history), so — as before D5 — replay considers windows only.
        var dndWindows = await alertRepository.GetDndWindowsAsOfAsync(tenantId, windowEnd, ct);

        // Replay evaluates through the managed ConditionEvaluatorRegistry directly rather
        // than IAlertEvaluationEngine. The seam contract assumes engine-owned persistence,
        // while replay needs a replay-local in-memory timer store and a fake clock per
        // pass — wiring the rust path here means threading that state through per-tick
        // envelopes like the parity tests do. Replay is read-only and its semantics are
        // pinned by the golden corpus (tests/Parity/AlertEngineCorpus), so it stays
        // managed until the cutover phase.
        var fakeTime = new ReplayTimeProvider();
        var timerStore = new InMemoryConditionTimerStore();
        await using var replayServices = BuildReplayServices(timerStore, fakeTime);
        var registry = replayServices.GetRequiredService<ConditionEvaluatorRegistry>();

        // Per-rule firing state across the replay so a continuously-true condition produces
        // one event at its leading edge rather than one per tick.
        var firing = new Dictionary<Guid, bool>(rules.Count);
        // Replay-only per-leaf transition log. Keyed first by rule id, then by leaf id
        // (assigned via LeafIdentity.AssignLeafIds on each tick for the rule's tree).
        // We track the previous boolean per leaf so we only push a point when truth flips,
        // and we always seed the first observed tick so callers can render the baseline.
        var forceRunner = new ForceEvalRunner();
        var leafPrev = new Dictionary<Guid, Dictionary<int, bool>>(rules.Count);
        var leafPoints = new Dictionary<Guid, Dictionary<int, List<LeafTransitionPoint>>>(rules.Count);
        // Per-tick fact snapshots (site age, IOB, temp basal rate, etc.). Keyed by snake_case
        // fact name; same compression as the leaf log — emit baseline + on rounded-value flip.
        // The previous-value map stores the rounded value to keep the change-detection cheap
        // and stable against floating-point jitter.
        var factPrev = new Dictionary<string, decimal>();
        var factPoints = new Dictionary<string, List<FactSnapshotPoint>>();
        // ActiveAlerts snapshot threaded into the SensorContext so alert_state references
        // resolve against rules that already fired earlier in the replay's timeline. The
        // enricher's EnrichAsOfAsync skips its own active-alerts repo fetch and reads this
        // dict from baseContext.ActiveAlerts — letting the walker own the running tally.
        var activeAlerts = new Dictionary<Guid, ActiveAlertSnapshot>();

        var events = new List<AlertReplayEvent>();

        for (var tick = windowStart; tick < windowEnd; tick += TickInterval)
        {
            ct.ThrowIfCancellationRequested();

            // Advance the replay clock so any TimeProvider-aware evaluator (staleness,
            // time_of_day, site_age, sensor_age, alert_state for-minutes, loop_stale,
            // pump_suspended, override_active) sees `tick` as "now".
            fakeTime.SetUtcNow(DateTime.SpecifyKind(tick, DateTimeKind.Utc));

            // Walk the reading stream once across the whole replay rather than re-scanning per
            // tick. Snap to the most recent reading at-or-before tick; trailing readings
            // (those after tick) stay queued for later ticks.
            await readings.AdvanceToAsync(tick);
            var current = readings.Current;
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
            var tickUtc = DateTime.SpecifyKind(tick, DateTimeKind.Utc);
            // Receipt-gated, and through the same resolver the live enricher uses so the two
            // cannot disagree about how a window resolves. No scheduled projection is passed:
            // a recurring schedule's past state is not reconstructible from the settings row.
            var tickDnd = DndWindowResolver.Resolve(dndWindows, tickUtc, receiptGated: true);
            var enrichedBase = (await enricher.EnrichAsOfAsync(
                baseContext, ordered, tenantId, tickUtc, ct))
                with
            {
                ActiveDndScopes = tickDnd.Scopes,
                ActiveDoNotDisturb = tickDnd.ActiveDoNotDisturb,
            };

            CaptureFactSnapshots(enrichedBase, DateTime.SpecifyKind(tick, DateTimeKind.Utc), factPrev, factPoints);

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

                // Force-evaluate every leaf in the rule's tree so the per-leaf transition
                // log captures truth even when the live composite evaluator would have
                // short-circuited. Failures inside individual leaves are swallowed by the
                // runner (mirrors the registry's silent-fail mode).
                IReadOnlyDictionary<int, bool> leafValues;
                try
                {
                    leafValues = await forceRunner.EvaluateAllLeavesAsync(node, ruleContext, registry, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex,
                        "Replay force-eval failed for rule {RuleId} at {Tick}; skipping leaf log for this tick",
                        rule.Id, tick);
                    leafValues = new Dictionary<int, bool>();
                }

                if (leafValues.Count > 0)
                {
                    if (!leafPrev.TryGetValue(rule.Id, out var prevForRule))
                    {
                        prevForRule = new Dictionary<int, bool>(leafValues.Count);
                        leafPrev[rule.Id] = prevForRule;
                    }
                    if (!leafPoints.TryGetValue(rule.Id, out var pointsForRule))
                    {
                        pointsForRule = new Dictionary<int, List<LeafTransitionPoint>>(leafValues.Count);
                        leafPoints[rule.Id] = pointsForRule;
                    }
                    var tickMs = new DateTimeOffset(DateTime.SpecifyKind(tick, DateTimeKind.Utc))
                        .ToUnixTimeMilliseconds();
                    foreach (var (leafId, value) in leafValues)
                    {
                        if (!prevForRule.TryGetValue(leafId, out var prev))
                        {
                            // First observation — seed a baseline point so callers can render
                            // the leaf's starting state without scanning later transitions.
                            if (!pointsForRule.TryGetValue(leafId, out var list))
                            {
                                list = new List<LeafTransitionPoint>();
                                pointsForRule[leafId] = list;
                            }
                            list.Add(new LeafTransitionPoint(tickMs, value));
                            prevForRule[leafId] = value;
                        }
                        else if (prev != value)
                        {
                            pointsForRule[leafId].Add(new LeafTransitionPoint(tickMs, value));
                            prevForRule[leafId] = value;
                        }
                    }
                }

                var wasFiring = firing.GetValueOrDefault(rule.Id);

                // Step 1: open / continue / close — mirrors AlertOrchestrator.EvaluateRuleAsync's
                // ExcursionTransition switch. `currentlyFiring` tracks whether the rule has an
                // open excursion after this step, fed to the auto-resolve gate below.
                bool currentlyFiring;
                if (met && !wasFiring)
                {
                    // Fresh open. DND suppression mirror of HandleExcursionOpened: a non-Critical
                    // rule whose class is covered by an active DND scope (receipt-gated to this
                    // tick) would have been recorded as suppressed in the live engine. We still
                    // seed activeAlerts so downstream alert_state references see the excursion as
                    // open — only the surfaced event kind differs (matches live, where the
                    // instance row is created and then marked suppressed).
                    var suppressedByDnd =
                        DndSuppressionGate.IsSuppressed(rule, ruleContext.ActiveDndScopes);

                    var kind = suppressedByDnd
                        ? AlertReplayEventKind.SuppressedByDnd
                        : AlertReplayEventKind.Fired;
                    events.Add(new AlertReplayEvent(tick, rule.Id, rule.Name, rule.Severity, kind));
                    activeAlerts[rule.Id] = new ActiveAlertSnapshot("firing", tick, null);
                    currentlyFiring = true;
                }
                else if (!met && wasFiring)
                {
                    // Natural clear. Live doesn't have this transition (excursions only close
                    // via auto-resolve or manual close); replay relaxes it so a rule whose body
                    // bounces met true→false→true produces a useful re-fire marker on the
                    // second open rather than staying silent. Manual closes are out of scope.
                    activeAlerts.Remove(rule.Id);
                    await timerStore.ClearAllForRuleAsync(rule.Id, ct);
                    currentlyFiring = false;
                }
                else
                {
                    // Continues firing (met && wasFiring) or continues not firing.
                    currentlyFiring = met;
                }

                // Step 2: auto-resolve. Mirrors AlertOrchestrator.EvaluateRuleAsync's
                // unconditional fall-through to TryAutoResolveAsync after the open/continue
                // path — runs against the same ruleContext under the AutoResolvePathRoot prefix
                // so nested sustained timers don't collide with timers owned by the main rule
                // body. The unconditional gate (live runs this even on a same-tick open) is the
                // important bit: a rule whose body opens at tick T and whose resolve predicate
                // is already true at T produces a fired+auto_resolved pair, matching live.
                if (currentlyFiring
                    && rule.AutoResolveEnabled
                    && !string.IsNullOrWhiteSpace(rule.AutoResolveParams))
                {
                    ConditionNode? resolveNode = null;
                    try
                    {
                        resolveNode = JsonSerializer.Deserialize<ConditionNode>(
                            rule.AutoResolveParams, EvaluatorJson.Options);
                    }
                    catch (JsonException ex)
                    {
                        logger.LogWarning(ex,
                            "Replay: malformed AutoResolveParams for rule {RuleId}; skipping auto-resolve",
                            rule.Id);
                    }

                    if (resolveNode is not null)
                    {
                        var resolveContext = ruleContext with
                        {
                            CurrentPath = AlertConditionTypeNames.AutoResolvePathRoot,
                        };
                        bool shouldResolve;
                        try
                        {
                            shouldResolve = await registry.EvaluateNodeAsync(resolveNode, resolveContext, ct);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            logger.LogWarning(ex,
                                "Replay auto-resolve evaluation failed for rule {RuleId} at {Tick}; treating as not-resolved",
                                rule.Id, tick);
                            shouldResolve = false;
                        }

                        if (shouldResolve)
                        {
                            events.Add(new AlertReplayEvent(
                                tick, rule.Id, rule.Name, rule.Severity, AlertReplayEventKind.AutoResolved));
                            activeAlerts.Remove(rule.Id);
                            await timerStore.ClearAllForRuleAsync(rule.Id, ct);
                            currentlyFiring = false;
                        }
                    }
                }

                firing[rule.Id] = currentlyFiring;
            }
        }

        var leafTransitionsByRule = leafPoints.ToDictionary(
            kvp => kvp.Key,
            kvp => (IReadOnlyList<LeafTransitionLog>)kvp.Value
                .Select(inner => new LeafTransitionLog(inner.Key, inner.Value))
                .OrderBy(l => l.LeafId)
                .ToList());

        var factTimelines = factPoints.ToDictionary(
            kvp => kvp.Key,
            kvp => (IReadOnlyList<FactSnapshotPoint>)kvp.Value);

        return new AlertReplayResult(windowStart, windowEnd, events)
        {
            LeafTransitionsByRule = leafTransitionsByRule,
            FactTimelines = factTimelines,
        };
    }

    /// <summary>
    /// Keyset-pages the window's glucose rows in ascending time order and yields the canonical
    /// stream page by page.
    /// </summary>
    /// <remarks>
    /// Canonical selection resolves a winner per aligned 5-minute bucket, so a bucket split
    /// across a page boundary is held back and re-selected together with the next page's head.
    /// Without that overlap two concurrent CGMs could each win their half of the same bucket and
    /// the tick loop would see a reading whole-window selection drops. Selection is otherwise
    /// bucket-local — a stream's priority relative to any other stream does not depend on which
    /// further streams happen to be in the same batch — so per-page selection over complete
    /// buckets yields exactly the whole-window result, provided each bucket fits within a page.
    /// A bucket wider than a page is flushed and selected per page instead of held back (see
    /// <see cref="Configuration.AlertEvaluationOptions.ReplayGlucosePageSize"/>).
    /// </remarks>
    private async IAsyncEnumerable<SensorGlucose> StreamCanonicalReadingsAsync(
        DateTime windowStart,
        DateTime windowEnd,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var pageSize = Math.Max(1, evaluationOptions.Value.ReplayGlucosePageSize);
        DateTime? afterTimestamp = null;
        Guid? afterId = null;
        var heldBack = new List<SensorGlucose>();

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var fetched = (await glucoseRepository.GetAsync(
                from: windowStart, to: windowEnd, device: null, source: null,
                limit: pageSize, offset: 0, descending: false, nativeOnly: false,
                afterTimestamp: afterTimestamp, afterId: afterId, ct: ct)).ToList();

            // A short page is the last one.
            var isFinalPage = fetched.Count < pageSize;
            if (fetched.Count > 0)
            {
                var last = fetched[^1];
                // Every non-empty page must end strictly past the cursor it was fetched with. A
                // fetch that ignores the cursor returns the same full page forever, and the loop
                // would never terminate; fail loudly instead of spinning.
                if (afterTimestamp is { } cursorTimestamp && afterId is { } cursorId
                    && (last.Timestamp < cursorTimestamp
                        || (last.Timestamp == cursorTimestamp && last.Id.CompareTo(cursorId) <= 0)))
                {
                    throw new InvalidOperationException(
                        "The glucose fetch returned a page that did not advance past the keyset " +
                        "cursor; replay paging cannot make progress.");
                }

                afterTimestamp = last.Timestamp;
                afterId = last.Id;
            }

            var batch = fetched;
            if (heldBack.Count > 0)
            {
                // Held-back rows precede the fetched page: they came from the previous page's
                // tail, and the keyset cursor only ever seeks forward.
                heldBack.AddRange(fetched);
                batch = heldBack;
                heldBack = [];
            }

            if (isFinalPage)
            {
                foreach (var reading in await SelectCanonicalAsync(batch, ct))
                {
                    yield return reading;
                }
                yield break;
            }

            var trailingBucket = BucketOf(batch[^1]);
            var completeCount = batch.Count;
            while (completeCount > 0 && BucketOf(batch[completeCount - 1]) == trailingBucket)
            {
                completeCount--;
            }

            if (completeCount == 0)
            {
                // A single bucket holding more rows than one page. Holding it back until it ends
                // would grow the resident set by a page per iteration, so it is flushed and
                // selected per page instead — the memory bound wins over exact bucket-winner
                // resolution on a shape only a bulk backfill or duplicate import produces.
                foreach (var reading in await SelectCanonicalAsync(batch, ct))
                {
                    yield return reading;
                }
                continue;
            }

            heldBack.AddRange(batch.Skip(completeCount));
            foreach (var reading in await SelectCanonicalAsync(batch.Take(completeCount).ToList(), ct))
            {
                yield return reading;
            }
        }
    }

    /// <summary>
    /// Canonical selection over one batch, re-sorted by timestamp. Selection preserves input
    /// order and the repository returns ascending rows, so the sort is a no-op on well-ordered
    /// input; it holds the ascending invariant the tick-loop fold depends on.
    /// </summary>
    private async Task<IEnumerable<SensorGlucose>> SelectCanonicalAsync(
        IReadOnlyList<SensorGlucose> batch, CancellationToken ct)
    {
        if (batch.Count == 0) return [];
        return (await canonicalGlucose.SelectAsync(batch, ct)).OrderBy(r => r.Timestamp);
    }

    private static long BucketOf(SensorGlucose reading) =>
        reading.Timestamp.Ticks / CanonicalGlucoseStream.BucketSize.Ticks;

    /// <summary>
    /// One-reading-lookahead cursor over the paged canonical stream. The tick loop needs the most
    /// recent reading at-or-before each tick, which a left-to-right fold over ascending readings
    /// gets from the current reading plus a peek at the next one — so no reading older than the
    /// playhead has to stay resident. <see cref="Current"/> is seeded with the first reading (as
    /// the pre-paging list walk did, starting at index 0) and never advances past the last, so a
    /// window whose readings all post-date a tick reports no reading for it, and ticks after the
    /// final reading keep seeing that reading.
    /// </summary>
    private sealed class ReadingCursor : IAsyncDisposable
    {
        private readonly IAsyncEnumerator<SensorGlucose> _source;
        private SensorGlucose? _next;
        private bool _exhausted;

        private ReadingCursor(IAsyncEnumerator<SensorGlucose> source) => _source = source;

        public SensorGlucose? Current { get; private set; }

        public static async Task<ReadingCursor> CreateAsync(
            IAsyncEnumerable<SensorGlucose> source, CancellationToken ct)
        {
            var cursor = new ReadingCursor(source.GetAsyncEnumerator(ct));
            try
            {
                cursor.Current = await cursor.PullAsync();
                cursor._next = await cursor.PullAsync();
            }
            catch
            {
                // Seeding owns the enumerator before the caller's `await using` does.
                await cursor.DisposeAsync();
                throw;
            }
            return cursor;
        }

        /// <summary>Advances <see cref="Current"/> to the last reading at-or-before <paramref name="tick"/>.</summary>
        public async Task AdvanceToAsync(DateTime tick)
        {
            while (_next is { } next && next.Timestamp <= tick)
            {
                Current = next;
                _next = await PullAsync();
            }
        }

        private async Task<SensorGlucose?> PullAsync()
        {
            if (_exhausted) return null;
            if (await _source.MoveNextAsync()) return _source.Current;
            _exhausted = true;
            return null;
        }

        public ValueTask DisposeAsync() => _source.DisposeAsync();
    }

    /// <summary>
    /// Compiled binding for one <see cref="ReplayFactAttribute"/>-tagged property: a
    /// pre-resolved getter plus the projection rule that turns the raw property value into
    /// the decimal wire value. Built once via reflection and reused across every replay tick.
    /// </summary>
    private sealed record FactBinding(
        string Key,
        int Decimals,
        ReplayFactConversion Conversion,
        Func<SensorContext, object?> Getter);

    private static readonly IReadOnlyList<FactBinding> FactBindings = DiscoverFactBindings();

    /// <summary>
    /// Scans <see cref="SensorContext"/> for <see cref="ReplayFactAttribute"/>-tagged
    /// properties at type-load time so adding a new fact is a one-line change on the model
    /// (drop the attribute) — replay surfaces it automatically with no parallel registry.
    /// </summary>
    private static IReadOnlyList<FactBinding> DiscoverFactBindings()
    {
        var ctxParam = System.Linq.Expressions.Expression.Parameter(typeof(SensorContext), "ctx");
        var bindings = new List<FactBinding>();
        foreach (var prop in typeof(SensorContext).GetProperties())
        {
            var attr = prop.GetCustomAttributes(typeof(ReplayFactAttribute), inherit: false)
                .OfType<ReplayFactAttribute>()
                .FirstOrDefault();
            if (attr is null) continue;

            var access = System.Linq.Expressions.Expression.Property(ctxParam, prop);
            var boxed = System.Linq.Expressions.Expression.Convert(access, typeof(object));
            var getter = System.Linq.Expressions.Expression
                .Lambda<Func<SensorContext, object?>>(boxed, ctxParam)
                .Compile();
            bindings.Add(new FactBinding(attr.Key, attr.Decimals, attr.Conversion, getter));
        }
        return bindings;
    }

    /// <summary>
    /// Reads the attribute-declared facts off <paramref name="ctx"/> and pushes a point onto
    /// the matching timeline whenever the rounded display value differs from the previous emit
    /// (or when this is the first observation of the fact). Rounding precision per fact comes
    /// from <see cref="ReplayFactAttribute.Decimals"/> on the source property, so the FE never
    /// sees jitter the user can't perceive.
    /// </summary>
    private static void CaptureFactSnapshots(
        SensorContext ctx,
        DateTime tickUtc,
        Dictionary<string, decimal> prev,
        Dictionary<string, List<FactSnapshotPoint>> points)
    {
        var tickMs = new DateTimeOffset(tickUtc).ToUnixTimeMilliseconds();

        foreach (var binding in FactBindings)
        {
            var raw = binding.Getter(ctx);
            if (raw is null) continue;

            var projected = binding.Conversion switch
            {
                // Direct takes decimal properties; bool flags are projected as 0/1.
                ReplayFactConversion.Direct => raw is bool flag ? (flag ? 1m : 0m) : (decimal)raw,
                ReplayFactConversion.MinutesSinceNow => (decimal)(tickUtc - (DateTime)raw).TotalMinutes,
                ReplayFactConversion.HoursSinceNow => (decimal)(tickUtc - (DateTime)raw).TotalHours,
                ReplayFactConversion.DaysSinceNow => (decimal)(tickUtc - (DateTime)raw).TotalDays,
                _ => (decimal?)null,
            };
            if (projected is not { } value) continue;

            var rounded = Math.Round(value, binding.Decimals, MidpointRounding.AwayFromZero);
            if (prev.TryGetValue(binding.Key, out var previous) && previous == rounded) continue;
            if (!points.TryGetValue(binding.Key, out var list))
            {
                list = new List<FactSnapshotPoint>();
                points[binding.Key] = list;
            }
            list.Add(new FactSnapshotPoint(tickMs, rounded));
            prev[binding.Key] = rounded;
        }
    }

    /// <summary>
    /// Resolves the requested window in UTC. <paramref name="localDate"/> null → rolling 24 h
    /// ending at "now" (timezone irrelevant for a rolling window — both endpoints are absolute
    /// UTC instants). Set → that calendar day in <paramref name="timezone"/>,
    /// midnight-to-midnight, converted to UTC. On DST-transition days the resulting UTC
    /// window is 23 or 25 hours wide rather than exactly 24.
    /// </summary>
    private static (DateTime Start, DateTime End) ResolveWindow(
        DateOnly? localDate, string? timezone, DateTime? fromUtc, DateTime? toUtc)
    {
        // Absolute UTC range wins when both endpoints are provided. The caller is
        // responsible for ordering — we swap rather than reject so a from > to range from
        // a date-only client (where the calendar picker happened to pass the day boundary)
        // still produces a usable window.
        if (fromUtc is { } from && toUtc is { } to)
        {
            var start = DateTime.SpecifyKind(from, DateTimeKind.Utc);
            var end = DateTime.SpecifyKind(to, DateTimeKind.Utc);
            return start <= end ? (start, end) : (end, start);
        }

        if (localDate is null)
        {
            var now = DateTime.UtcNow;
            return (now.AddHours(-24), now);
        }

        var tz = TimeZoneHelper.GetTimeZoneInfoFromId(timezone);

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
                    Composite: JsonSerializer.Deserialize<CompositeCondition>(rule.ConditionParams, EvaluatorJson.Options)),
                AlertConditionType.Not => new ConditionNode("not",
                    Not: JsonSerializer.Deserialize<NotCondition>(rule.ConditionParams, EvaluatorJson.Options)),
                AlertConditionType.Sustained => new ConditionNode("sustained",
                    Sustained: JsonSerializer.Deserialize<SustainedCondition>(rule.ConditionParams, EvaluatorJson.Options)),
                AlertConditionType.AlertState => new ConditionNode("alert_state",
                    AlertState: JsonSerializer.Deserialize<AlertStateCondition>(rule.ConditionParams, EvaluatorJson.Options)),
                AlertConditionType.Threshold => new ConditionNode("threshold",
                    Threshold: JsonSerializer.Deserialize<ThresholdCondition>(rule.ConditionParams, EvaluatorJson.Options)),
                AlertConditionType.RateOfChange => new ConditionNode("rate_of_change",
                    RateOfChange: JsonSerializer.Deserialize<RateOfChangeCondition>(rule.ConditionParams, EvaluatorJson.Options)),
                AlertConditionType.Staleness => new ConditionNode("staleness",
                    Staleness: JsonSerializer.Deserialize<StalenessCondition>(rule.ConditionParams, EvaluatorJson.Options)),
                AlertConditionType.Predicted => new ConditionNode("predicted",
                    Predicted: JsonSerializer.Deserialize<PredictedCondition>(rule.ConditionParams, EvaluatorJson.Options)),
                AlertConditionType.Trend => new ConditionNode("trend",
                    Trend: JsonSerializer.Deserialize<TrendCondition>(rule.ConditionParams, EvaluatorJson.Options)),
                AlertConditionType.TimeOfDay => new ConditionNode("time_of_day",
                    TimeOfDay: JsonSerializer.Deserialize<TimeOfDayCondition>(rule.ConditionParams, EvaluatorJson.Options)),
                AlertConditionType.Iob => new ConditionNode("iob",
                    Iob: JsonSerializer.Deserialize<IobCondition>(rule.ConditionParams, EvaluatorJson.Options)),
                AlertConditionType.Cob => new ConditionNode("cob",
                    Cob: JsonSerializer.Deserialize<CobCondition>(rule.ConditionParams, EvaluatorJson.Options)),
                AlertConditionType.Reservoir => new ConditionNode("reservoir",
                    Reservoir: JsonSerializer.Deserialize<ReservoirCondition>(rule.ConditionParams, EvaluatorJson.Options)),
                AlertConditionType.SiteAge => new ConditionNode("site_age",
                    SiteAge: JsonSerializer.Deserialize<SiteAgeCondition>(rule.ConditionParams, EvaluatorJson.Options)),
                AlertConditionType.SensorAge => new ConditionNode("sensor_age",
                    SensorAge: JsonSerializer.Deserialize<SensorAgeCondition>(rule.ConditionParams, EvaluatorJson.Options)),
                AlertConditionType.LoopStale => new ConditionNode("loop_stale",
                    LoopStale: JsonSerializer.Deserialize<LoopStaleCondition>(rule.ConditionParams, EvaluatorJson.Options)),
                AlertConditionType.LoopEnactionStale => new ConditionNode("loop_enaction_stale",
                    LoopEnactionStale: JsonSerializer.Deserialize<LoopEnactionStaleCondition>(rule.ConditionParams, EvaluatorJson.Options)),
                AlertConditionType.PumpSuspended => new ConditionNode("pump_suspended",
                    PumpSuspended: JsonSerializer.Deserialize<PumpSuspendedCondition>(rule.ConditionParams, EvaluatorJson.Options)),
                AlertConditionType.PumpBattery => new ConditionNode("pump_battery",
                    PumpBattery: JsonSerializer.Deserialize<PumpBatteryCondition>(rule.ConditionParams, EvaluatorJson.Options)),
                AlertConditionType.TempBasal => new ConditionNode("temp_basal",
                    TempBasal: JsonSerializer.Deserialize<TempBasalCondition>(rule.ConditionParams, EvaluatorJson.Options)),
                AlertConditionType.UploaderBattery => new ConditionNode("uploader_battery",
                    UploaderBattery: JsonSerializer.Deserialize<UploaderBatteryCondition>(rule.ConditionParams, EvaluatorJson.Options)),
                AlertConditionType.OverrideActive => new ConditionNode("override_active",
                    OverrideActive: JsonSerializer.Deserialize<OverrideActiveCondition>(rule.ConditionParams, EvaluatorJson.Options)),
                AlertConditionType.SensitivityRatio => new ConditionNode("sensitivity_ratio",
                    SensitivityRatio: JsonSerializer.Deserialize<SensitivityRatioCondition>(rule.ConditionParams, EvaluatorJson.Options)),
                AlertConditionType.DoNotDisturb => new ConditionNode("do_not_disturb",
                    DoNotDisturb: JsonSerializer.Deserialize<DoNotDisturbCondition>(rule.ConditionParams, EvaluatorJson.Options)),
                AlertConditionType.GlucoseBucket => new ConditionNode("glucose_bucket",
                    GlucoseBucket: JsonSerializer.Deserialize<GlucoseBucketCondition>(rule.ConditionParams, EvaluatorJson.Options)),
                AlertConditionType.TimeSinceLastCarb => new ConditionNode("time_since_last_carb",
                    TimeSinceLastCarb: JsonSerializer.Deserialize<TimeSinceLastCarbCondition>(rule.ConditionParams, EvaluatorJson.Options)),
                AlertConditionType.TimeSinceLastBolus => new ConditionNode("time_since_last_bolus",
                    TimeSinceLastBolus: JsonSerializer.Deserialize<TimeSinceLastBolusCondition>(rule.ConditionParams, EvaluatorJson.Options)),
                AlertConditionType.DayOfWeek => new ConditionNode("day_of_week",
                    DayOfWeek: JsonSerializer.Deserialize<DayOfWeekCondition>(rule.ConditionParams, EvaluatorJson.Options)),
                AlertConditionType.PumpState => new ConditionNode("pump_state",
                    PumpState: JsonSerializer.Deserialize<PumpStateCondition>(rule.ConditionParams, EvaluatorJson.Options)),
                AlertConditionType.StateSpanActive => new ConditionNode("state_span_active",
                    StateSpanActive: JsonSerializer.Deserialize<StateSpanActiveCondition>(rule.ConditionParams, EvaluatorJson.Options)),
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
    /// Builds a self-contained <see cref="ServiceProvider"/> for the replay run by reusing the
    /// live <see cref="ServiceRegistrationExtensions.AddAlertEvaluators"/> registration with
    /// only the <see cref="TimeProvider"/> and <see cref="IConditionTimerStore"/> swapped for
    /// replay-local instances. Sourcing the evaluator list from DI ensures replay can never
    /// drift behind the live engine when new condition evaluators are added.
    /// </summary>
    /// <remarks>
    /// The returned provider owns the lifetime of the registered evaluators and registry — the
    /// caller must dispose it (replay uses <c>await using</c>). Recursive evaluators
    /// (Composite/Not/Sustained) take <see cref="IServiceProvider"/> in their constructors and
    /// resolve <see cref="ConditionEvaluatorRegistry"/> lazily, which is satisfied by the
    /// container we build here.
    /// </remarks>
    internal static ServiceProvider BuildReplayServices(
        IConditionTimerStore timerStore, TimeProvider time)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(time);
        services.AddSingleton(timerStore);
        services.AddAlertEvaluators();
        services.AddSingleton<ConditionEvaluatorRegistry>();
        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Topologically sort rules so each rule appears after every rule it depends on via
    /// <c>alert_state</c>. Falls back to insertion order on cycle (cycles are blocked at
    /// Layers <paramref name="ruleOverride"/> onto <paramref name="stored"/>: when its Id
    /// matches an existing rule the override replaces it; otherwise the override is appended
    /// (with a synthesised id so any <c>alert_state</c> references the editor seeded resolve
    /// against the override rather than against a non-existent rule). The original list is
    /// returned unchanged when <paramref name="ruleOverride"/> is null.
    /// </summary>
    private static IReadOnlyList<AlertRuleSnapshot> ApplyOverride(
        IReadOnlyList<AlertRuleSnapshot> stored,
        ReplayRuleOverride? ruleOverride,
        Guid tenantId)
    {
        if (ruleOverride is null) return stored;

        var overrideId = ruleOverride.Id ?? Guid.CreateVersion7();
        var overrideSnapshot = new AlertRuleSnapshot(
            Id: overrideId,
            TenantId: tenantId,
            Name: ruleOverride.Name,
            ConditionType: ruleOverride.ConditionType,
            ConditionParams: ruleOverride.ConditionParams,
            Severity: ruleOverride.Severity,
            ClientConfiguration: "{}",
            SortOrder: 0,
            AutoResolveEnabled: ruleOverride.AutoResolveEnabled,
            AutoResolveParams: ruleOverride.AutoResolveParams,
            AllowThroughDnd: ruleOverride.AllowThroughDnd);

        var matchedExisting = ruleOverride.Id.HasValue
            && stored.Any(r => r.Id == ruleOverride.Id.Value);
        if (matchedExisting)
        {
            return stored
                .Select(r => r.Id == overrideId ? overrideSnapshot : r)
                .ToList();
        }

        var combined = new List<AlertRuleSnapshot>(stored.Count + 1);
        combined.AddRange(stored);
        combined.Add(overrideSnapshot);
        return combined;
    }

    /// <summary>
    /// Topo-sort by alert_state edges so a rule's parents have been evaluated for the same
    /// tick before the rule itself runs. Cycles short-circuit to insertion order (cycles are
    /// already prevented at write time, but defence-in-depth keeps replay alive on stale data).
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
