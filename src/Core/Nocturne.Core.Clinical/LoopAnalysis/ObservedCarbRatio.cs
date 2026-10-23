using Nocturne.Core.Constants;
using Nocturne.Core.Models.LoopAnalysis;

namespace Nocturne.Core.Clinical.LoopAnalysis;

/// <summary>
/// Observed carb ratio per local block, from isolated announced meals: grams over the insulin the
/// meal needed, which is the insulin that acted over its window
/// (<see cref="SettingsEvidenceContext.InsulinActed"/>) plus what glucose's change across it says
/// was missing or spare.
/// </summary>
/// <remarks>
/// A meal is one of <see cref="EventAnchors.Meals"/>, isolated
/// (<see cref="SettingsEvidenceContext.Isolating"/>) over its window. The glucose change is converted as
/// <see cref="IsfConversion"/> says, so a wrong profile ISF does not pass into the carb ratio.
/// </remarks>
public static class ObservedCarbRatio
{
    /// <summary>A pre-bolus commonly comes up to a quarter of an hour before the plate.</summary>
    public const long WindowBeforeMs = 15 * LocalSlotBinning.MinuteMs;

    /// <summary>Four hours: a mixed meal has absorbed by then, and its bolus mostly acted.</summary>
    public const long WindowAfterMs = 4 * LocalSlotBinning.HourMs;

    /// <summary>A meal's glucose must start in the target range, so the window's change is the meal's.</summary>
    public const double StartLowMgdl = GlucoseConstants.TargetBottomMgdl;

    /// <inheritdoc cref="StartLowMgdl"/>
    public const double StartHighMgdl = GlucoseConstants.TargetTopMgdl;

    private static readonly SettingsEvidenceRule[] Rules =
    [
        SettingsEvidenceRule.NotIsolated,
        SettingsEvidenceRule.Override,
        SettingsEvidenceRule.TemporaryTarget,
        SettingsEvidenceRule.Exercise,
        SettingsEvidenceRule.Illness,
        SettingsEvidenceRule.OutsideRange,
        SettingsEvidenceRule.Filtered,
        SettingsEvidenceRule.MissingGlucose,
        SettingsEvidenceRule.StartOutOfRange,
        SettingsEvidenceRule.StartUnstable,
        SettingsEvidenceRule.IobUnknown,
        SettingsEvidenceRule.IobSourceChanged,
        SettingsEvidenceRule.InsulinUncovered,
        SettingsEvidenceRule.TooLittleInsulin,
    ];

    internal static List<CarbRatioEvidenceBlock> Calculate(
        SettingsEvidenceContext context, IsfConversion conversion, BasalBaseline? baseline)
    {
        var blocks = Enum.GetValues<SettingsEvidenceBlock>();
        var tallies = blocks.Select(_ => new RejectionTally(Rules)).ToArray();
        var episodes = blocks.Select(_ => new List<CarbRatioEvidenceEpisode>()).ToArray();

        var meals = EventAnchors.Meals(context.Inputs.Series.Carbs);
        foreach (var meal in meals)
        {
            if (meal.Mills < context.From || meal.Mills >= context.To)
                continue;
            var block = (int)SettingsEvidenceCalculator.BlockOf(LocalSlotBinning.LocalTime(meal.Mills, context.Zone).Hour);
            tallies[block].Candidate();
            var (episode, rule) = EventAnchors.IsIsolated(meal, context.Isolating, WindowBeforeMs, WindowAfterMs)
                ? Measure(context, meal, conversion, baseline)
                : (null, SettingsEvidenceRule.NotIsolated);
            if (rule is { } rejected)
                tallies[block].Reject(rejected);
            else
                episodes[block].Add(episode!);
        }

        var glucoseWeight = conversion.ProfileOnly ? SettingsEvidenceCalculator.ProfileOnlyGlucoseWeight : 1.0;
        return [.. blocks.Select((b, i) =>
        {
            var tally = tallies[i];
            return context.Finish(
                new CarbRatioEvidenceBlock { Block = b, Episodes = context.ListEpisodes ? episodes[i] : [] },
                b,
                episodes[i],
                tally,
                tally.Candidates == 0 ? SettingsEvidenceUnavailableReason.NoAnnouncedMeals
                    : tally[SettingsEvidenceRule.NotIsolated] == tally.Candidates ? SettingsEvidenceUnavailableReason.NoneIsolated
                    : SettingsEvidenceCalculator.Unavailable(tally.Candidates, episodes[i].Count),
                t => context.TherapyAt(t).CarbRatioAt(t),
                isfDependent: episodes[i].Any(e => e.IsfDependent),
                glucoseTermMaterial: SettingsEvidenceCalculator.Material(episodes[i].Select(e => glucoseWeight * GlucoseTermShare(e))),
                materialBorrowed: SettingsEvidenceCalculator.Material(episodes[i].Select(e =>
                    glucoseWeight * GlucoseTermShare(e) + SettingsEvidenceCalculator.Share(e.BasalExcess, e.InsulinActed))));
        })];
    }

    /// <summary>
    /// The share of a meal's insulin needed that the glucose-change term carries. A carb ratio rests
    /// on both the ISF that converts it and the basal need its insulin is measured above, so the two
    /// borrowed shares are added before they are judged: each can sit under
    /// <see cref="SettingsEvidenceCalculator.MaterialShare"/> while together they move the figure past it.
    /// </summary>
    private static double GlucoseTermShare(CarbRatioEvidenceEpisode e) =>
        SettingsEvidenceCalculator.Share((e.EndGlucose - e.StartGlucose) / e.Isf, e.InsulinNeeded);

    /// <summary>
    /// IOB before the meal's own bolus: the first manual dose up to <see cref="EventAnchors.CarbFreeMs"/>
    /// before the meal, which a pre-bolus may put ahead of the window's start.
    /// </summary>
    private static double? StandingIob(SettingsEvidenceContext context, long meal)
    {
        var doses = context.Insulin.Doses;
        for (var i = SortedSearch.FirstEndingAfter(doses, meal - EventAnchors.CarbFreeMs - 1, static d => d.Mills);
             i < doses.Count && doses[i].Mills <= meal;
             i++)
        {
            if (!doses[i].Automatic)
                return context.Series.IobAt(doses[i].Mills - 1);
        }
        return null;
    }

    private static (CarbRatioEvidenceEpisode? Episode, SettingsEvidenceRule? Rejected) Measure(
        SettingsEvidenceContext context, EventAnchor meal, IsfConversion conversion, BasalBaseline? baseline)
    {
        var start = meal.Mills - WindowBeforeMs;
        var end = meal.Mills + WindowAfterMs;
        if (context.Activity(start, end, openLoop: false) is { } activity)
            return (null, activity);
        if (context.Outside(start, end) is { } outside)
            return (null, outside);
        if (context.Series.GlucoseAt(start) is not { } startGlucose
            || context.Series.GlucoseAt(meal.Mills) is not { } mealGlucose
            || context.Series.GlucoseAt(end) is not { } endGlucose)
            return (null, SettingsEvidenceRule.MissingGlucose);
        if (startGlucose is < StartLowMgdl or > StartHighMgdl)
            return (null, SettingsEvidenceRule.StartOutOfRange);
        if (!SettingsEvidenceCalculator.Stable(startGlucose, mealGlucose))
            return (null, SettingsEvidenceRule.StartUnstable);
        var (acted, uncovered) = context.InsulinActed(start, end, baseline);
        if (acted is not { } insulin)
            return (null, uncovered);

        var (isf, isfDependent) = conversion.At(context, meal.Mills);
        var needed = insulin.Acted + (endGlucose - startGlucose) / isf;
        if (needed < SettingsEvidenceCalculator.MinimumInsulinUnits)
            return (null, SettingsEvidenceRule.TooLittleInsulin);

        var grams = meal.Grams!.Value;
        var episode = context.Episode<CarbRatioEvidenceEpisode>(
            meal.Mills, end, grams / needed, context.TherapyAt(meal.Mills).CarbRatioAt(meal.Mills));
        context.Fill(episode, end, startGlucose, endGlucose, insulin, StandingIob(context, meal.Mills) ?? insulin.IobAtStart);
        episode.Grams = SlotSummary.Round(grams);
        episode.InsulinNeeded = SlotSummary.Round(needed);
        episode.Isf = SlotSummary.Round(isf);
        episode.IsfDependent = isfDependent;
        return (episode, null);
    }
}
