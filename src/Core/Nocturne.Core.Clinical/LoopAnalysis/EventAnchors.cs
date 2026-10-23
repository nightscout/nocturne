using Nocturne.Core.Models.LoopAnalysis;

namespace Nocturne.Core.Clinical.LoopAnalysis;

/// <summary>An instant a response curve is aligned on, and for a meal its size.</summary>
public readonly record struct EventAnchor(long Mills, EventAnchorKind Kind, double? Grams = null);

/// <summary>Finds the instants each <see cref="EventAnchorKind"/> aligns on.</summary>
public static class EventAnchors
{
    /// <summary>
    /// How far either side of a bolus a carb entry makes it a meal bolus rather than a correction,
    /// and of a rise makes it an announced meal. Pre-bolusing commonly runs 15 to 20 minutes ahead of
    /// eating, and a carb entry is often logged after the plate is cleared; half an hour covers both
    /// without reaching the next meal.
    /// </summary>
    public const long CarbFreeMs = 30 * LocalSlotBinning.MinuteMs;

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

    /// <summary>Every manual dose with no carb entry within <see cref="CarbFreeMs"/> either side.</summary>
    public static List<EventAnchor> CorrectionBoluses(IEnumerable<InsulinDose> doses, IReadOnlyList<CarbEvent> carbs)
    {
        var carbMills = CarbMills(carbs);
        return
        [
            .. doses.Where(d => !d.Automatic && d.Units > 0 && !AnyWithin(carbMills, d.Mills - CarbFreeMs, d.Mills + CarbFreeMs))
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
