using Nocturne.Core.Clinical.Treatments;
using Nocturne.Core.Models;
using Nocturne.Core.Models.LoopAnalysis;

namespace Nocturne.Core.Clinical.LoopAnalysis;

/// <summary>Everything the Settings evidence page is computed from, fetched once for every range it covers.</summary>
/// <remarks>
/// The series must reach past each end of the range: a basal block looks back
/// <see cref="ObservedBasalNeed.CarbFreeMs"/> for carbs, a climb's own length before that, and
/// <see cref="ObservedBasalNeed.ManualBolusFreeMs"/> for boluses; every window reads
/// <see cref="ObservedCarbRatio.WindowAfterMs"/> past its anchor.
/// </remarks>
public sealed record SettingsEvidenceInputs
{
    /// <summary>Glucose, insulin, carbs, IOB/COB and the target range in force.</summary>
    public required DailyProfileInputs Series { get; init; }

    /// <summary>The overrides, temporary targets, exercise, illness, open loop and data exclusions an episode is rejected for.</summary>
    public LoopAnalysisMaskSources Sources { get; init; } = new();

    /// <summary>The ISF and carb ratio in force.</summary>
    public required TherapyTimeline Therapy { get; init; }
}

/// <summary>
/// The Settings evidence page: the basal, ISF and carb ratio the user's own data shows, beside the
/// profile's. Each is an observation with its n, spread, confidence, rejections and episodes, never
/// an instruction.
/// </summary>
/// <remarks>
/// <para>
/// A candidate episode is tested against its section's rules in order and counted against the
/// first it fails, so a section's rejections and its n add up to its candidates. A rule's window is
/// the episode's whole window: an override, say, anywhere in it rejects it.
/// </para>
/// <para>
/// The ISF and carb ratio read insulin above the basal need, not above the schedule: a schedule
/// short by <c>x</c> U/h has the loop topping it up, and would credit a four-hour window with
/// <c>4x</c> U the meal or correction did not use. Where no basal need is observed with confidence
/// they fall back to the schedule (<see cref="BasalBaseline"/>).
/// </para>
/// </remarks>
public static class SettingsEvidenceCalculator
{
    /// <summary>
    /// The fewest qualifying episodes a median is reported from: with fewer, the median and
    /// quartiles are one or two episodes' own values, not a spread.
    /// </summary>
    public const int MinimumEpisodes = 3;

    /// <summary>
    /// Below this many episodes a figure is <see cref="SettingsEvidenceConfidence.Low"/> whatever
    /// else holds: with five or fewer, the quartiles sit on single episodes, so a narrow spread says
    /// little about how far the median would move with the next one.
    /// </summary>
    public const int LowConfidenceBelowEpisodes = 6;

    /// <summary>
    /// Episodes from which a figure's n no longer lowers its confidence: ten gives quartiles that
    /// one unusual day moves by no more than a neighbouring value.
    /// </summary>
    public const int ConfidentEpisodes = 10;

    /// <summary>
    /// The widest interquartile range, as a share of the observed value, that does not lower
    /// confidence. Half the value is the spread at which the lower and upper quartile would call for
    /// settings apart by a factor of three, past any one adjustment a clinician would make.
    /// </summary>
    public const double MaxRelativeIqr = 0.5;

    /// <summary>
    /// The least insulin an ISF or carb ratio is read against, U. CGM noise of about 10 mg/dL at
    /// either end of a window is a fifth of a typical ISF against half a unit, and grows past any
    /// signal below it.
    /// </summary>
    public const double MinimumInsulinUnits = 0.5;

    /// <summary>
    /// The largest typical share of a window's insulin still on board at its end before the figure
    /// is read as resting on the IOB curve: past 30%, the curve's shape, not the glucose, decides
    /// much of what acted, and a DIA set long or short moves the figure with it.
    /// </summary>
    public const double MaxResidualIobShare = 0.3;

    /// <summary>
    /// The least share of a figure a borrowed input must carry, as a median across its episodes,
    /// for the figure to be read as resting on it: the glucose-change term converted at a borrowed
    /// ISF, or a basal need borrowed from the basal figure. Below 15%, even an input 50% wrong moves
    /// the figure by less than the spread a good figure already shows.
    /// </summary>
    public const double MaterialShare = 0.15;

    /// <summary>
    /// What a carb ratio's glucose-change share is multiplied by before it is judged against
    /// <see cref="MaterialShare"/>, where no ISF in the range is observed and every conversion is at
    /// the profile's, unscaled. Such an ISF can be off by half or double with nothing in the range to
    /// say so, and a share read at an ISF twice the truth looks half its size; weighing it double
    /// flags a term at half the material share.
    /// </summary>
    public const double ProfileOnlyGlucoseWeight = 2.0;

    /// <summary>
    /// The least share of the range's days with any carb entry for fasting to be read off the carb
    /// entries: below a fifth, entries are the exception, a snack logged now and then, and most
    /// meals are known only by the glucose they raise.
    /// </summary>
    public const double MinimumCarbDayShare = 0.2;

    /// <summary>
    /// How far a figure must sit from the profile's, as a share of it, to be read as pointing
    /// towards more insulin: within 10%, the difference is inside the spread a right profile reads
    /// with, and no step a setting is changed by.
    /// </summary>
    public const double DirectionDeadband = 0.10;

    /// <summary>The span before a window's end its closing trend is read over.</summary>
    public const long EndTrendSpanMs = 30 * LocalSlotBinning.MinuteMs;

    /// <summary>
    /// The fastest glucose may still be moving over <see cref="EndTrendSpanMs"/> before a window's
    /// end, as a median across a figure's episodes, for the window to have caught the whole response:
    /// past 1 mg/dL a minute, where a CGM's trend arrow leaves flat, insulin or carbs were still
    /// acting when the window closed.
    /// </summary>
    public const double MaxEndTrendMgdlPerMinute = 1;

    /// <summary>
    /// The largest share of candidates rejected for another intake or bolus in their window before
    /// the survivors are read as atypical: past half, the episodes that qualified are the quiet
    /// exceptions, and their figure may not hold for the user's ordinary days.
    /// </summary>
    public const double MaxNotIsolatedShare = 0.5;

    /// <summary>
    /// How many times the basal need and the ISF are read in turn. Each is read with the other: the
    /// basal need converts glucose's drift at the observed ISF, and the ISF measures insulin above
    /// the observed basal need. The first pass reads the basal need at the profile's ISF; the second
    /// at the ISF that read gave, which leaves both within a few percent of where further passes
    /// would settle.
    /// </summary>
    public const int Passes = 2;

    /// <summary>The span before a correction or meal its glucose must be stable over.</summary>
    public const long StableSpanMs = 15 * LocalSlotBinning.MinuteMs;

    /// <summary>
    /// The fastest glucose may move over <see cref="StableSpanMs"/> before a correction or meal and
    /// still count as stable: 1 mg/dL a minute, where a CGM's trend arrow turns from flat. Faster,
    /// and the window's glucose change is partly a trend already under way: a correction given
    /// while glucose still climbs is answering food still absorbing.
    /// </summary>
    public const double MaxStartTrendMgdlPerMinute = 1;

    /// <summary>The thresholds this calculator's rules and windows apply.</summary>
    public static SettingsEvidenceRules Rules() => new()
    {
        BasalCarbFreeMs = ObservedBasalNeed.CarbFreeMs,
        BasalManualBolusFreeMs = ObservedBasalNeed.ManualBolusFreeMs,
        IsfCarbFreeBeforeMs = ObservedIsf.CarbFreeBeforeMs,
        IsfMinimumWindowMs = ObservedIsf.MinimumWindowMs,
        IsfWindowMs = ObservedIsf.WindowMs,
        CarbRatioWindowMs = ObservedCarbRatio.WindowAfterMs,
        LowMgdl = ObservedIsf.LowMgdl,
        StartLowMgdl = ObservedCarbRatio.StartLowMgdl,
        StartHighMgdl = ObservedCarbRatio.StartHighMgdl,
    };

    /// <param name="listEpisodes">Whether to list the episodes; the comparison range's are not shown.</param>
    public static SettingsEvidenceRange Calculate(
        SettingsEvidenceInputs inputs, LoopAnalysisMask mask, long from, long to, bool listEpisodes = true)
    {
        var context = new SettingsEvidenceContext(inputs, mask, from, to, listEpisodes);
        var conversion = IsfConversion.Of([]);
        List<BasalEvidenceBlock> basal = [];
        List<IsfEvidenceBlock> isf = [];
        BasalBaseline? baseline = null;
        for (var pass = 0; pass < Passes; pass++)
        {
            basal = ObservedBasalNeed.Calculate(context, conversion);
            baseline = BasalBaseline.Of(basal);
            isf = ObservedIsf.Calculate(context, baseline);
            conversion = IsfConversion.Of(isf);
        }
        return new SettingsEvidenceRange
        {
            From = DateTimeOffset.FromUnixTimeMilliseconds(from).UtcDateTime,
            To = DateTimeOffset.FromUnixTimeMilliseconds(to).UtcDateTime,
            Basal = basal,
            Isf = isf,
            CarbRatio = ObservedCarbRatio.Calculate(context, conversion, baseline),
        };
    }

    /// <summary>
    /// Whether another figure may be read with this one: the basal need the ISF and carb ratio
    /// measure insulin above, and the ISF a glucose change is converted at. A figure is lent only
    /// when it is <see cref="SettingsEvidenceConfidence.High"/>, or
    /// <see cref="SettingsEvidenceConfidence.Moderate"/> for some reason other than its n, so a
    /// handful of episodes never moves another figure.
    /// </summary>
    internal static bool Lendable(SettingsEvidenceFigure figure) =>
        figure.Observed is not null
        && (figure.Confidence == SettingsEvidenceConfidence.High
            || (figure.Confidence == SettingsEvidenceConfidence.Moderate && figure.N >= ConfidentEpisodes));

    /// <summary>Whether glucose moved from <paramref name="before"/> to <paramref name="at"/>, <see cref="StableSpanMs"/> apart, slowly enough to be stable.</summary>
    internal static bool Stable(double before, double at) =>
        Math.Abs(at - before) <= MaxStartTrendMgdlPerMinute * StableSpanMs / LocalSlotBinning.MinuteMs;

    public static SettingsEvidenceBlock BlockOf(int localHour) => localHour switch
    {
        < 6 => SettingsEvidenceBlock.Overnight,
        < 11 => SettingsEvidenceBlock.Morning,
        < 17 => SettingsEvidenceBlock.Midday,
        _ => SettingsEvidenceBlock.Evening,
    };

    /// <summary>The local hours a block spans, the end exclusive; the inverse of <see cref="BlockOf"/>.</summary>
    public static (int Start, int End) HoursOf(SettingsEvidenceBlock block) => block switch
    {
        SettingsEvidenceBlock.Overnight => (0, 6),
        SettingsEvidenceBlock.Morning => (6, 11),
        SettingsEvidenceBlock.Midday => (11, 17),
        _ => (17, 24),
    };

    /// <summary>
    /// Sets an available figure's observed value, spread and confidence: one level below
    /// <see cref="SettingsEvidenceConfidence.High"/> for each reason it falls short, and
    /// <see cref="SettingsEvidenceConfidence.Low"/> below <see cref="LowConfidenceBelowEpisodes"/>.
    /// The spread is judged against the observed value, whether that is a median or, for a basal
    /// rate, a mean.
    /// </summary>
    /// <param name="higherMeansMoreInsulin">
    /// True where an observed value above the profile's calls for more insulin (a basal rate); false
    /// where below does (an ISF, a carb ratio).
    /// </param>
    /// <param name="materialBorrowed">
    /// A borrowed input carries at least <see cref="MaterialShare"/> of the figure (<see cref="Material"/>).
    /// Such a figure pointing towards more insulin is lowered one level, since an error in the input
    /// it borrowed would carry into a figure in the riskier direction.
    /// </param>
    internal static void Rate(
        SettingsEvidenceFigure figure,
        double observed,
        List<double> values,
        bool higherMeansMoreInsulin,
        bool materialBorrowed,
        IEnumerable<SettingsEvidenceConfidenceReason> reasons)
    {
        var spread = SlotSummary.Of(values)!;
        figure.Observed = SlotSummary.Round(observed);
        figure.Spread = spread;
        if (values.Count < ConfidentEpisodes)
            figure.ConfidenceReasons.Add(SettingsEvidenceConfidenceReason.FewEpisodes);
        if (spread.P75 - spread.P25 > MaxRelativeIqr * Math.Abs(observed))
            figure.ConfidenceReasons.Add(SettingsEvidenceConfidenceReason.WideSpread);
        figure.ConfidenceReasons.AddRange(reasons);
        figure.Confidence = values.Count < LowConfidenceBelowEpisodes
            ? SettingsEvidenceConfidence.Low
            : figure.ConfidenceReasons.Count switch
            {
                0 => SettingsEvidenceConfidence.High,
                1 => SettingsEvidenceConfidence.Moderate,
                _ => SettingsEvidenceConfidence.Low,
            };

        // Against the rounded figure, as shown beside the profile's, so a figure reads the way it points.
        var shown = figure.Observed.Value;
        var departs = figure.Profile is > 0 and var profile
            && Math.Abs(shown / profile - 1) >= DirectionDeadband;
        var moreInsulin = departs && (higherMeansMoreInsulin ? shown > figure.Profile : shown < figure.Profile);
        figure.TowardsMoreInsulin = moreInsulin;
        figure.TowardsLessInsulin = departs && !moreInsulin;
        if (moreInsulin && materialBorrowed)
        {
            figure.Confidence = figure.Confidence == SettingsEvidenceConfidence.High
                ? SettingsEvidenceConfidence.Moderate
                : SettingsEvidenceConfidence.Low;
            figure.ConfidenceReasons.Add(SettingsEvidenceConfidenceReason.MoreInsulinOnBorrowedInput);
        }
    }

    /// <summary>Whether the median of <paramref name="shares"/> reaches <see cref="MaterialShare"/>.</summary>
    internal static bool Material(IEnumerable<double> shares) =>
        SlotSummary.Of([.. shares.Where(double.IsFinite)])?.P50 >= MaterialShare;

    /// <summary>|<paramref name="part"/>| as a share of |<paramref name="whole"/>|; infinite where the whole is zero.</summary>
    internal static double Share(double part, double whole) =>
        whole == 0 ? double.PositiveInfinity : Math.Abs(part) / Math.Abs(whole);

    /// <summary>The reason a figure of episodes has no value, when it has none.</summary>
    internal static SettingsEvidenceUnavailableReason? Unavailable(int candidates, int qualified) =>
        candidates == 0 ? SettingsEvidenceUnavailableReason.NoCandidates
        : qualified == 0 ? SettingsEvidenceUnavailableReason.NoneQualified
        : qualified < MinimumEpisodes ? SettingsEvidenceUnavailableReason.TooFew
        : null;
}

/// <summary>Counts each rule's rejections, every rule listed whether it rejected any or not.</summary>
internal sealed class RejectionTally(IReadOnlyList<SettingsEvidenceRule> rules)
{
    private readonly Dictionary<SettingsEvidenceRule, int> _counts = rules.ToDictionary(r => r, _ => 0);

    public int Candidates { get; private set; }

    public void Candidate() => Candidates++;

    public void Reject(SettingsEvidenceRule rule) => _counts[rule]++;

    /// <summary>The rejections for <paramref name="rule"/>; none for a rule the section does not apply.</summary>
    public int this[SettingsEvidenceRule rule] => _counts.GetValueOrDefault(rule);

    public List<SettingsEvidenceRejection> ToList() =>
        [.. rules.Select(r => new SettingsEvidenceRejection { Rule = r, Count = _counts[r] })];
}

/// <summary>Insulin that acted over a window, read between the IOB ticks at its ends.</summary>
/// <param name="BasalDependent">
/// For some of the window the basal need was not observed with confidence for its own time; see
/// <see cref="BasalBaseline"/>.
/// </param>
internal readonly record struct ActedInsulin(
    NetInsulin Net, double IobAtStart, double IobAtEnd, double BasalExcess, bool BasalDependent)
{
    public double Acted => Net.Total + IobAtStart - IobAtEnd - BasalExcess;
}

/// <summary>What every section reads, indexed once per range.</summary>
internal sealed class SettingsEvidenceContext
{
    private readonly IntervalSet _overrides, _temporaryTargets, _exercise, _illness, _openLoop;

    public SettingsEvidenceContext(SettingsEvidenceInputs inputs, LoopAnalysisMask mask, long from, long to, bool listEpisodes)
    {
        Inputs = inputs;
        Mask = mask;
        From = from;
        To = to;
        ListEpisodes = listEpisodes;
        Exclusions = IntervalSet.Of(inputs.Sources.DataExclusions);
        Series = new EventSeries(inputs.Series, Exclusions);
        CarbMills = EventAnchors.CarbMills(inputs.Series.Carbs);
        _overrides = IntervalSet.Of(inputs.Sources.Overrides);
        _temporaryTargets = IntervalSet.Of(inputs.Sources.TemporaryTargets);
        _exercise = IntervalSet.Of(inputs.Sources.Exercise);
        _illness = IntervalSet.Of(inputs.Sources.Illness);
        _openLoop = IntervalSet.Of(inputs.Sources.OpenLoop);
    }

    public SettingsEvidenceInputs Inputs { get; }
    public LoopAnalysisMask Mask { get; }
    public long From { get; }
    public long To { get; }
    public bool ListEpisodes { get; }
    public IntervalSet Exclusions { get; }
    public EventSeries Series { get; }
    public long[] CarbMills { get; }
    public TimeZoneInfo Zone => Inputs.Series.Zone;

    /// <summary>
    /// Too few of the range's days hold a carb entry (<see cref="SettingsEvidenceCalculator.MinimumCarbDayShare"/>)
    /// for whether a block was fasting to be read off them, rather than off glucose alone.
    /// </summary>
    public bool FastingUnverifiable
    {
        get
        {
            var days = LocalSlotBinning.LocalDay(To - 1, Zone).DayNumber - LocalSlotBinning.LocalDay(From, Zone).DayNumber + 1;
            var withCarbs = CarbMills.Where(m => m >= From && m < To).Select(m => LocalSlotBinning.LocalDay(m, Zone)).Distinct().Count();
            return withCarbs < SettingsEvidenceCalculator.MinimumCarbDayShare * days;
        }
    }

    /// <summary>
    /// Every intake or dose that keeps an episode around it from being isolated, sorted: the
    /// meals, correction boluses and meal-like climbs, announced or not, the Meals and events page
    /// isolates its events by (<see cref="EventAnchors.IsIsolated"/>).
    /// </summary>
    public IReadOnlyList<long> Isolating => _isolating ??=
    [
        .. EventAnchors.Meals(Inputs.Series.Carbs).Select(m => m.Mills)
            .Concat(EventAnchors.CorrectionBoluses(Insulin.Doses, Inputs.Series.Carbs).Select(c => c.Mills))
            .Concat(Climbs.Select(c => c.Start))
            .Order(),
    ];

    /// <summary>
    /// Every meal-like climb a carb entry did not cause (<see cref="RiseSearch.Climbs"/>), an intake
    /// of its own, logged or not, from its start to its peak: the highest reading within
    /// <see cref="UnannouncedRises.MergeMs"/> of the start, the span a later climb would be merged into.
    /// </summary>
    public IReadOnlyList<MillsInterval> Climbs => _climbs ??=
    [
        .. UnannouncedRises.Find(Series.Readings, Exclusions, Inputs.Series.Carbs, Insulin, requireCorroboration: false, Series.CobAt)
            .Climbs.Select(start => new MillsInterval(start, PeakAt(start, start + UnannouncedRises.MergeMs))),
    ];

    private long[]? _isolating;
    private MillsInterval[]? _climbs;

    public NetInsulinAboveSchedule Insulin => Series.Insulin;
    public TherapySnapshot TherapyAt(long mills) => Inputs.Therapy.SnapshotAt(mills);

    /// <summary>
    /// The first of the open loop (when <paramref name="openLoop"/>), an override, a temporary
    /// target, exercise or illness that overlaps <c>[start, end)</c>.
    /// </summary>
    public SettingsEvidenceRule? Activity(long start, long end, bool openLoop) =>
        openLoop && _openLoop.Overlaps(start, end) ? SettingsEvidenceRule.OpenLoop
        : _overrides.Overlaps(start, end) ? SettingsEvidenceRule.Override
        : _temporaryTargets.Overlaps(start, end) ? SettingsEvidenceRule.TemporaryTarget
        : _exercise.Overlaps(start, end) ? SettingsEvidenceRule.Exercise
        : _illness.Overlaps(start, end) ? SettingsEvidenceRule.Illness
        : null;

    /// <summary>
    /// <see cref="SettingsEvidenceRule.OutsideRange"/> for a window reaching outside the range, else
    /// <see cref="SettingsEvidenceRule.Filtered"/> for one reaching outside the report's filter.
    /// </summary>
    public SettingsEvidenceRule? Outside(long start, long end) =>
        start < From || end > To ? SettingsEvidenceRule.OutsideRange
        : !Mask.Analysed.Covers(start, end) ? SettingsEvidenceRule.Filtered
        : null;

    /// <summary>Glucose's change over <see cref="SettingsEvidenceCalculator.EndTrendSpanMs"/> before <paramref name="end"/>, mg/dL a minute.</summary>
    public double? EndTrend(long end) =>
        Series.GlucoseAt(end) is { } last && Series.GlucoseAt(end - SettingsEvidenceCalculator.EndTrendSpanMs) is { } before
            ? (last - before) / (SettingsEvidenceCalculator.EndTrendSpanMs / (double)LocalSlotBinning.MinuteMs)
            : null;

    /// <summary>
    /// The lowest reading from <paramref name="start"/> up to and including the drop's end reading
    /// (<see cref="GlucoseDrop.EndReadingMills"/>), or null where there is none.
    /// </summary>
    public double? LowestGlucose(long start, long end)
    {
        var readings = Series.Readings;
        var last = GlucoseDrop.EndReadingMills(Series, end);
        double? lowest = null;
        for (var i = Series.FirstReadingFrom(start); i < readings.Count && readings[i].Mills <= last; i++)
            lowest = lowest is { } low ? Math.Min(low, readings[i].Mgdl) : readings[i].Mgdl;
        return lowest;
    }

    /// <summary>
    /// IOB at the tick standing at <paramref name="start"/>, plus net insulin above schedule
    /// delivered after that tick up to and including the tick standing at <paramref name="end"/>,
    /// less IOB there, less what <paramref name="baseline"/> says the basal need exceeded the
    /// schedule by meanwhile. A calculated tick's IOB holds every dose up to and including its
    /// instant; a device tick's is the latest snapshot at or before it. Reading the insulin between
    /// the ticks rather than between the instants counts each dose once, up to the age of a
    /// device snapshot. The two ticks must come from the same source, or their difference is the
    /// gap between two models rather than insulin that acted.
    /// </summary>
    public (ActedInsulin? Acted, SettingsEvidenceRule? Rejected) InsulinActed(long start, long end, BasalBaseline? baseline = null)
    {
        if (Series.TickAt(start) is not { } first || Series.TickAt(end) is not { } last)
            return (null, SettingsEvidenceRule.IobUnknown);
        if (first.IobSource != last.IobSource)
            return (null, SettingsEvidenceRule.IobSourceChanged);
        var (from, to) = (first.Mills + 1, last.Mills + 1);
        var net = Series.NetInsulinOver(from, to, DailyProfileCalculator.InsulinSummaryMinimumCoverage);
        if (net is null)
            return (null, SettingsEvidenceRule.InsulinUncovered);
        var (excess, borrowed) = baseline?.Excess(this, first.Mills, last.Mills) ?? (0, true);
        return (new ActedInsulin(net, first.Iob, last.Iob, excess, borrowed), null);
    }

    /// <summary>Scheduled basal over the time in <c>[start, end)</c> both delivery and the schedule are known, U.</summary>
    public double ScheduledUnits(long start, long end)
    {
        var pieces = Insulin.Pieces;
        double units = 0;
        for (var i = SortedSearch.FirstEndingAfter(pieces, start, static p => p.End); i < pieces.Count && pieces[i].Start < end; i++)
        {
            if (pieces[i].HasDelta)
                units += pieces[i].Scheduled!.Value * (Math.Min(end, pieces[i].End) - Math.Max(start, pieces[i].Start)) / LocalSlotBinning.HourMs;
        }
        return units;
    }

    /// <summary>
    /// Completes an ISF or carb ratio block from the episodes that qualified in it: its n, profile,
    /// rejections and, unless <paramref name="unavailable"/>, the median and its confidence.
    /// </summary>
    /// <param name="profileAt">The profile's setting in force at an instant.</param>
    /// <param name="isfDependent">Some episode converted glucose at an ISF from the profile.</param>
    /// <param name="glucoseTermMaterial">
    /// The glucose-change term carries a material share of the figure
    /// (<see cref="SettingsEvidenceCalculator.Material"/>), so an ISF-dependent figure is less sure.
    /// </param>
    /// <param name="materialBorrowed">
    /// See <see cref="SettingsEvidenceCalculator.Rate"/>. A basal-dependent episode, glucose still
    /// moving at the window's end, and corrections left out for going low
    /// (<see cref="SettingsEvidenceConfidenceReason.ManyWentLow"/>) also count: none of these errors
    /// can be sized from the data, so none can be shown to be small.
    /// </param>
    public T Finish<T, TEpisode>(
        T figure,
        SettingsEvidenceBlock block,
        List<TEpisode> episodes,
        RejectionTally tally,
        SettingsEvidenceUnavailableReason? unavailable,
        Func<long, double> profileAt,
        bool isfDependent,
        bool glucoseTermMaterial,
        bool materialBorrowed)
        where T : InsulinActedFigure
        where TEpisode : InsulinActedEpisode
    {
        _namedBlocks ??= DayBlocks(hour => (int)SettingsEvidenceCalculator.BlockOf(hour));
        (figure.StartHour, figure.EndHour) = SettingsEvidenceCalculator.HoursOf(block);
        figure.N = episodes.Count;
        figure.Profile = ProfileOver(
            _namedBlocks.Where(t => t.Block == (int)block).Select(t => (t.Start, t.End)),
            t => profileAt(t)) is { } profile ? SlotSummary.Round(profile) : null;
        figure.UnavailableReason = unavailable;
        figure.Rejections = tally.ToList();
        figure.BasalDependent = episodes.Any(e => e.BasalDependent);
        figure.IsfDependent = isfDependent;
        if (unavailable is not null)
            return figure;

        List<SettingsEvidenceConfidenceReason> reasons = [];
        if (isfDependent && glucoseTermMaterial)
            reasons.Add(SettingsEvidenceConfidenceReason.IsfDependent);
        if (figure.BasalDependent)
            reasons.Add(SettingsEvidenceConfidenceReason.BasalDependent);
        if (SlotSummary.Of([.. episodes
                .Where(e => e.InsulinActed + e.ResidualIob > 0)
                .Select(e => e.ResidualIob / (e.InsulinActed + e.ResidualIob))])?.P50 > SettingsEvidenceCalculator.MaxResidualIobShare)
            reasons.Add(SettingsEvidenceConfidenceReason.ResidualIob);
        if (tally[SettingsEvidenceRule.NotIsolated] > SettingsEvidenceCalculator.MaxNotIsolatedShare * tally.Candidates)
            reasons.Add(SettingsEvidenceConfidenceReason.MostNotIsolated);
        var stillMoving = SlotSummary.Of([.. episodes.Select(e => e.EndTrend).OfType<double>().Select(Math.Abs)])?.P50
            > SettingsEvidenceCalculator.MaxEndTrendMgdlPerMinute;
        if (stillMoving)
            reasons.Add(SettingsEvidenceConfidenceReason.StillMovingAtEnd);
        var wentLow = tally[SettingsEvidenceRule.WentLow];
        var manyWentLow = SettingsEvidenceCalculator.Share(wentLow, wentLow + episodes.Count) >= SettingsEvidenceCalculator.MaterialShare;
        if (manyWentLow)
            reasons.Add(SettingsEvidenceConfidenceReason.ManyWentLow);

        var values = episodes.Select(e => e.Value).ToList();
        SettingsEvidenceCalculator.Rate(
            figure, SlotSummary.Of(values)!.P50, values, higherMeansMoreInsulin: false,
            materialBorrowed || figure.BasalDependent || stillMoving || manyWentLow, reasons);
        return figure;
    }

    private List<(int Block, long Start, long End)>? _namedBlocks;

    /// <summary>
    /// Every local day of the range cut into blocks by local hour, in time order: each block is the
    /// instants whose local hour <paramref name="blockOf"/> maps to it, so across a DST change a block
    /// runs an hour short or long. Blocks are not clipped to the range.
    /// </summary>
    public List<(int Block, long Start, long End)> DayBlocks(Func<int, int> blockOf)
    {
        var blocks = new List<(int Block, long Start, long End)>();
        var (_, starts) = LocalSlotBinning.DayStarts(From, To, Zone);
        for (var d = 0; d < starts.Length - 1; d++)
        {
            LocalSlotBinning.Distribute(
                starts[d],
                starts[d + 1],
                Zone,
                60,
                (start, local, ms) =>
                {
                    var block = blockOf(local.Hour);
                    if (blocks.Count > 0 && blocks[^1].Block == block && blocks[^1].End == start)
                        blocks[^1] = blocks[^1] with { End = start + ms };
                    else
                        blocks.Add((block, start, start + ms));
                });
        }
        return blocks;
    }

    /// <summary>
    /// The median of <paramref name="value"/> at every five minutes of the block's time in the range,
    /// where it is known: the profile's setting over the time it was in force.
    /// </summary>
    public double? ProfileOver(IEnumerable<(long Start, long End)> blockTime, Func<long, double?> value)
    {
        var values = new List<double>();
        foreach (var (start, end) in blockTime)
        {
            for (var t = Math.Max(start, From); t < Math.Min(end, To); t += EventWindow.SlotMs)
            {
                if (value(t) is { } v)
                    values.Add(v);
            }
        }
        return SlotSummary.Of(values)?.P50;
    }

    /// <summary>The scheduled basal rate at an instant, U/h, where one is known.</summary>
    public double? ScheduledAt(long mills)
    {
        var pieces = Insulin.Pieces;
        var i = SortedSearch.FirstEndingAfter(pieces, mills, static p => p.End);
        return i < pieces.Count && pieces[i].Start <= mills ? pieces[i].Scheduled : null;
    }

    public T Episode<T>(long start, long end, double value, double? profile) where T : SettingsEvidenceEpisode, new()
    {
        var (date, time) = LocalSlotBinning.DayInReview(start, Zone);
        return new T
        {
            At = DateTimeOffset.FromUnixTimeMilliseconds(start).UtcDateTime,
            End = DateTimeOffset.FromUnixTimeMilliseconds(end).UtcDateTime,
            Date = date,
            Time = time,
            Value = SlotSummary.Round(value),
            Profile = profile is { } p ? SlotSummary.Round(p) : null,
        };
    }

    /// <param name="standingIob">IOB just before the window's own doses: what was already on board.</param>
    public void Fill(InsulinActedEpisode episode, long end, double startGlucose, double endGlucose, ActedInsulin acted, double standingIob)
    {
        episode.ResidualIob = SlotSummary.Round(Math.Max(0, acted.IobAtEnd - standingIob));
        episode.EndTrend = EndTrend(end) is { } trend ? SlotSummary.Round(trend) : null;
        episode.StartGlucose = SlotSummary.Round(startGlucose);
        episode.EndGlucose = SlotSummary.Round(endGlucose);
        episode.NetInsulin = SlotSummary.Round(acted.Net.Total);
        episode.IobAtStart = SlotSummary.Round(acted.IobAtStart);
        episode.IobAtEnd = SlotSummary.Round(acted.IobAtEnd);
        episode.BasalExcess = SlotSummary.Round(acted.BasalExcess);
        episode.BasalDependent = acted.BasalDependent;
        episode.InsulinActed = SlotSummary.Round(acted.Acted);
    }

    /// <summary>When the highest reading in <c>[start, end)</c> came; <paramref name="start"/> where there is none.</summary>
    private long PeakAt(long start, long end)
    {
        var readings = Series.Readings;
        var peak = start;
        double? highest = null;
        for (var i = Series.FirstReadingFrom(start); i < readings.Count && readings[i].Mills < end; i++)
        {
            if (highest is null || readings[i].Mgdl > highest)
            {
                highest = readings[i].Mgdl;
                peak = readings[i].Mills;
            }
        }
        return peak;
    }
}
