using Nocturne.Core.Models.LoopAnalysis;

namespace Nocturne.Core.Clinical.LoopAnalysis;

/// <summary>An instant a response curve is aligned on, and for a meal its size.</summary>
public readonly record struct EventAnchor(long Mills, EventAnchorKind Kind, double? Grams = null);

/// <summary>Finds the instants each <see cref="EventAnchorKind"/> aligns on.</summary>
public static class EventAnchors
{
    /// <summary>A pre-bolus for a slow meal or a high glucose can come up to an hour before eating.</summary>
    public const long BolusBeforeMealMs = 60 * LocalSlotBinning.MinuteMs;

    /// <summary>A carb entry is often logged, or a bolus given, after the plate is cleared.</summary>
    public const long BolusAfterMealMs = 30 * LocalSlotBinning.MinuteMs;

    /// <summary>
    /// Which way glucose is expected to go after an anchor of <paramref name="kind"/>. A sensor start
    /// has no expected direction and keeps the rise figures. Exercise is a fall, since a low after it
    /// is the risk. Every override is a fall: the anchor carries no insulin percentage, so one that
    /// asks for less insulin cannot be told apart here; carrying that percentage is where to split it.
    /// </summary>
    public static EventResponseKind ResponseOf(EventAnchorKind kind) => kind switch
    {
        EventAnchorKind.Meal or EventAnchorKind.UnannouncedRise or EventAnchorKind.SensorStart => EventResponseKind.Rise,
        EventAnchorKind.CorrectionBolus or EventAnchorKind.ExerciseStart
            or EventAnchorKind.OverrideStart or EventAnchorKind.TemporaryTargetStart => EventResponseKind.Fall,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    /// <summary>
    /// One anchor per instant with carbs: entries at the same instant, such as a meal logged food by
    /// food, are one meal of their summed grams.
    /// </summary>
    public static List<EventAnchor> Meals(IEnumerable<CarbEvent> carbs) =>
    [
        .. carbs.Where(c => c.Grams > 0)
            .GroupBy(c => c.Mills)
            .OrderBy(g => g.Key)
            .Select(g => new EventAnchor(g.Key, EventAnchorKind.Meal, g.Sum(c => c.Grams))),
    ];

    /// <summary>Every manual dose that <see cref="MealPairing"/> pairs with no meal.</summary>
    public static List<EventAnchor> CorrectionBoluses(IEnumerable<InsulinDose> doses, IReadOnlyList<CarbEvent> carbs)
    {
        var pairing = new MealPairing(carbs);
        return
        [
            .. doses.Where(d => !d.Automatic && d.Units > 0 && pairing.MealOf(d) is null)
                .Select(d => d.Mills)
                .Distinct()
                .Order()
                .Select(m => new EventAnchor(m, EventAnchorKind.CorrectionBolus)),
        ];
    }

    /// <summary>The start of every span, once per instant.</summary>
    public static List<EventAnchor> Starts(IEnumerable<MillsInterval> spans, EventAnchorKind kind) =>
        [.. spans.Select(s => s.Start).Distinct().Order().Select(m => new EventAnchor(m, kind))];

    /// <summary>
    /// How close a sensor change and a sensor start, or two starts, must be to be one new sensor:
    /// an uploader commonly records the change at insertion and the start once its warm-up of up to
    /// two hours has begun or ended.
    /// </summary>
    public const long SensorSessionMergeMs = 2 * LocalSlotBinning.HourMs;

    /// <summary>One anchor per new sensor, at the first record of it.</summary>
    public static List<EventAnchor> SensorStarts(IEnumerable<long> instants)
    {
        var anchors = new List<EventAnchor>();
        foreach (var mills in instants.Order())
        {
            if (anchors.Count == 0 || mills - anchors[^1].Mills > SensorSessionMergeMs)
                anchors.Add(new EventAnchor(mills, EventAnchorKind.SensorStart));
        }
        return anchors;
    }

    /// <summary>Carb entry instants with carbs, sorted, for <see cref="AnyWithin"/>.</summary>
    public static long[] CarbMills(IEnumerable<CarbEvent> carbs) =>
        [.. carbs.Where(c => c.Grams > 0).Select(c => c.Mills).Order()];

    /// <summary>Whether any of <paramref name="sorted"/> lies in <c>[from, to]</c>.</summary>
    public static bool AnyWithin(IReadOnlyList<long> sorted, long from, long to)
    {
        var i = SortedSearch.FirstEndingAfter(sorted, from - 1, static m => m);
        return i < sorted.Count && sorted[i] <= to;
    }

    /// <summary>
    /// Whether no other meal, correction bolus or rise in <paramref name="isolating"/> (sorted instants)
    /// falls inside the anchor's window, <c>[anchor - before, anchor + after)</c>. The anchor's own
    /// instant does not count against it.
    /// </summary>
    public static bool IsIsolated(EventAnchor anchor, IReadOnlyList<long> isolating, long beforeMs, long afterMs)
    {
        for (var i = SortedSearch.FirstEndingAfter(isolating, anchor.Mills - beforeMs - 1, static m => m);
             i < isolating.Count && isolating[i] < anchor.Mills + afterMs;
             i++)
        {
            if (isolating[i] != anchor.Mills)
                return false;
        }
        return true;
    }
}

/// <summary>
/// The meal of <see cref="EventAnchors.Meals"/> each dose was given for. A dose pairs with the meal
/// of a carb entry sharing its <see cref="InsulinDose.CorrelationId"/>, else with the nearest meal
/// from <see cref="EventAnchors.BolusAfterMealMs"/> before it to
/// <see cref="EventAnchors.BolusBeforeMealMs"/> after it, the earlier on a tie. A dose pairs with one
/// meal only, so a bolus between two meals is not counted for both.
/// </summary>
public sealed class MealPairing
{
    private readonly Dictionary<Guid, int> _mealByCorrelation = [];

    public MealPairing(IEnumerable<CarbEvent> carbs)
    {
        var carbList = carbs.Where(c => c.Grams > 0).ToList();
        Meals = EventAnchors.Meals(carbList);
        foreach (var carb in carbList.OrderBy(c => c.Mills))
        {
            if (carb.CorrelationId is { } id)
                _mealByCorrelation.TryAdd(id, SortedSearch.FirstEndingAfter(Meals, carb.Mills - 1, static m => m.Mills));
        }
    }

    public IReadOnlyList<EventAnchor> Meals { get; }

    /// <summary>The index in <see cref="Meals"/> of the meal <paramref name="dose"/> pairs with, or null.</summary>
    public int? MealOf(InsulinDose dose)
    {
        if (dose.CorrelationId is { } id && _mealByCorrelation.TryGetValue(id, out var linked))
            return linked;

        int? nearest = null;
        for (var i = SortedSearch.FirstEndingAfter(Meals, dose.Mills - EventAnchors.BolusAfterMealMs - 1, static m => m.Mills);
             i < Meals.Count && Meals[i].Mills <= dose.Mills + EventAnchors.BolusBeforeMealMs;
             i++)
        {
            if (nearest is not { } best || Math.Abs(Meals[i].Mills - dose.Mills) < Math.Abs(Meals[best].Mills - dose.Mills))
                nearest = i;
        }
        return nearest;
    }
}
