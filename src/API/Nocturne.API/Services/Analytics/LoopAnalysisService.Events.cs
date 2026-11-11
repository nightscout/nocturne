using Nocturne.Core.Clinical.LoopAnalysis;
using Nocturne.Core.Clinical.Treatments;
using Nocturne.Core.Models;
using Nocturne.Core.Models.LoopAnalysis;

namespace Nocturne.API.Services.Analytics;

internal sealed partial class LoopAnalysisService
{
    private static readonly DeviceEventType[] SensorStartTypes = [DeviceEventType.SensorStart, DeviceEventType.SensorChange];

    private static readonly DeviceEventType[] SiteChangeTypes =
        [DeviceEventType.SiteChange, DeviceEventType.CannulaChange, DeviceEventType.PodChange, DeviceEventType.PodActivated];

    /// <summary>The most sleep sessions one range reads: several a night over the longest range.</summary>
    private const int MaxSleepSessions = 2000;

    /// <summary>
    /// How far past the window's end each load reads, so an anchor at the end of a range has the
    /// carbs after it that decide a correction bolus or a rise, and the climb that shows a rise.
    /// </summary>
    private const long EventMarginMs = LocalSlotBinning.HourMs;

    /// <summary>
    /// How far before the range each load reads. A climb at the start of an anchor's window decides
    /// whether the anchor is isolated: it is told from a meal by carbs up to
    /// <see cref="UnannouncedRises.CarbsBeforeMs"/> before it, and from the continuation of an earlier
    /// climb by one up to <see cref="UnannouncedRises.MergeMs"/> before it, found only after a
    /// <see cref="UnannouncedRises.StableMs"/> stretch. Corroboration reads the schedule
    /// <see cref="UnannouncedRises.CorroborationScheduleMs"/> before a rise. A minutes-since-meal
    /// condition reads carbs <see cref="EventResponseCalculator.ConditionLookbackMs"/> before an anchor.
    /// </summary>
    private static long EventLookbackMs(EventWindow window, EventResponseDefinition definition) =>
        Math.Max(
            Math.Max(
                window.BeforeMs + Math.Max(UnannouncedRises.CarbsBeforeMs, UnannouncedRises.MergeMs + UnannouncedRises.StableMs),
                UnannouncedRises.CorroborationScheduleMs),
            EventResponseCalculator.ConditionLookbackMs(definition));

    public async Task<EventResponseReport> GetEventResponseAsync(
        LoopAnalysisFilter filter, EventResponseDefinition definition, TimeZoneInfo zone, CancellationToken ct = default)
    {
        var window = EventWindow.Of(definition);
        var anchors = definition.Cohorts.Select(c => c.Anchor).ToHashSet();
        var reads = new EventReads(
            SensorStarts: anchors.Contains(EventAnchorKind.SensorStart),
            SiteChanges: anchors.Contains(EventAnchorKind.SiteChange),
            Sleep: anchors.Contains(EventAnchorKind.SleepStart) || anchors.Contains(EventAnchorKind.Wake));
        var mealFoods = definition.Cohorts.Any(c => c.Anchor == EventAnchorKind.Meal && c.HasFoodFilter);
        var (primary, comparison, names) = await ForRangesAsync(
            filter,
            range => LoadEventsAsync(range, window, EventLookbackMs(window, definition), zone, reads, mealFoods, filter, ct),
            (loaded, range, isPrimary) => EventResponseCalculator.Calculate(
                loaded.Inputs, Mask(filter, range, zone, loaded.Sources), range.From, range.To, definition,
                listEvents: isPrimary));
        return new EventResponseReport { Primary = primary, Comparison = comparison, ProfileNames = names };
    }

    /// <summary>The reads beyond the Daily profile's that the cohorts' anchors need.</summary>
    private sealed record EventReads(bool SensorStarts, bool SiteChanges, bool Sleep);

    private sealed record EventsLoaded(EventResponseInputs Inputs) : ILoaded
    {
        public LoopAnalysisMaskSources Sources => Inputs.Sources;
    }

    /// <summary>
    /// The Daily profile's reads over <paramref name="range"/> widened by
    /// <see cref="EventLookbackMs"/> before it and the window and <see cref="EventMarginMs"/> after
    /// it, since an anchor near either end reads past it, and the sensor
    /// starts in the range when a cohort asks for them, from
    /// <see cref="EventAnchors.SensorSessionMergeMs"/> before it so a start just inside the range is
    /// merged with the change recorded just before it, and so the site changes. The sleep sessions
    /// overlapping the range when a cohort asks for sleep starts or wake-ups. With
    /// <paramref name="mealFoods"/>, also the food records of the carb entries in the range.
    /// </summary>
    private async Task<EventsLoaded> LoadEventsAsync(
        Window range, EventWindow window, long lookbackMs, TimeZoneInfo zone, EventReads reads, bool mealFoods, LoopAnalysisFilter filter, CancellationToken ct)
    {
        var padded = new Window(range.From - lookbackMs, range.To + window.AfterMs + EventMarginMs);
        var sleepTask = reads.Sleep
            ? sleepSessionRepository.GetSessionsAsync(ToUtc(range.From), ToUtc(range.To), limit: MaxSleepSessions, descending: false, cancellationToken: ct)
            : Task.FromResult(Enumerable.Empty<SleepSession>());
        var sensorTask = reads.SensorStarts || reads.SiteChanges
            ? deviceEventRepository.GetAsync(
                ToUtc(range.From - EventAnchors.SensorSessionMergeMs), ToUtc(range.To), null, null, int.MaxValue, descending: false, ct: ct)
            : Task.FromResult(Enumerable.Empty<Core.Models.V4.DeviceEvent>());
        var loaded = await LoadDailyProfileAsync(padded, zone, filter, ct);
        var foods = mealFoods ? await MealFoodsAsync(loaded.CarbIntakes, range, ct) : new Dictionary<long, IReadOnlyList<MealFood>>();
        var deviceEvents = (await sensorTask).ToList();

        return new EventsLoaded(new EventResponseInputs
        {
            MealFoods = foods,
            Series = loaded.Inputs,
            Sources = loaded.Sources,
            SensorStarts = reads.SensorStarts ? [.. deviceEvents.Where(e => SensorStartTypes.Contains(e.EventType)).Select(e => e.Mills)] : [],
            SiteChanges = reads.SiteChanges ? [.. deviceEvents.Where(e => SiteChangeTypes.Contains(e.EventType)).Select(e => e.Mills)] : [],
            SleepSessions = [.. (await sleepTask).Select(s => new MillsInterval(s.StartTime.ToMills(), s.EndTime.ToMills()))],
            HistoryVisibleFrom = loaded.Inputs.HistoryVisibleFrom,
            Now = filter.Now.ToMills(),
        });
    }

    /// <summary>The food records of each carb entry with carbs in <paramref name="range"/>, by its instant.</summary>
    private async Task<Dictionary<long, IReadOnlyList<MealFood>>> MealFoodsAsync(
        IEnumerable<Core.Models.V4.CarbIntake> carbs, Window range, CancellationToken ct)
    {
        var inRange = carbs.Where(c => c.Carbs > 0 && c.Mills >= range.From && c.Mills < range.To).ToList();
        var records = (await treatmentFoodRepository.GetCarbIntakeFoodsAsync(inRange.Select(c => c.Id), ct))
            .ToLookup(r => r.CarbIntakeId);
        return inRange
            .GroupBy(c => c.Mills)
            .Select(g => (Mills: g.Key, Foods: g.SelectMany(c => records[c.Id]).Select(r => new MealFood(r.FoodId, r.FoodCategory)).ToList()))
            .Where(m => m.Foods.Count > 0)
            .ToDictionary(m => m.Mills, m => (IReadOnlyList<MealFood>)m.Foods);
    }
}
