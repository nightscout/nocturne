using Nocturne.Core.Models.LoopAnalysis;

namespace Nocturne.Core.Clinical.LoopAnalysis;

/// <summary>
/// Observed basal need per two-hour local block, read off fasting blocks: the schedule, plus the
/// loop's correction, plus the insulin carried into the block less that carried out of it, plus the
/// insulin glucose's drift across the block says was still missing or spare.
/// </summary>
/// <remarks>
/// A block on one day is an episode. Its correction is attributed by when it was delivered: loop
/// insulin acts over the following hour or two, which is why blocks are two hours and not one. The
/// carried term, the insulin that acted in the block
/// (<see cref="SettingsEvidenceContext.InsulinActed"/>) less the correction delivered in it, turns
/// delivered insulin into insulin that acted; it is zero where the loop holds steady, and otherwise
/// is what keeps the tail of the evening's insulin from reading as the first overnight block's need.
/// The observed need is the mean across days, not the median the ISF and carb ratio report: a rate
/// adds up over time, so the mean is the rate that would have delivered the days' insulin.
/// </remarks>
public static class ObservedBasalNeed
{
    public const int BlockHours = 2;

    /// <summary>
    /// How long before a block carbs, or a climb no carb entry caused, keep it from being fasting: a
    /// mixed meal absorbs over two to four hours, and its glucose would be read as basal need.
    /// </summary>
    public const long CarbFreeMs = 4 * LocalSlotBinning.HourMs;

    /// <summary>
    /// How long before a block a manual bolus keeps it from being fasting. Not the profile's DIA: the
    /// carried-insulin term (IOB at the block's start less IOB at its end) already accounts for what
    /// an earlier bolus still has on board, so the rule only has to keep out the meal a bolus was
    /// likely given for, whose absorption <see cref="CarbFreeMs"/> bounds, plus an hour for a bolus
    /// given ahead of eating. A full DIA, ten hours as Trio users commonly set it, rejects every
    /// overnight block after any evening bolus.
    /// </summary>
    public const long ManualBolusFreeMs = 5 * LocalSlotBinning.HourMs;

    /// <summary>
    /// The fewest fasting days a block's need is reported from: the day count below which
    /// <see cref="SlotSummary.SparseDayThreshold"/> reads no slot's spread as a pattern.
    /// </summary>
    public const int MinimumDays = SlotSummary.SparseDayThreshold;

    /// <summary>
    /// The least share of days whose correction has the majority's sign: below 70%, the loop added
    /// and took away about equally, and its mean is the difference of two habits, not a need.
    /// </summary>
    public const double MinimumConsistency = 0.7;

    /// <summary>
    /// How near zero, as a share of the scheduled rate, a day's correction counts as agreeing with
    /// either sign: within a tenth of the schedule, below the smallest step a basal setting is
    /// changed by, the loop left the schedule alone. Without it a schedule that is right, around which
    /// the loop's corrections scatter both ways, would read as inconsistent.
    /// </summary>
    public const double ConsistencyDeadband = 0.1;

    /// <summary>
    /// The steepest mean glucose trend across a block, mg/dL per hour, that its need is read through:
    /// beyond it the drift term, and the ISF it is converted at, outweigh the loop's correction, and
    /// a block drifting 20 mg/dL or more is not one the loop held.
    /// </summary>
    public const double MaxTrendMgdlPerHour = 10;

    private static readonly SettingsEvidenceRule[] Rules =
    [
        SettingsEvidenceRule.OpenLoop,
        SettingsEvidenceRule.Override,
        SettingsEvidenceRule.TemporaryTarget,
        SettingsEvidenceRule.Exercise,
        SettingsEvidenceRule.Illness,
        SettingsEvidenceRule.CarbsBefore,
        SettingsEvidenceRule.RiseBefore,
        SettingsEvidenceRule.ManualBolus,
        SettingsEvidenceRule.CarbsOnBoard,
        SettingsEvidenceRule.Filtered,
        SettingsEvidenceRule.ManualDelivery,
        SettingsEvidenceRule.InferredDelivery,
        SettingsEvidenceRule.InsulinUncovered,
        SettingsEvidenceRule.IobUnknown,
        SettingsEvidenceRule.IobSourceChanged,
        SettingsEvidenceRule.MissingGlucose,
    ];

    private const int BlocksPerDay = 24 / BlockHours;

    internal static List<BasalEvidenceBlock> Calculate(SettingsEvidenceContext context, IsfConversion conversion)
    {
        var tallies = Enumerable.Range(0, BlocksPerDay).Select(_ => new RejectionTally(Rules)).ToArray();
        var days = Enumerable.Range(0, BlocksPerDay).Select(_ => new List<Day>()).ToArray();
        var dayBlocks = context.DayBlocks(hour => hour / BlockHours);

        foreach (var (block, start, end) in dayBlocks)
        {
            if (start < context.From || end > context.To)
                continue;
            tallies[block].Candidate();
            var (day, rule) = Measure(context, start, end, conversion);
            if (rule is { } rejected)
                tallies[block].Reject(rejected);
            else
                days[block].Add(day!);
        }

        var result = new List<BasalEvidenceBlock>(BlocksPerDay);
        for (var b = 0; b < BlocksPerDay; b++)
        {
            var blockTime = dayBlocks.Where(d => d.Block == b).Select(d => (d.Start, d.End));
            result.Add(Summarise(
                context, b, days[b], tallies[b], context.ProfileOver(blockTime, context.ScheduledAt)));
        }
        return result;
    }

    private static (Day? Day, SettingsEvidenceRule? Rejected) Measure(
        SettingsEvidenceContext context, long start, long end, IsfConversion conversion)
    {
        if (context.Activity(start, end, openLoop: true) is { } activity)
            return (null, activity);
        if (EventAnchors.AnyWithin(context.CarbMills, start - CarbFreeMs, end - 1))
            return (null, SettingsEvidenceRule.CarbsBefore);
        if (ClimbedBefore(context, start, end))
            return (null, SettingsEvidenceRule.RiseBefore);
        if (context.Insulin.Over(start - ManualBolusFreeMs, end).ManualBolus > 0)
            return (null, SettingsEvidenceRule.ManualBolus);
        if (context.Series.IobCobOver(start, end)?.Cob > 0)
            return (null, SettingsEvidenceRule.CarbsOnBoard);
        if (context.Outside(start, end) is { } outside)
            return (null, outside);
        var net = context.Insulin.Over(start, end);
        if (net.ManualTemp != 0 || net.Suspend != 0)
            return (null, SettingsEvidenceRule.ManualDelivery);
        if (net.Inferred != 0)
            return (null, SettingsEvidenceRule.InferredDelivery);
        if (net.CoveredMs < DailyProfileCalculator.InsulinSummaryMinimumCoverage * (end - start))
            return (null, SettingsEvidenceRule.InsulinUncovered);
        var (acted, rejected) = context.InsulinActed(start, end);
        if (acted is not { } insulin)
            return (null, rejected);
        if (context.Series.GlucoseAt(start) is not { } startGlucose || context.Series.GlucoseAt(end) is not { } endGlucose)
            return (null, SettingsEvidenceRule.MissingGlucose);

        var hours = (end - start) / (double)LocalSlotBinning.HourMs;
        double isfSum = 0;
        var samples = 0;
        var isfDependent = false;
        for (var t = start; t < end; t += EventWindow.SlotMs, samples++)
        {
            var (value, dependent) = conversion.At(context, t);
            isfSum += value;
            isfDependent |= dependent;
        }
        var isf = isfSum / samples;
        var change = endGlucose - startGlucose;
        var scheduled = context.ScheduledUnits(start, end) * LocalSlotBinning.HourMs / net.CoveredMs;
        var correction = net.AlgorithmTemp * LocalSlotBinning.HourMs / net.CoveredMs + net.AutomaticBolus / hours;
        var carried = insulin.Acted / hours - correction;
        return (new Day(start, end, hours, scheduled, correction, carried, change, isf, isfDependent,
            scheduled + correction + carried + change / isf / hours), null);
    }

    /// <summary>
    /// Whether a meal-like climb no carb entry caused was still under way within
    /// <see cref="CarbFreeMs"/> before the block or during it: read off the climb's peak, not its
    /// start, since its food is absorbing until glucose stops rising.
    /// </summary>
    private static bool ClimbedBefore(SettingsEvidenceContext context, long start, long end)
    {
        var climbs = context.Climbs;
        for (var i = SortedSearch.FirstEndingAfter(climbs, start - CarbFreeMs - UnannouncedRises.MergeMs, static c => c.Start);
             i < climbs.Count && climbs[i].Start < end;
             i++)
        {
            if (climbs[i].End >= start - CarbFreeMs)
                return true;
        }
        return false;
    }

    private static BasalEvidenceBlock Summarise(
        SettingsEvidenceContext context, int block, List<Day> days, RejectionTally tally, double? profile)
    {
        var result = new BasalEvidenceBlock
        {
            StartHour = block * BlockHours,
            EndHour = (block + 1) * BlockHours,
            Profile = profile is { } p ? SlotSummary.Round(p) : null,
            N = days.Count,
            IsfDependent = days.Any(d => d.IsfDependent),
            Rejections = tally.ToList(),
        };

        if (days.Count > 0)
        {
            var corrections = days.Select(d => d.Correction).ToList();
            var meanTrend = days.Average(d => d.Change / d.Hours);
            var agreeUp = days.Count(d => d.Correction >= -ConsistencyDeadband * d.Scheduled);
            var agreeDown = days.Count(d => d.Correction <= ConsistencyDeadband * d.Scheduled);
            var consistency = Math.Max(agreeUp, agreeDown) / (double)days.Count;
            result.MeanNetLoopCorrection = SlotSummary.Round(corrections.Average());
            result.Consistency = SlotSummary.Round(consistency);
            result.GlucoseTrend = SlotSummary.Of([.. days.Select(d => d.Change / d.Hours)]);
            result.UnavailableReason = days.Count < MinimumDays ? SettingsEvidenceUnavailableReason.TooFew
                : Math.Abs(meanTrend) > MaxTrendMgdlPerHour ? SettingsEvidenceUnavailableReason.SteepTrend
                : consistency < MinimumConsistency ? SettingsEvidenceUnavailableReason.Inconsistent
                : null;
        }
        else
        {
            result.UnavailableReason = tally.Candidates == 0
                ? SettingsEvidenceUnavailableReason.NoCandidates
                : SettingsEvidenceUnavailableReason.NoneQualified;
        }

        if (result.UnavailableReason is null)
        {
            List<SettingsEvidenceConfidenceReason> reasons = [];
            var driftMaterial = SettingsEvidenceCalculator.Material(
                days.Select(d => SettingsEvidenceCalculator.Share(d.Change / d.Isf / d.Hours, d.Need)));
            if (result.IsfDependent && driftMaterial)
                reasons.Add(SettingsEvidenceConfidenceReason.IsfDependent);
            if (context.FastingUnverifiable)
                reasons.Add(SettingsEvidenceConfidenceReason.FastingUnverifiable);
            var carriedMaterial = SettingsEvidenceCalculator.Material(
                days.Select(d => SettingsEvidenceCalculator.Share(d.Carried, d.Need)));
            if (carriedMaterial)
                reasons.Add(SettingsEvidenceConfidenceReason.CarriedInsulinMaterial);
            var needs = days.Select(d => d.Need).ToList();
            SettingsEvidenceCalculator.Rate(
                result, needs.Average(), needs, higherMeansMoreInsulin: true,
                materialBorrowed: driftMaterial || carriedMaterial, reasons);
        }

        if (context.ListEpisodes)
            result.Days = [.. days.Select(d =>
            {
                var day = context.Episode<BasalEvidenceDay>(d.Start, d.End, d.Need, d.Scheduled);
                day.Hours = SlotSummary.Round(d.Hours);
                day.NetLoopCorrection = SlotSummary.Round(d.Correction);
                day.CarriedInsulin = SlotSummary.Round(d.Carried);
                day.GlucoseChange = SlotSummary.Round(d.Change);
                day.Isf = SlotSummary.Round(d.Isf);
                day.IsfDependent = d.IsfDependent;
                return day;
            })];
        return result;
    }

    private sealed record Day(
        long Start,
        long End,
        double Hours,
        double Scheduled,
        double Correction,
        double Carried,
        double Change,
        double Isf,
        bool IsfDependent,
        double Need);
}

/// <summary>
/// The basal need the ISF and carb ratio measure insulin above: the schedule, scaled in each
/// two-hour block by that block's observed need over its profile rate where the need is
/// <see cref="SettingsEvidenceCalculator.Lendable"/>, and in every other block by the mean of those
/// ratios. A schedule wrong by much the same share all day is the usual case, and a block with no
/// fasting time of its own, as a daytime block often has none, has nothing better to go by; but a
/// basal need that differs through the day, a dawn rise, say, makes the mean wrong, so an episode
/// that borrows it is basal-dependent.
/// </summary>
internal sealed class BasalBaseline
{
    private readonly double?[] _ratios;
    private readonly double _mean;

    private BasalBaseline(double?[] ratios)
    {
        _ratios = ratios;
        _mean = ratios.OfType<double>().Average();
    }

    /// <returns>Null when no block's need is confident: insulin is then measured above the schedule.</returns>
    public static BasalBaseline? Of(List<BasalEvidenceBlock> blocks)
    {
        double?[] ratios =
        [
            .. blocks.Select(b => SettingsEvidenceCalculator.Lendable(b) && b.Profile is > 0
                ? b.Observed!.Value / b.Profile.Value
                : (double?)null),
        ];
        return ratios.Any(r => r is not null) ? new BasalBaseline(ratios) : null;
    }

    /// <summary>
    /// The basal need above the schedule over <c>[start, end)</c>, U, negative where the schedule
    /// exceeds it; and whether any of it was scaled by the mean ratio, for want of the block's own.
    /// </summary>
    public (double Excess, bool Borrowed) Excess(SettingsEvidenceContext context, long start, long end)
    {
        double excess = 0;
        var borrowed = false;
        LocalSlotBinning.Distribute(start, end, context.Zone, ObservedBasalNeed.BlockHours * 60, (sliceStart, local, ms) =>
        {
            var scheduled = context.ScheduledUnits(sliceStart, sliceStart + ms);
            var own = _ratios[local.Hour / ObservedBasalNeed.BlockHours];
            borrowed |= own is null && scheduled > 0;
            excess += scheduled * ((own ?? _mean) - 1);
        });
        return (excess, borrowed);
    }
}
