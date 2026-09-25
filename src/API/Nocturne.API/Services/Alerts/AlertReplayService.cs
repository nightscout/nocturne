using System.Runtime.CompilerServices;
using Microsoft.Extensions.Options;
using Nocturne.API.Configuration;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.Alerts;

/// <summary>
/// Replays the tenant's enabled rule set over a historical glucose window. This service builds
/// each 5-minute tick's context as of that instant, through the same
/// <see cref="ISensorContextEnricher"/> the live engine uses
/// (<see cref="ISensorContextEnricher.EnrichAsOfAsync"/>), resolves Do Not Disturb per tick, and
/// captures the fact timelines. The <see cref="IAlertReplayEngine"/> the <c>Alerts:Engine</c> flag
/// selects evaluates the rules over those ticks (docs/alerts/engine-semantics.md §8).
/// </summary>
internal sealed class AlertReplayService(
    IAlertRepository alertRepository,
    ISensorGlucoseRepository glucoseRepository,
    ICanonicalGlucoseService canonicalGlucose,
    ISensorContextEnricher enricher,
    ITenantAccessor tenantAccessor,
    IOptions<AlertEvaluationOptions> evaluationOptions,
    IAlertReplayEngine replayEngine)
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

        // Replay walks the canonical stream — the same series the live engine alarms on. The
        // window is keyset-paged and consumed by the tick loop as a left-to-right fold, so the
        // readings are never all resident; the per-tick contexts built from them are.
        await using var readings = await ReadingCursor.CreateAsync(
            StreamCanonicalReadingsAsync(windowStart, windowEnd, ct), ct);

        // Scoped DND (ADR 0004 D5): the tenant's windows received by the replay's end, resolved
        // per tick with WasActiveAt (receipt-gated) so replay reproduces what the live engine
        // saw and never rewrites the offline-authoring gap. Both the suppression scopes and the
        // do_not_disturb leaf's ActiveDoNotDisturb snapshot re-source from these windows per
        // tick. Scheduled DND is not reconstructible historically (the schedule row keeps no
        // change history), so — as before D5 — replay considers windows only.
        var dndWindows = await alertRepository.GetDndWindowsAsOfAsync(tenantId, windowEnd, ct);

        // Per-tick fact snapshots (site age, IOB, temp basal rate, etc.). Keyed by snake_case
        // fact name; same compression as the leaf log — emit baseline + on rounded-value flip.
        // The previous-value map stores the rounded value to keep the change-detection cheap
        // and stable against floating-point jitter.
        var factPrev = new Dictionary<string, decimal>();
        var factPoints = new Dictionary<string, List<FactSnapshotPoint>>();
        var ticks = new List<AlertReplayTick>();

        for (var tick = windowStart; tick < windowEnd; tick += TickInterval)
        {
            ct.ThrowIfCancellationRequested();

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
                // A tick before the window's first reading reads as fresh rather than as never
                // having had one (engine-semantics.md §8).
                LastReadingAt = hasReadingForTick ? current!.Timestamp : tick,
                ActiveAlerts = new Dictionary<Guid, ActiveAlertSnapshot>(),
            };

            // Pin every fact in the per-tick context to `tick` — APS / pump / uploader /
            // state-span / temp-basal / device-event repos all support an as-of cutoff.
            var tickUtc = DateTime.SpecifyKind(tick, DateTimeKind.Utc);
            // Receipt-gated, and through the same resolver the live enricher uses so the two
            // cannot disagree about how a window resolves. No scheduled projection is passed:
            // a recurring schedule's past state is not reconstructible from the settings row.
            var tickDnd = DndWindowResolver.Resolve(dndWindows, tickUtc, receiptGated: true);
            var enrichedBase = (await enricher.EnrichAsOfAsync(
                baseContext, rules, tenantId, tickUtc, ct))
                with
            {
                ActiveDndScopes = tickDnd.Scopes,
                ActiveDoNotDisturb = tickDnd.ActiveDoNotDisturb,
            };

            CaptureFactSnapshots(enrichedBase, tickUtc, factPrev, factPoints);

            // The gate live delivery applies when an excursion opens: a non-Critical rule whose
            // class an active scope covers is recorded as suppressed.
            var suppressed = rules
                .Where(r => DndSuppressionGate.IsSuppressed(r, tickDnd.Scopes))
                .Select(r => r.Id)
                .ToHashSet();
            ticks.Add(new AlertReplayTick(tickUtc, enrichedBase, suppressed));
        }

        var run = await replayEngine.ReplayAsync(new AlertReplayInput(rules, ticks), ct);

        var byId = rules.ToDictionary(r => r.Id);
        var events = run.Events
            .Where(e => e.Kind != AlertReplayTransition.Cleared)
            .Select(e =>
            {
                var rule = byId[e.RuleId];
                return new AlertReplayEvent(
                    DateTime.SpecifyKind(e.At, DateTimeKind.Utc), rule.Id, rule.Name, rule.Severity, EventKind(e.Kind));
            })
            .ToList();

        var factTimelines = factPoints.ToDictionary(
            kvp => kvp.Key,
            kvp => (IReadOnlyList<FactSnapshotPoint>)kvp.Value);

        return new AlertReplayResult(windowStart, windowEnd, events)
        {
            LeafTransitionsByRule = run.LeafTransitions.ToDictionary(l => l.RuleId, l => l.Leaves),
            FactTimelines = factTimelines,
        };
    }

    private static AlertReplayEventKind EventKind(AlertReplayTransition transition) => transition switch
    {
        AlertReplayTransition.Fired => AlertReplayEventKind.Fired,
        AlertReplayTransition.SuppressedByDnd => AlertReplayEventKind.SuppressedByDnd,
        AlertReplayTransition.AutoResolved => AlertReplayEventKind.AutoResolved,
        _ => throw new ArgumentOutOfRangeException(nameof(transition), transition, null),
    };

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
}
