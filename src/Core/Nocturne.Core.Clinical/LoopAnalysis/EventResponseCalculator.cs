using Nocturne.Core.Models.LoopAnalysis;

namespace Nocturne.Core.Clinical.LoopAnalysis;

/// <summary>Everything the Meals and events page is computed from, fetched once for every range it covers.</summary>
/// <remarks>
/// The series must reach a window past each end of the range, since an anchor near its edge reads
/// glucose and insulin outside it.
/// </remarks>
public sealed record EventResponseInputs
{
    public required DailyProfileInputs Series { get; init; }

    /// <summary>The spans whose starts anchor events, and the data exclusions glucose never qualifies under.</summary>
    public LoopAnalysisMaskSources Sources { get; init; } = new();

    public IReadOnlyList<long> SensorStarts { get; init; } = [];

    /// <inheritdoc cref="LoopAnalysisFilter.HistoryVisibleFrom"/>
    public long? HistoryVisibleFrom { get; init; }

    /// <inheritdoc cref="LoopAnalysisFilter.Now"/>
    /// <remarks>An event whose window ends after it is still under way, so it is not counted.</remarks>
    public long Now { get; init; } = long.MaxValue;

    /// <summary>
    /// The food records of each carb entry, by its instant, so a meal carries those of every entry it
    /// sums. Read only when a cohort has a food filter; an instant with none is a meal of carbs only.
    /// </summary>
    public IReadOnlyDictionary<long, IReadOnlyList<MealFood>> MealFoods { get; init; } =
        new Dictionary<long, IReadOnlyList<MealFood>>();
}

/// <summary>One food record of a carb entry: the food it names, if any, and that food's category.</summary>
/// <param name="FoodId">Null for a record of unspecified carbs.</param>
public readonly record struct MealFood(Guid? FoodId, string? Category);

/// <summary>
/// The Meals and events page: each cohort's events aligned on their anchors, every series spread
/// across events in relative slots, with each event's figures and their spread.
/// </summary>
/// <remarks>
/// An anchor counts when it falls in the range's analysed time (<see cref="LoopAnalysisMask.Analysed"/>);
/// its window then reads whatever data surrounds it, masked or not, except readings under a data
/// exclusion. An anchor two cohorts both match is one episode, counted in each. Whether an event is
/// isolated (<see cref="EventAnchors.IsIsolated"/>) is decided for every event, whether or not only
/// isolated events are asked for, because a meal's grams per unit is read only off an isolated one.
/// </remarks>
public static class EventResponseCalculator
{
    /// <summary>
    /// How far before an anchor what decides it reaches: the window shown before it, the carbs
    /// that announce a rise, the carbs that make a bolus a meal's rather than a correction, the
    /// readings that decide an excursion start, and the carbs a minutes-since-meal condition reads
    /// (<see cref="ConditionLookbackMs"/>).
    /// </summary>
    public static long HistoryLookbackMs(EventWindow window, EventResponseDefinition? definition = null) =>
        new[]
        {
            window.BeforeMs, UnannouncedRises.CarbsBeforeMs, EventAnchors.BolusAfterMealMs,
            EventAnchors.ExcursionLookbackMs, definition is null ? 0 : ConditionLookbackMs(definition),
        }.Max();

    /// <summary>
    /// How far before an anchor the cohorts' minutes-since-meal conditions read carbs: the largest
    /// bound any of them names. A carb entry further back decides none of them.
    /// </summary>
    public static long ConditionLookbackMs(EventResponseDefinition definition) =>
        definition.Cohorts
            .Select(c => Math.Max(c.MinMinutesSinceMeal ?? 0, c.MaxMinutesSinceMeal ?? 0))
            .DefaultIfEmpty(0)
            .Max() * LocalSlotBinning.MinuteMs;

    /// <param name="listEvents">Whether to list the events; the comparison range's are not shown.</param>
    public static EventResponseRange Calculate(
        EventResponseInputs inputs,
        LoopAnalysisMask mask,
        long from,
        long to,
        EventResponseDefinition definition,
        bool listEvents = true)
    {
        var zone = inputs.Series.Zone;
        var window = EventWindow.Of(definition);
        var visibleFrom = VisibleHistory.ReadableFrom(inputs.HistoryVisibleFrom, HistoryLookbackMs(window, definition));
        var exclusions = IntervalSet.Of(inputs.Sources.DataExclusions);
        var series = new EventSeries(inputs.Series, exclusions);
        var anchors = new AnchorSource(inputs, series, exclusions, definition.RequireCorroboration);
        var state = new AnchorState(series, EventAnchors.CarbMills(inputs.Series.Carbs));
        long[] isolating =
        [
            .. anchors.Of(EventAnchorKind.Meal)
                .Concat(anchors.Of(EventAnchorKind.CorrectionBolus))
                .Select(a => a.Mills)
                .Concat(anchors.Climbs)
                .Order(),
        ];

        var episodes = new Dictionary<EventAnchor, EventEpisode?>();
        var membership = new SortedDictionary<long, List<(EventEpisode Episode, int Cohort)>>();
        var cohorts = new List<EventCohortResult>(definition.Cohorts.Count);

        foreach (var cohort in definition.Cohorts)
        {
            var result = new EventCohortResult { Cohort = cohort.Cohort, Response = EventAnchors.ResponseOf(cohort.Anchor) };
            var counted = new List<EventEpisode>();
            var siblings = cohort.Others ? Siblings(definition, cohort) : [];
            var withheld = cohort.FoodFilterWithheld || siblings.Any(s => s.FoodFilterWithheld);
            foreach (var anchor in withheld ? [] : anchors.Of(cohort.Anchor))
            {
                if (anchor.Mills < from || anchor.Mills >= to || anchor.Mills + window.AfterMs > inputs.Now
                    || !mask.Analysed.Contains(anchor.Mills))
                    continue;
                if (cohort.Others)
                {
                    if (siblings.Any(s => Selects(s, anchor, zone, inputs.MealFoods, state)))
                        continue;
                }
                else
                {
                    if (!Matches(cohort, anchor, zone))
                        continue;
                    if (anchor.Kind == EventAnchorKind.Meal && cohort.HasFoodFilter)
                    {
                        if (FoodRecords(inputs.MealFoods, anchor) is not { } foods)
                        {
                            result.MealsWithoutFoodRecord++;
                            continue;
                        }
                        result.MealsWithFoodRecord++;
                        if (!MatchesFoods(cohort, foods))
                            continue;
                    }
                    if (cohort.HasIob && state.Iob(anchor) is null)
                    {
                        result.MissingAnchorIob++;
                        continue;
                    }
                    if (!MatchesConditions(cohort, anchor, state))
                        continue;
                }
                result.Anchors++;
                if (anchor.Mills < visibleFrom)
                {
                    result.BeforeVisibleHistory++;
                    continue;
                }
                var isolated = EventAnchors.IsIsolated(anchor, isolating, window.BeforeMs, window.AfterMs);
                if (definition.IsolatedOnly && !isolated)
                {
                    result.NotIsolated++;
                    continue;
                }
                if (!episodes.TryGetValue(anchor, out var episode))
                    episodes[anchor] = episode = EventEpisode.Build(anchor, series, window, isolated);
                if (episode is null)
                {
                    result.MissingAnchorGlucose++;
                    continue;
                }
                counted.Add(episode);
                if (anchor.Kind == EventAnchorKind.Meal && episode.Metrics.GramsPerUnit is null)
                {
                    if (!isolated)
                        result.GramsPerUnitNotIsolated++;
                    else if (episode.Metrics.NetInsulin is null)
                        result.GramsPerUnitUncovered++;
                    else
                        result.GramsPerUnitNoNetInsulin++;
                }
                if (!membership.TryGetValue(anchor.Mills, out var members))
                    membership[anchor.Mills] = members = [];
                members.Add((episode, cohort.Cohort));
            }

            result.Events = counted.Count;
            result.WentBelowRange = counted.Count(e => e.Metrics.WentBelowRange == true);
            result.WentBelowRangeObserved = counted.Count(e => e.Metrics.WentBelowRange != null);
            result.Slots = Slots(counted, window);
            result.Metrics = Summarise(counted);
            cohorts.Add(result);
        }

        return new EventResponseRange
        {
            From = DateTimeOffset.FromUnixTimeMilliseconds(from).UtcDateTime,
            To = DateTimeOffset.FromUnixTimeMilliseconds(to).UtcDateTime,
            Cohorts = cohorts,
            Events = listEvents
                ? [.. membership.Values.SelectMany(members => members
                    .GroupBy(m => m.Episode)
                    .Select(g => Event(g.Key, [.. g.Select(m => m.Cohort).Distinct().Order()], zone, definition.IncludeTraces)))]
                : [],
        };
    }

    /// <summary>Minutes from the anchor to the start of each slot of the window.</summary>
    public static List<int> SlotMinutes(EventResponseDefinition definition)
    {
        var window = EventWindow.Of(definition);
        return [.. Enumerable.Range(0, window.SlotCount).Select(k => (int)(window.SlotOffset(k) / LocalSlotBinning.MinuteMs))];
    }

    /// <summary>
    /// Whether an anchor falls on one of the cohort's local weekdays and in its local time-of-day
    /// window and, for a meal, its size. The foods a meal carries are <see cref="MatchesFoods"/>'.
    /// </summary>
    public static bool Matches(EventCohortDefinition cohort, EventAnchor anchor, TimeZoneInfo zone)
    {
        if (anchor.Grams is { } grams && (grams < cohort.MinGrams || grams > cohort.MaxGrams))
            return false;
        var days = cohort.DaysOfWeek is { Count: > 0 } d ? d : null;
        if (days is null && cohort.FromMinute is null && cohort.ToMinute is null)
            return true;

        var local = LocalSlotBinning.LocalTime(anchor.Mills, zone);
        if (days is not null && !days.Contains(local.DayOfWeek))
            return false;
        if (cohort.FromMinute is null && cohort.ToMinute is null)
            return true;

        var minute = local.Hour * 60 + local.Minute;
        var start = cohort.FromMinute ?? 0;
        var end = cohort.ToMinute ?? 24 * 60;
        return start <= end ? minute >= start && minute < end : minute >= start || minute < end;
    }

    /// <summary>
    /// Whether a meal's food records satisfy the cohort's food filter: one names a food of
    /// <see cref="EventCohortDefinition.FoodIds"/>, and one is of
    /// <see cref="EventCohortDefinition.FoodCategory"/>, where each is set.
    /// </summary>
    public static bool MatchesFoods(EventCohortDefinition cohort, IReadOnlyList<MealFood> foods)
    {
        if (cohort.FoodIds is { Count: > 0 } ids && !foods.Any(f => f.FoodId is { } id && ids.Contains(id)))
            return false;
        if (cohort.FoodCategory?.Trim() is { Length: > 0 } category
            && !foods.Any(f => string.Equals(f.Category?.Trim(), category, StringComparison.OrdinalIgnoreCase)))
            return false;
        return true;
    }

    /// <summary>Whether a record names a food or a category; one of unspecified carbs is not a food record.</summary>
    public static bool IsFoodRecord(MealFood food) => food.FoodId is not null || !string.IsNullOrWhiteSpace(food.Category);

    /// <summary>The cohorts an <see cref="EventCohortDefinition.Others"/> cohort is the rest of: every other cohort of its anchor.</summary>
    private static List<EventCohortDefinition> Siblings(EventResponseDefinition definition, EventCohortDefinition others) =>
        [.. definition.Cohorts.Where(c => c != others && !c.Others && c.Anchor == others.Anchor)];

    /// <summary>A meal's food records, or null where it carries none.</summary>
    private static List<MealFood>? FoodRecords(IReadOnlyDictionary<long, IReadOnlyList<MealFood>> mealFoods, EventAnchor anchor) =>
        mealFoods.TryGetValue(anchor.Mills, out var recorded) && recorded.Where(IsFoodRecord).ToList() is { Count: > 0 } foods
            ? foods
            : null;

    /// <summary>
    /// Whether <paramref name="cohort"/> takes the anchor into its <see cref="EventCohortResult.Anchors"/>:
    /// every filter and condition it is counted by before its isolation and start glucose are read.
    /// An anchor no sibling selects belongs to the <see cref="EventCohortDefinition.Others"/> cohort, so
    /// the two together hold every anchor of the kind exactly once.
    /// </summary>
    private static bool Selects(
        EventCohortDefinition cohort, EventAnchor anchor, TimeZoneInfo zone,
        IReadOnlyDictionary<long, IReadOnlyList<MealFood>> mealFoods, AnchorState state) =>
        Matches(cohort, anchor, zone)
        && (anchor.Kind != EventAnchorKind.Meal || !cohort.HasFoodFilter
            || FoodRecords(mealFoods, anchor) is { } foods && MatchesFoods(cohort, foods))
        && (!cohort.HasIob || state.Iob(anchor) is not null)
        && MatchesConditions(cohort, anchor, state);

    /// <summary>
    /// Whether the state at the anchor meets the cohort's conditions. A start glucose that cannot
    /// be read passes here and is counted as <see cref="EventCohortResult.MissingAnchorGlucose"/>,
    /// as any anchor without one is; an IOB that cannot be read fails.
    /// </summary>
    private static bool MatchesConditions(EventCohortDefinition cohort, EventAnchor anchor, AnchorState state)
    {
        if (cohort.HasMinutesSinceMeal)
        {
            var since = state.MinutesSinceMeal(anchor);
            if (cohort.MinMinutesSinceMeal is { } min && since < min)
                return false;
            if (cohort.MaxMinutesSinceMeal is { } max && !(since <= max))
                return false;
        }
        if (cohort.HasStartGlucose && state.StartGlucose(anchor) is { } glucose
            && (glucose < cohort.MinStartGlucose || glucose > cohort.MaxStartGlucose))
            return false;
        if (cohort.HasIob && (state.Iob(anchor) is not { } iob || iob < cohort.MinIob || iob > cohort.MaxIob))
            return false;
        return true;
    }

    /// <summary>
    /// What a cohort's conditions read at an anchor, off the series already loaded: the start
    /// glucose, the IOB and the time since the last carbs.
    /// </summary>
    private sealed class AnchorState(EventSeries series, long[] carbMills)
    {
        public double? StartGlucose(EventAnchor anchor) => EventEpisode.StartGlucoseOf(anchor, series);

        public double? Iob(EventAnchor anchor) => series.IobAt(anchor.Mills);

        /// <summary>
        /// Minutes from the last carb entry strictly before the anchor; null where none was loaded,
        /// which reads as longer than any bound, since the load reaches back
        /// <see cref="ConditionLookbackMs"/>.
        /// </summary>
        public double? MinutesSinceMeal(EventAnchor anchor)
        {
            var next = SortedSearch.FirstEndingAfter(carbMills, anchor.Mills - 1, static m => m);
            return next > 0 ? (anchor.Mills - carbMills[next - 1]) / (double)LocalSlotBinning.MinuteMs : null;
        }
    }

    private static EventResponseEvent Event(EventEpisode episode, List<int> cohorts, TimeZoneInfo zone, bool trace)
    {
        var (date, time) = LocalSlotBinning.DayInReview(episode.Anchor.Mills, zone);
        return new EventResponseEvent
        {
            At = DateTimeOffset.FromUnixTimeMilliseconds(episode.Anchor.Mills).UtcDateTime,
            Date = date,
            Time = time,
            Anchor = episode.Anchor.Kind,
            Response = EventAnchors.ResponseOf(episode.Anchor.Kind),
            Grams = episode.Anchor.Grams is { } grams ? SlotSummary.Round(grams) : null,
            Cohorts = cohorts,
            GlucoseCoverage = GlucoseCoverage(episode),
            GlucoseTrace = trace ? [.. episode.Slots.Select(s => s.Glucose is { } g ? SlotSummary.Round(g) : (double?)null)] : [],
            Metrics = Rounded(episode.Metrics),
        };
    }

    private static string GlucoseCoverage(EventEpisode episode)
    {
        var bits = new byte[(episode.Slots.Count + 7) / 8];
        for (var k = 0; k < episode.Slots.Count; k++)
            if (episode.Slots[k].Glucose is not null)
                bits[k / 8] |= (byte)(1 << (k % 8));
        return Convert.ToBase64String(bits);
    }

    private static List<EventResponseSlot> Slots(List<EventEpisode> episodes, EventWindow window)
    {
        var slots = new List<EventResponseSlot>(window.SlotCount);
        for (var k = 0; k < window.SlotCount; k++)
        {
            LoopAnalysisAggregate? Of(Func<EpisodeSlot, double?> pick) =>
                SlotSummary.Of([.. episodes.Select(e => pick(e.Slots[k])).OfType<double>()]);

            slots.Add(new EventResponseSlot
            {
                GlucoseChange = Of(s => s.GlucoseChange),
                Glucose = Of(s => s.Glucose),
                NetLoopCorrection = Of(s => s.NetLoopCorrection),
                NetInsulinSinceAnchor = Of(s => s.NetInsulinSinceAnchor),
                NetLoopCorrectionSinceAnchor = Of(s => s.NetLoopCorrectionSinceAnchor),
                Iob = Of(s => s.Iob),
                Cob = Of(s => s.Cob),
            });
        }
        return slots;
    }

    /// <summary>
    /// Every numeric <see cref="EventMetrics"/> figure with its <see cref="EventMetricsSummary"/>
    /// counterpart; <see cref="EventMetrics.WentBelowRange"/> is counted on the cohort instead.
    /// </summary>
    private static readonly (Func<EventMetrics, double?> Get, Action<EventMetrics, double?> Set, Action<EventMetricsSummary, LoopAnalysisAggregate?> Summary)[] MetricFields =
    [
        (m => m.StartGlucose, (m, v) => m.StartGlucose = v, (s, v) => s.StartGlucose = v),
        (m => m.PeakRise, (m, v) => m.PeakRise = v, (s, v) => s.PeakRise = v),
        (m => m.MinutesToPeak, (m, v) => m.MinutesToPeak = v, (s, v) => s.MinutesToPeak = v),
        (m => m.GlucoseAt2Hours, (m, v) => m.GlucoseAt2Hours = v, (s, v) => s.GlucoseAt2Hours = v),
        (m => m.GlucoseAt4Hours, (m, v) => m.GlucoseAt4Hours = v, (s, v) => s.GlucoseAt4Hours = v),
        (m => m.MinutesBackToStart, (m, v) => m.MinutesBackToStart = v, (s, v) => s.MinutesBackToStart = v),
        (m => m.Drop, (m, v) => m.Drop = v, (s, v) => s.Drop = v),
        (m => m.Nadir, (m, v) => m.Nadir = v, (s, v) => s.Nadir = v),
        (m => m.MinutesToNadir, (m, v) => m.MinutesToNadir = v, (s, v) => s.MinutesToNadir = v),
        (m => m.TotalInsulin, (m, v) => m.TotalInsulin = v, (s, v) => s.TotalInsulin = v),
        (m => m.NetInsulin, (m, v) => m.NetInsulin = v, (s, v) => s.NetInsulin = v),
        (m => m.IobAtAnchor, (m, v) => m.IobAtAnchor = v, (s, v) => s.IobAtAnchor = v),
        (m => m.IobAtEnd, (m, v) => m.IobAtEnd = v, (s, v) => s.IobAtEnd = v),
        (m => m.GramsPerUnit, (m, v) => m.GramsPerUnit = v, (s, v) => s.GramsPerUnit = v),
        (m => m.DropPerUnit, (m, v) => m.DropPerUnit = v, (s, v) => s.DropPerUnit = v),
        (m => m.NetInsulinFirst30, (m, v) => m.NetInsulinFirst30 = v, (s, v) => s.NetInsulinFirst30 = v),
        (m => m.NetInsulinFirst60, (m, v) => m.NetInsulinFirst60 = v, (s, v) => s.NetInsulinFirst60 = v),
        (m => m.NetInsulinFirst120, (m, v) => m.NetInsulinFirst120 = v, (s, v) => s.NetInsulinFirst120 = v),
    ];

    private static EventMetricsSummary Summarise(List<EventEpisode> episodes)
    {
        var summary = new EventMetricsSummary();
        foreach (var (get, _, set) in MetricFields)
            set(summary, SlotSummary.Of([.. episodes.Select(e => get(e.Metrics)).OfType<double>()]));
        return summary;
    }

    private static EventMetrics Rounded(EventMetrics metrics)
    {
        var rounded = new EventMetrics { WentBelowRange = metrics.WentBelowRange };
        foreach (var (get, set, _) in MetricFields)
            set(rounded, get(metrics) is { } value ? SlotSummary.Round(value) : null);
        return rounded;
    }

    /// <summary>The anchors of each kind, found once per range whichever cohorts ask for them.</summary>
    private sealed class AnchorSource(
        EventResponseInputs inputs, EventSeries series, IntervalSet exclusions, bool requireCorroboration)
    {
        private readonly Dictionary<EventAnchorKind, List<EventAnchor>> _found = [];
        private RiseSearch? _rises;

        /// <summary>
        /// Every meal-like climb, announced, merged and unanswered ones included, whatever
        /// corroboration is asked for: an intake that makes an episode around it not isolated.
        /// </summary>
        public List<long> Climbs => Rises().Climbs;

        private RiseSearch Rises() => _rises ??= UnannouncedRises.Find(
            series.Readings, exclusions, inputs.Series.Carbs, series.Insulin, requireCorroboration, series.CobAt);

        public List<EventAnchor> Of(EventAnchorKind kind)
        {
            if (!_found.TryGetValue(kind, out var anchors))
                _found[kind] = anchors = Find(kind);
            return anchors;
        }

        private List<EventAnchor> Find(EventAnchorKind kind)
        {
            var carbs = inputs.Series.Carbs;
            var sources = inputs.Sources;
            return kind switch
            {
                EventAnchorKind.Meal => EventAnchors.Meals(carbs),
                EventAnchorKind.CorrectionBolus => EventAnchors.CorrectionBoluses(series.Insulin.Doses, carbs),
                EventAnchorKind.SensorStart => EventAnchors.SensorStarts(inputs.SensorStarts),
                EventAnchorKind.ExerciseStart => EventAnchors.Starts(sources.Exercise, kind),
                EventAnchorKind.OverrideStart => EventAnchors.Starts(sources.Overrides, kind),
                EventAnchorKind.TemporaryTargetStart => EventAnchors.Starts(sources.TemporaryTargets, kind),
                EventAnchorKind.UnannouncedRise => Rises().Anchors,
                EventAnchorKind.LowStart => Excursions().Lows,
                EventAnchorKind.HighStart => Excursions().Highs,
                _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
            };
        }

        private (List<EventAnchor> Lows, List<EventAnchor> Highs)? _excursions;

        /// <summary>Over the readings outside the data exclusions: an excluded stretch longer than a gap ends an episode.</summary>
        private (List<EventAnchor> Lows, List<EventAnchor> Highs) Excursions() =>
            _excursions ??= EventAnchors.Excursions(series.Readings);
    }
}
