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
}

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
    /// that announce a rise, and the carbs that make a bolus a meal's rather than a correction.
    /// </summary>
    public static long HistoryLookbackMs(EventWindow window) =>
        Math.Max(window.BeforeMs, Math.Max(UnannouncedRises.CarbsBeforeMs, EventAnchors.CarbFreeMs));

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
        var visibleFrom = VisibleHistory.ReadableFrom(inputs.HistoryVisibleFrom, HistoryLookbackMs(window));
        var exclusions = IntervalSet.Of(inputs.Sources.DataExclusions);
        var series = new EventSeries(inputs.Series, exclusions);
        var anchors = new AnchorSource(inputs, series, exclusions, definition.RequireCorroboration);
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
            foreach (var anchor in anchors.Of(cohort.Anchor))
            {
                if (anchor.Mills < from || anchor.Mills >= to || anchor.Mills + window.AfterMs > inputs.Now
                    || !mask.Analysed.Contains(anchor.Mills) || !Matches(cohort, anchor, zone))
                    continue;
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
                    .Select(g => Event(g.Key, [.. g.Select(m => m.Cohort).Distinct().Order()], zone)))]
                : [],
        };
    }

    /// <summary>Minutes from the anchor to the start of each slot of the window.</summary>
    public static List<int> SlotMinutes(EventResponseDefinition definition)
    {
        var window = EventWindow.Of(definition);
        return [.. Enumerable.Range(0, window.SlotCount).Select(k => (int)(window.SlotOffset(k) / LocalSlotBinning.MinuteMs))];
    }

    /// <summary>Whether an anchor falls in the cohort's local time-of-day window and, for a meal, its size.</summary>
    public static bool Matches(EventCohortDefinition cohort, EventAnchor anchor, TimeZoneInfo zone)
    {
        if (anchor.Grams is { } grams && (grams < cohort.MinGrams || grams > cohort.MaxGrams))
            return false;
        if (cohort.FromMinute is null && cohort.ToMinute is null)
            return true;

        var local = LocalSlotBinning.LocalTime(anchor.Mills, zone);
        var minute = local.Hour * 60 + local.Minute;
        var start = cohort.FromMinute ?? 0;
        var end = cohort.ToMinute ?? 24 * 60;
        return start <= end ? minute >= start && minute < end : minute >= start || minute < end;
    }

    private static EventResponseEvent Event(EventEpisode episode, List<int> cohorts, TimeZoneInfo zone)
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
                _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
            };
        }
    }
}
