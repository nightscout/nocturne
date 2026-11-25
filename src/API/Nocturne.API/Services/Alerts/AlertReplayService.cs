using System.Runtime.CompilerServices;
using Microsoft.Extensions.Options;
using Nocturne.Core.Alerts.Native;
using Nocturne.API.Configuration;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.Alerts;

/// <summary>
/// Replays the tenant's enabled rule set over a historical glucose window. This service builds a
/// tick at each reading and, in a gap between readings, a sample of the other facts every
/// <see cref="SampleInterval"/>, each with its context as of that instant, through the same
/// <see cref="ISensorContextEnricher"/> the live engine uses
/// (<see cref="ISensorContextEnricher.EnrichAsOfAsync"/>), resolves Do Not Disturb per tick, and
/// captures the fact timelines. The <see cref="IAlertReplayEngine"/> steps the rules over those
/// ticks and the instants between them (docs/alerts/engine-semantics.md §8).
/// </summary>
internal sealed class AlertReplayService(
    IAlertRepository alertRepository,
    IDndWindowRepository dndWindowRepository,
    ISensorGlucoseRepository glucoseRepository,
    ISensorContextEnricher enricher,
    ITenantAccessor tenantAccessor,
    IOptions<AlertEvaluationOptions> evaluationOptions,
    IAlertReplayEngine replayEngine,
    ImportedHistory importedHistory,
    TimeProvider timeProvider)
    : IAlertReplayService
{
    /// <summary>
    /// How often a gap between readings samples the other facts. Their writes are not replayed
    /// one by one, so a fact that moves inside a gap is seen at most this late.
    /// </summary>
    private static readonly TimeSpan SampleInterval = TimeSpan.FromMinutes(5);

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
    /// Replayed ahead of each searched day and not reported. Replay starts every rule from rest,
    /// so a condition already true at the start of a window fires on its first tick; the lead-in
    /// moves that edge out of the day, where the day before reports the real one. It is longer
    /// than any "for at least" duration the editor offers.
    /// </summary>
    private static readonly TimeSpan FiringSearchLeadIn = TimeSpan.FromHours(3);

    private const int MaxFiringsReturned = 50;

    public async Task<AlertFiringSearchResult> FindFiringsAsync(
        ReplayRuleOverride ruleOverride, bool lastDayOnly, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ruleOverride);

        var options = evaluationOptions.Value;
        var utcNow = timeProvider.GetUtcNow().UtcDateTime;
        // On a sample boundary, so a repeated search covers the same days.
        var searchedTo = new DateTime(
            utcNow.Ticks - utcNow.Ticks % SampleInterval.Ticks, DateTimeKind.Utc);

        var tenantId = tenantAccessor.TenantId;
        if (tenantId == Guid.Empty)
        {
            return new AlertFiringSearchResult(searchedTo, searchedTo, false, 0, []);
        }

        var candidate = await OverrideSnapshotAsync(ruleOverride, tenantId, ct);
        IReadOnlyList<AlertRuleSnapshot> rules = RuleDataNeeds.Walk([candidate]).NeedsActiveAlerts
            ? Layer(await alertRepository.GetEnabledRulesAsync(ct), candidate)
            : [candidate];

        var started = timeProvider.GetTimestamp();
        var firings = new List<AlertFiring>();
        var hasReadings = false;
        var searchedFrom = searchedTo;

        var days = lastDayOnly ? 1 : Math.Max(1, options.FiringSearchDays);
        var arrivals = await ArrivalsAsync(searchedTo.AddDays(-days) - FiringSearchLeadIn, searchedTo, ct);
        for (var day = 0; day < days; day++)
        {
            var dayEnd = searchedTo.AddDays(-day);
            var dayStart = dayEnd.AddDays(-1);

            var series = await BuildTicksAsync(
                rules, dayStart - FiringSearchLeadIn, dayEnd, arrivals, captureFacts: false, ct);
            var run = await replayEngine.ReplayAsync(
                new AlertReplayInput(rules, series.Ticks, Until: dayEnd), ct);

            firings.AddRange(run.Events
                .Where(e => e.RuleId == candidate.Id
                            && e.At >= dayStart
                            && e.Kind is AlertReplayTransition.Fired or AlertReplayTransition.SuppressedByDnd)
                .OrderByDescending(e => e.At)
                .Select(e => new AlertFiring(DateTime.SpecifyKind(e.At, DateTimeKind.Utc), EventKind(e.Kind))));
            hasReadings |= series.HasReadings;
            searchedFrom = dayStart;

            if (timeProvider.GetElapsedTime(started) > options.FiringSearchBudget)
                break;
        }

        return new AlertFiringSearchResult(
            searchedFrom, searchedTo, hasReadings, firings.Count,
            firings.Take(MaxFiringsReturned).ToList());
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
            var now = timeProvider.GetUtcNow().UtcDateTime;
            return new AlertReplayResult(now, now, []);
        }

        var stored = await alertRepository.GetEnabledRulesAsync(ct);
        var rules = ruleOverride is null
            ? stored
            : Layer(stored, await OverrideSnapshotAsync(ruleOverride, tenantId, ct));
        if (rules.Count == 0)
        {
            return new AlertReplayResult(windowStart, windowEnd, []);
        }

        var series = await BuildTicksAsync(
            rules, windowStart, windowEnd, await ArrivalsAsync(windowStart, windowEnd, ct), captureFacts: true, ct);

        var run = await replayEngine.ReplayAsync(
            new AlertReplayInput(rules, series.Ticks, Until: windowEnd), ct);

        var byId = rules.ToDictionary(r => r.Id);
        var events = run.Events
            .Where(e => e.Kind is AlertReplayTransition.Fired
                or AlertReplayTransition.SuppressedByDnd
                or AlertReplayTransition.AutoResolved)
            .Select(e =>
            {
                var rule = byId[e.RuleId];
                return new AlertReplayEvent(
                    DateTime.SpecifyKind(e.At, DateTimeKind.Utc), rule.Id, rule.Name, rule.Severity, EventKind(e.Kind));
            })
            .ToList();

        var factTimelines = series.FactPoints.ToDictionary(
            kvp => kvp.Key,
            kvp => (IReadOnlyList<FactSnapshotPoint>)kvp.Value);

        return new AlertReplayResult(windowStart, windowEnd, events)
        {
            LeafTransitionsByRule = run.LeafTransitions.ToDictionary(l => l.RuleId, l => l.Leaves),
            FactTimelines = factTimelines,
        };
    }

    /// <summary>The ticks of one replay window, each enriched as of its own instant.</summary>
    /// <param name="FactPoints">Empty unless the fact timelines were asked for.</param>
    /// <param name="HasReadings">Whether any tick had a glucose reading at or before it.</param>
    private sealed record TickSeries(
        IReadOnlyList<AlertReplayTick> Ticks,
        Dictionary<string, List<FactSnapshotPoint>> FactPoints,
        bool HasReadings);

    /// <summary>
    /// How replay tells the window's live arrivals from imported history: once per replay request,
    /// not per tick.
    /// </summary>
    private async Task<Arrivals> ArrivalsAsync(DateTime windowStart, DateTime windowEnd, CancellationToken ct) =>
        new(await importedHistory.InAsync(windowStart - ReadingTrend.MaxGap, windowEnd, ct));

    /// <summary>
    /// Where live evaluated each reading (docs/alerts/engine-semantics.md §8). A reading dated
    /// before its connection first sent anything (<paramref name="Imported"/>) is imported history, as
    /// is one stored further before its timestamp than the clock tolerance. Every other reading is
    /// a live arrival, however late: live evaluated it when it was stored, and no earlier than its
    /// timestamp, since a tick is never before its reading.
    /// </summary>
    private sealed record Arrivals(IReadOnlySet<Guid> Imported)
    {
        public bool Live(SensorGlucose reading) =>
            !Imported.Contains(reading.Id)
            && reading.CreatedAt >= reading.Timestamp - RustAlertEngine.ClockTolerance();

        public DateTime EvaluatedAt(SensorGlucose reading) =>
            Live(reading) && reading.CreatedAt > reading.Timestamp ? reading.CreatedAt : reading.Timestamp;
    }

    private async Task<TickSeries> BuildTicksAsync(
        IReadOnlyList<AlertRuleSnapshot> rules,
        DateTime windowStart,
        DateTime windowEnd,
        Arrivals arrivals,
        bool captureFacts,
        CancellationToken ct)
    {
        // Replay walks the canonical stream — the same series the live engine alarms on. The
        // window is keyset-paged and consumed by the tick loop as a left-to-right fold, so the
        // readings are never all resident; the per-tick contexts built from them are.
        await using var readings = await ReadingCursor.CreateAsync(
            StreamCanonicalReadingsAsync(windowStart - ReadingTrend.MaxGap, windowEnd, ct),
            StreamHiddenReadingsAsync(windowStart - ReadingTrend.MaxGap, windowEnd, ct),
            windowStart, ct);

        // Scoped DND (ADR 0004 D5): the tenant's windows received by the replay's end, resolved
        // per tick with WasActiveAt (receipt-gated) so replay reproduces what the live engine
        // saw and never rewrites the offline-authoring gap. Both the suppression scopes and the
        // do_not_disturb leaf's ActiveDoNotDisturb snapshot re-source from these windows per
        // tick. Scheduled DND is not reconstructible historically (the schedule row keeps no
        // change history), so — as before D5 — replay considers windows only.
        var dndWindows = await dndWindowRepository.GetReceivedByAsync(windowEnd, ct);

        // Per-tick fact snapshots (site age, IOB, temp basal rate, etc.). Keyed by snake_case
        // fact name; same compression as the leaf log — emit baseline + on rounded-value flip.
        // The previous-value map stores the rounded value to keep the change-detection cheap
        // and stable against floating-point jitter.
        var factPrev = new Dictionary<string, decimal>();
        var factPoints = new Dictionary<string, List<FactSnapshotPoint>>();
        var ticks = new List<AlertReplayTick>();
        var hasReadings = false;

        // Readings the cursor has walked past, waiting for the instant live evaluated them.
        var arriving = new PriorityQueue<(SensorGlucose Reading, ReadingTrend Trend), DateTime>();
        (SensorGlucose Reading, ReadingTrend Trend)? known = null;
        // The newest known reading with a value, and the first with none after it, as live's look
        // back finds them (AlertReadings.LookBackAsync), so a reading with no value reads as live
        // reads it.
        SensorGlucose? usable = null;
        DateTime? valuelessSince = null;

        // The window's start stands for the evaluation of the last reading before it, so every rule
        // is evaluated there; after it, a tick is a reading where live evaluated it, or a sample.
        var isReading = true;
        for (var tick = windowStart; tick < windowEnd;)
        {
            ct.ThrowIfCancellationRequested();

            // Walk the reading stream once across the whole replay rather than re-scanning per
            // tick. A reading is evaluated no earlier than its timestamp, so every reading
            // evaluated by `tick` has been walked past once the cursor reaches `tick`.
            await readings.AdvanceToAsync(tick);
            Enqueue(readings, arrivals, arriving);
            while (arriving.TryPeek(out var reading, out var evaluatedAt) && evaluatedAt <= tick)
            {
                arriving.Dequeue();
                if (reading.Reading.Timestamp <= AlertReadings.Bound(tick)
                    && (known is null || reading.Reading.Timestamp > known.Value.Reading.Timestamp))
                {
                    known = reading;
                    if (AlertReadings.HasValue(reading.Reading))
                        (usable, valuelessSince) = (reading.Reading, null);
                    else
                        valuelessSince ??= reading.Reading.Timestamp;
                }
            }

            var current = known?.Reading;
            var trend = known?.Trend ?? default;
            hasReadings |= current is not null;
            var valueless = current is not null && !AlertReadings.HasValue(current);

            var baseContext = new SensorContext
            {
                LatestValue = valueless ? null : current?.Mgdl.ToDecimal(),
                LatestTimestamp = current?.Timestamp,
                LatestReceivedAt = current is not null && arrivals.Live(current) ? current.CreatedAt : null,
                TrendRate = valueless ? null : trend.Rate,
                TrendBucket = valueless ? null : trend.Bucket,
                // A tick before the window's first reading reads as fresh rather than as never
                // having had one (engine-semantics.md §8). After a reading with no value it is the
                // last reading with one (CanonicalAlertEvaluator.ContextForValueless).
                LastReadingAt = valueless ? usable?.Timestamp ?? valuelessSince : current?.Timestamp ?? tick,
                SensorState = valueless ? new SensorStateSnapshot(SensorState.NoValue, valuelessSince) : null,
                LatestSource = current is null ? null : new ReadingSource(current.DataSource, current.Device),
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
                baseContext, rules, tickUtc, ct))
                with
            {
                ActiveDndScopes = tickDnd.Scopes,
                ActiveDoNotDisturb = tickDnd.ActiveDoNotDisturb,
            };

            if (captureFacts)
                CaptureFactSnapshots(enrichedBase, tickUtc, factPrev, factPoints);

            // The gate live delivery applies when an excursion opens: a non-Critical rule whose
            // class an active scope covers is recorded as suppressed.
            var suppressed = rules
                .Where(r => DndSuppressionGate.IsSuppressed(r, tickDnd.Scopes))
                .Select(r => r.Id)
                .ToHashSet();
            ticks.Add(new AlertReplayTick(tickUtc, enrichedBase, suppressed, isReading));

            var sample = tick + SampleInterval;
            var next = await NextEvaluationAsync(readings, arrivals, arriving);
            isReading = next <= sample;
            tick = isReading ? next!.Value : sample;
        }

        return new TickSeries(ticks, factPoints, hasReadings);
    }

    private static void Enqueue(
        ReadingCursor readings, Arrivals arrivals,
        PriorityQueue<(SensorGlucose Reading, ReadingTrend Trend), DateTime> arriving)
    {
        foreach (var advanced in readings.TakeAdvanced())
            arriving.Enqueue(advanced, arrivals.EvaluatedAt(advanced.Reading));
    }

    /// <summary>
    /// The next instant a reading is evaluated. A reading not yet walked past is evaluated no
    /// earlier than its timestamp, so the cursor walks on until none can come before the earliest
    /// one waiting.
    /// </summary>
    private static async Task<DateTime?> NextEvaluationAsync(
        ReadingCursor readings, Arrivals arrivals,
        PriorityQueue<(SensorGlucose Reading, ReadingTrend Trend), DateTime> arriving)
    {
        while (true)
        {
            DateTime? due = arriving.TryPeek(out _, out var at) ? at : null;
            if (readings.Upcoming is not { } upcoming || (due is { } d && d <= upcoming))
                return due;
            await readings.AdvanceToAsync(upcoming);
            Enqueue(readings, arrivals, arriving);
        }
    }

    private static AlertReplayEventKind EventKind(AlertReplayTransition transition) => transition switch
    {
        AlertReplayTransition.Fired => AlertReplayEventKind.Fired,
        AlertReplayTransition.SuppressedByDnd => AlertReplayEventKind.SuppressedByDnd,
        AlertReplayTransition.AutoResolved => AlertReplayEventKind.AutoResolved,
        _ => throw new ArgumentOutOfRangeException(nameof(transition), transition, null),
    };

    /// <summary>
    /// Keyset-pages the window's canonical glucose rows in ascending time order, a page of
    /// <see cref="Configuration.AlertEvaluationOptions.ReplayGlucosePageSize"/> at a time.
    /// </summary>
    private IAsyncEnumerable<SensorGlucose> StreamCanonicalReadingsAsync(
        DateTime windowStart, DateTime windowEnd, CancellationToken ct) =>
        KeysetPagesAsync(
            async (pageSize, afterTimestamp, afterId) => (IReadOnlyList<SensorGlucose>)(await glucoseRepository.GetAsync(
                from: windowStart, to: windowEnd, device: null, source: null,
                limit: pageSize, offset: 0, descending: false,
                afterTimestamp: afterTimestamp, afterId: afterId, ct: ct,
                streams: GlucoseStreams.Canonical)).ToList(),
            reading => reading,
            ct);

    /// <summary>
    /// Keyset-pages the window's glucose rows the canonical read leaves out, which only ever serve
    /// as the earlier reading of a derived <see cref="ReadingTrend"/>.
    /// </summary>
    private IAsyncEnumerable<(SensorGlucose Reading, Guid? DuplicateOf)> StreamHiddenReadingsAsync(
        DateTime windowStart, DateTime windowEnd, CancellationToken ct) =>
        KeysetPagesAsync(
            (pageSize, afterTimestamp, afterId) => glucoseRepository.GetHiddenAsync(
                windowStart, windowEnd, pageSize, afterTimestamp, afterId, ct),
            row => row.Reading,
            ct);

    private async IAsyncEnumerable<T> KeysetPagesAsync<T>(
        Func<int, DateTime?, Guid?, Task<IReadOnlyList<T>>> fetchPage,
        Func<T, SensorGlucose> readingOf,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var pageSize = Math.Max(1, evaluationOptions.Value.ReplayGlucosePageSize);
        DateTime? afterTimestamp = null;
        Guid? afterId = null;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var fetched = await fetchPage(pageSize, afterTimestamp, afterId);

            if (fetched.Count > 0)
            {
                var last = readingOf(fetched[^1]);
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

            foreach (var row in fetched)
                yield return row;

            // A short page is the last one.
            if (fetched.Count < pageSize)
                yield break;
        }
    }

    /// <summary>
    /// One-reading-lookahead cursor over the paged canonical stream, walked in timestamp order. Each
    /// reading of the window it walks past is handed out once by <see cref="TakeAdvanced"/>; the
    /// readings before the window start serve only as the earlier readings of a trend.
    /// </summary>
    /// <remarks>
    /// The earlier readings are those of the last <see cref="ReadingTrend.MaxGap"/> before the
    /// reading walked to, canonical and hidden, those before the window start included, so each
    /// reading derives its trend from the readings already loaded, by the rule live evaluation reads by.
    /// </remarks>
    private sealed class ReadingCursor : IAsyncDisposable
    {
        private readonly IAsyncEnumerator<SensorGlucose> _source;
        private readonly IAsyncEnumerator<(SensorGlucose Reading, Guid? DuplicateOf)> _hidden;
        private readonly List<(SensorGlucose Reading, Guid? DuplicateOf)> _earlier = [];
        private (SensorGlucose Reading, Guid? DuplicateOf)? _nextHidden;
        private SensorGlucose? _next;
        private bool _exhausted;
        private bool _hiddenExhausted;

        private ReadingCursor(
            IAsyncEnumerator<SensorGlucose> source,
            IAsyncEnumerator<(SensorGlucose Reading, Guid? DuplicateOf)> hidden)
        {
            _source = source;
            _hidden = hidden;
        }

        private readonly List<(SensorGlucose Reading, ReadingTrend Trend)> _advanced = [];

        private SensorGlucose? Current { get; set; }

        /// <summary>The timestamp of the first reading the cursor has not advanced to.</summary>
        public DateTime? Upcoming => _next?.Timestamp;

        /// <summary>
        /// The readings advanced to since the last call, each with the trend it derives from the
        /// readings before it.
        /// </summary>
        public List<(SensorGlucose Reading, ReadingTrend Trend)> TakeAdvanced()
        {
            var advanced = _advanced.ToList();
            _advanced.Clear();
            return advanced;
        }

        public static async Task<ReadingCursor> CreateAsync(
            IAsyncEnumerable<SensorGlucose> source,
            IAsyncEnumerable<(SensorGlucose Reading, Guid? DuplicateOf)> hidden,
            DateTime windowStart,
            CancellationToken ct)
        {
            var cursor = new ReadingCursor(source.GetAsyncEnumerator(ct), hidden.GetAsyncEnumerator(ct));
            try
            {
                var first = await cursor.PullAsync();
                while (first is not null && first.Timestamp < windowStart)
                {
                    cursor._earlier.Add((first, null));
                    first = await cursor.PullAsync();
                }
                cursor._nextHidden = await cursor.PullHiddenAsync();
                await cursor.MoveToAsync(first);
                cursor._next = await cursor.PullAsync();
            }
            catch
            {
                // Seeding owns the enumerators before the caller's `await using` does.
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
                _earlier.Add((Current!, null));
                await MoveToAsync(next);
                _next = await PullAsync();
            }
        }

        private async Task MoveToAsync(SensorGlucose? reading)
        {
            Current = reading;
            if (reading is null) return;

            while (_nextHidden is { } hidden && hidden.Reading.Timestamp <= reading.Timestamp)
            {
                _earlier.Add(hidden);
                _nextHidden = await PullHiddenAsync();
            }
            _earlier.RemoveAll(e => reading.Timestamp - e.Reading.Timestamp > ReadingTrend.MaxGap);
            _advanced.Add((reading, ReadingTrend.Of(reading, _earlier)));
        }

        private async Task<SensorGlucose?> PullAsync()
        {
            if (_exhausted) return null;
            if (await _source.MoveNextAsync()) return _source.Current;
            _exhausted = true;
            return null;
        }

        private async Task<(SensorGlucose Reading, Guid? DuplicateOf)?> PullHiddenAsync()
        {
            if (_hiddenExhausted) return null;
            if (await _hidden.MoveNextAsync()) return _hidden.Current;
            _hiddenExhausted = true;
            return null;
        }

        public async ValueTask DisposeAsync()
        {
            await _source.DisposeAsync();
            await _hidden.DisposeAsync();
        }
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
        var tickMs = tickUtc.ToMills();

        foreach (var binding in FactBindings)
        {
            var raw = binding.Getter(ctx);
            if (raw is null) continue;

            var projected = binding.Conversion switch
            {
                // Direct takes decimal properties; bool flags are projected as 0/1.
                ReplayFactConversion.Direct => raw is bool flag ? (flag ? 1m : 0m) : (decimal)raw,
                ReplayFactConversion.MinutesSinceNow => (tickUtc - (DateTime)raw).TotalMinutes.ToDecimal(),
                ReplayFactConversion.HoursSinceNow => (tickUtc - (DateTime)raw).TotalHours.ToDecimal(),
                ReplayFactConversion.DaysSinceNow => (tickUtc - (DateTime)raw).TotalDays.ToDecimal(),
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
    private (DateTime Start, DateTime End) ResolveWindow(
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
            var now = timeProvider.GetUtcNow().UtcDateTime;
            return (now.AddHours(-24), now);
        }

        var tz = TimeZoneHelper.GetTimeZoneInfoFromId(timezone);

        var localStart = localDate.Value.ToDateTime(TimeOnly.MinValue);
        var localEnd = localStart.AddDays(1);
        var startUtc = TimeZoneHelper.ConvertTimeToUtc(DateTime.SpecifyKind(localStart, DateTimeKind.Unspecified), tz);
        var endUtc = TimeZoneHelper.ConvertTimeToUtc(DateTime.SpecifyKind(localEnd, DateTimeKind.Unspecified), tz);
        return (startUtc, endUtc);
    }

    /// <summary>
    /// The override as a snapshot, with a synthesised id when it has none so any
    /// <c>alert_state</c> references the editor seeded resolve against it. Without its own
    /// <c>on_unknown</c> or glucose freshness bound it takes the stored rule's, disabled or not, so
    /// a replay reads an unknown root and a stale reading as the live engine does.
    /// </summary>
    private async Task<AlertRuleSnapshot> OverrideSnapshotAsync(
        ReplayRuleOverride ruleOverride, Guid tenantId, CancellationToken ct)
    {
        var stored = ruleOverride.Id is { } id
                     && (ruleOverride.OnUnknown is null || ruleOverride.GlucoseFreshnessMinutes is null)
            ? (await alertRepository.GetRuleAsync(id, ct))?.Rule
            : null;
        return new(
            Id: ruleOverride.Id ?? Guid.CreateVersion7(),
            TenantId: tenantId,
            Name: ruleOverride.Name,
            ConditionType: ruleOverride.ConditionType,
            ConditionParams: ruleOverride.ConditionParams,
            Severity: ruleOverride.Severity,
            ClientConfiguration: "{}",
            SortOrder: 0,
            AutoResolveEnabled: ruleOverride.AutoResolveEnabled,
            AutoResolveParams: ruleOverride.AutoResolveParams,
            AllowThroughDnd: ruleOverride.AllowThroughDnd,
            OnUnknown: ruleOverride.OnUnknown ?? stored?.OnUnknown ?? AlertRuleOnUnknown.Hold,
            GlucoseFreshnessMinutes: ruleOverride.GlucoseFreshnessMinutes ?? stored?.GlucoseFreshnessMinutes);
    }

    /// <summary>
    /// Layers <paramref name="candidate"/> onto <paramref name="stored"/>: when its Id matches an
    /// existing rule it replaces it; otherwise it is appended.
    /// </summary>
    private static IReadOnlyList<AlertRuleSnapshot> Layer(
        IReadOnlyList<AlertRuleSnapshot> stored, AlertRuleSnapshot candidate)
    {
        if (stored.Any(r => r.Id == candidate.Id))
        {
            return stored
                .Select(r => r.Id == candidate.Id ? candidate : r)
                .ToList();
        }

        var combined = new List<AlertRuleSnapshot>(stored.Count + 1);
        combined.AddRange(stored);
        combined.Add(candidate);
        return combined;
    }
}
