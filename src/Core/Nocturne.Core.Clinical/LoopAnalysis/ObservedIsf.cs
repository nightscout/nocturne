using Nocturne.Core.Constants;
using Nocturne.Core.Models.LoopAnalysis;

namespace Nocturne.Core.Clinical.LoopAnalysis;

/// <summary>
/// Observed ISF per local block, from correction boluses: the drop from the bolus to the end of its
/// window (<see cref="GlucoseDrop"/>) over the insulin that acted in it
/// (<see cref="SettingsEvidenceContext.InsulinActed"/>).
/// </summary>
/// <remarks>
/// A correction is one of <see cref="EventAnchors.CorrectionBoluses"/>, the Meals &amp; events
/// correction anchor, isolated (<see cref="SettingsEvidenceContext.Isolating"/>) over its window: a
/// further bolus or an unannounced climb after it would be read as the correction's own insulin or
/// resistance.
/// </remarks>
public static class ObservedIsf
{
    /// <summary>
    /// How long before a correction carbs reject it: a meal's carbs are still absorbing for an hour
    /// or more, and would be read as insulin resistance.
    /// </summary>
    public const long CarbFreeBeforeMs = LocalSlotBinning.HourMs;

    /// <summary>
    /// The shortest window: a rapid-acting bolus has done most of its work by three hours, so a
    /// window cut shorter by carbs leaves too much of it still to act.
    /// </summary>
    public const long MinimumWindowMs = 3 * LocalSlotBinning.HourMs;

    /// <summary>The window, unless carbs end it sooner: four hours, about as long as a rapid-acting bolus acts.</summary>
    public const long WindowMs = 4 * LocalSlotBinning.HourMs;

    /// <summary>
    /// The most insulin on board just before the bolus, as a share of the bolus: the bolus must be at
    /// least half of what is on board after it. Insulin acted takes the earlier insulin's share out,
    /// so this bounds how far the figure rests on the IOB curve being right. A closed loop has
    /// usually begun correcting a high by the time a manual correction follows, so a tighter bound
    /// would leave a looper few corrections.
    /// </summary>
    public const double MaxIobShareOfBolus = 1.0;

    /// <summary>
    /// The glucose below which a correction is rejected: the hypoglycaemia threshold, at which the
    /// loop suspends and the user treats, so the drop is no longer the insulin's alone. Ending merely
    /// below target is a drop like any other. The corrections rejected are those that dropped
    /// furthest, so a material share of them lowers the figure's confidence
    /// (<see cref="SettingsEvidenceConfidenceReason.ManyWentLow"/>).
    /// </summary>
    public const double LowMgdl = GlucoseConstants.TargetBottomMgdl;

    private static readonly SettingsEvidenceRule[] Rules =
    [
        SettingsEvidenceRule.CarbsNear,
        SettingsEvidenceRule.NotIsolated,
        SettingsEvidenceRule.Override,
        SettingsEvidenceRule.TemporaryTarget,
        SettingsEvidenceRule.Exercise,
        SettingsEvidenceRule.Illness,
        SettingsEvidenceRule.OutsideRange,
        SettingsEvidenceRule.Filtered,
        SettingsEvidenceRule.MissingGlucose,
        SettingsEvidenceRule.NoTarget,
        SettingsEvidenceRule.NotAboveTarget,
        SettingsEvidenceRule.StartUnstable,
        SettingsEvidenceRule.IobUnknown,
        SettingsEvidenceRule.IobAtBolus,
        SettingsEvidenceRule.IobSourceChanged,
        SettingsEvidenceRule.InsulinUncovered,
        SettingsEvidenceRule.WentLow,
        SettingsEvidenceRule.TooLittleInsulin,
    ];

    private static readonly SettingsEvidenceBlock[] Blocks = Enum.GetValues<SettingsEvidenceBlock>();

    internal static List<IsfEvidenceBlock> Calculate(SettingsEvidenceContext context, BasalBaseline? baseline)
    {
        var tallies = Blocks.Select(_ => new RejectionTally(Rules)).ToArray();
        var episodes = Blocks.Select(_ => new List<IsfEvidenceEpisode>()).ToArray();
        var manual = context.Insulin.Doses.Where(d => !d.Automatic).ToLookup(d => d.Mills, d => d.Units);

        foreach (var anchor in EventAnchors.CorrectionBoluses(context.Insulin.Doses, context.Inputs.Series.Carbs))
        {
            if (anchor.Mills < context.From || anchor.Mills >= context.To)
                continue;
            var block = (int)SettingsEvidenceCalculator.BlockOf(LocalSlotBinning.LocalTime(anchor.Mills, context.Zone).Hour);
            tallies[block].Candidate();
            var (episode, rule) = Measure(context, anchor.Mills, manual[anchor.Mills].Sum(), baseline);
            if (rule is { } rejected)
                tallies[block].Reject(rejected);
            else
                episodes[block].Add(episode!);
        }

        return [.. Blocks.Select((b, i) => context.Finish(
            new IsfEvidenceBlock { Block = b, Episodes = context.ListEpisodes ? episodes[i] : [] },
            b,
            episodes[i],
            tallies[i],
            SettingsEvidenceCalculator.Unavailable(tallies[i].Candidates, episodes[i].Count),
            t => context.TherapyAt(t).SensitivityAt(t),
            isfDependent: false,
            glucoseTermMaterial: false,
            materialBorrowed: SettingsEvidenceCalculator.Material(
                episodes[i].Select(e => SettingsEvidenceCalculator.Share(e.BasalExcess, e.InsulinActed)))))];
    }

    private static (IsfEvidenceEpisode? Episode, SettingsEvidenceRule? Rejected) Measure(
        SettingsEvidenceContext context, long start, double units, BasalBaseline? baseline)
    {
        if (EventAnchors.AnyWithin(context.CarbMills, start - CarbFreeBeforeMs, start + MinimumWindowMs - 1))
            return (null, SettingsEvidenceRule.CarbsNear);
        var end = EventAnchors.AnyWithin(context.CarbMills, start + MinimumWindowMs, start + WindowMs - 1)
            ? context.CarbMills[SortedSearch.FirstEndingAfter(context.CarbMills, start + MinimumWindowMs - 1, static m => m)]
            : start + WindowMs;
        if (!EventAnchors.IsIsolated(new EventAnchor(start, EventAnchorKind.CorrectionBolus), context.Isolating, 0, end - start))
            return (null, SettingsEvidenceRule.NotIsolated);

        if (context.Activity(start, end, openLoop: false) is { } activity)
            return (null, activity);
        if (context.Outside(start, end) is { } outside)
            return (null, outside);
        if (context.Series.GlucoseAt(start - SettingsEvidenceCalculator.StableSpanMs) is not { } beforeGlucose
            || GlucoseDrop.Over(context.Series, start, end) is not { } drop)
            return (null, SettingsEvidenceRule.MissingGlucose);
        var (startGlucose, endGlucose) = drop;
        if (context.Inputs.Series.TargetAt?.Invoke(start) is not { } startTarget)
            return (null, SettingsEvidenceRule.NoTarget);
        if (startGlucose <= startTarget.High)
            return (null, SettingsEvidenceRule.NotAboveTarget);
        if (!SettingsEvidenceCalculator.Stable(beforeGlucose, startGlucose))
            return (null, SettingsEvidenceRule.StartUnstable);
        if (context.Series.IobAt(start - 1) is not { } iobBefore)
            return (null, SettingsEvidenceRule.IobUnknown);
        if (Math.Abs(iobBefore) > MaxIobShareOfBolus * units)
            return (null, SettingsEvidenceRule.IobAtBolus);
        var (acted, uncovered) = context.InsulinActed(start, end, baseline);
        if (acted is not { } insulin)
            return (null, uncovered);
        if (context.LowestGlucose(start, end) < LowMgdl)
            return (null, SettingsEvidenceRule.WentLow);
        if (insulin.Acted < SettingsEvidenceCalculator.MinimumInsulinUnits)
            return (null, SettingsEvidenceRule.TooLittleInsulin);

        var episode = context.Episode<IsfEvidenceEpisode>(
            start, end, drop.Mgdl / insulin.Acted, context.TherapyAt(start).SensitivityAt(start));
        context.Fill(episode, end, startGlucose, endGlucose, insulin, iobBefore);
        episode.Units = SlotSummary.Round(units);
        episode.Drop = SlotSummary.Round(drop.Mgdl);
        return (episode, null);
    }
}

/// <summary>
/// The ISF a glucose change is converted to insulin at: the block's observed ISF where it is at
/// <see cref="SettingsEvidenceCalculator.Lendable"/>; elsewhere the profile's, scaled
/// by the mean of the confident blocks' observed-to-profile ratios where any block is confident, on
/// the same reading as <see cref="BasalBaseline"/>. Anything but the block's own observed ISF is
/// ISF-dependent.
/// </summary>
internal sealed class IsfConversion
{
    private readonly double?[] _observed;
    private readonly double _ratio;

    /// <summary>No block's ISF is lendable, so every conversion is at the profile's ISF, unscaled.</summary>
    public bool ProfileOnly => _observed.All(o => o is null);

    private IsfConversion(double?[] observed, double ratio)
    {
        _observed = observed;
        _ratio = ratio;
    }

    public static IsfConversion Of(List<IsfEvidenceBlock> blocks)
    {
        var observed = new double?[Enum.GetValues<SettingsEvidenceBlock>().Length];
        var ratios = new List<double>();
        foreach (var block in blocks)
        {
            if (!SettingsEvidenceCalculator.Lendable(block) || block.Observed is not { } value)
                continue;
            observed[(int)block.Block] = value;
            if (block.Profile is > 0)
                ratios.Add(value / block.Profile.Value);
        }
        return new IsfConversion(observed, ratios.Count > 0 ? ratios.Average() : 1);
    }

    public (double Isf, bool Dependent) At(SettingsEvidenceContext context, long mills)
    {
        var block = SettingsEvidenceCalculator.BlockOf(LocalSlotBinning.LocalTime(mills, context.Zone).Hour);
        return _observed[(int)block] is { } observed
            ? (observed, false)
            : (context.TherapyAt(mills).SensitivityAt(mills) * _ratio, true);
    }
}
