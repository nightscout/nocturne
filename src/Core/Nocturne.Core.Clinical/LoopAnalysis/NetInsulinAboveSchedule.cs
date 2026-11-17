using Nocturne.Core.Clinical.Statistics;
using Nocturne.Core.Clinical.Treatments;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Basal;
using Nocturne.Core.Models.V4;

namespace Nocturne.Core.Clinical.LoopAnalysis;

/// <summary>A constant rate, U/h, over <c>[Start, End)</c>.</summary>
public readonly record struct RateSegment(long Start, long End, double Rate);

public readonly record struct DeliveredBasalSegment(long Start, long End, double Rate, BasalDeliveryOrigin Origin);

public readonly record struct InsulinDose(long Mills, double Units, bool Automatic, Guid? CorrelationId = null);

/// <summary>
/// Where insulin above schedule came from. Every AID system's correction is
/// <see cref="AlgorithmTemp"/> plus <see cref="AutomaticBolus"/>, whichever of the two it uses.
/// </summary>
public enum NetInsulinOrigin
{
    /// <summary>The algorithm's temp basals, zero-temps included.</summary>
    AlgorithmTemp,
    AutomaticBolus,
    ManualTemp,
    Suspend,

    /// <summary>
    /// No temp running, whether the pump said so or a gap was filled from the schedule. Zero
    /// except where the baseline is scaled by an override the pump schedule does not carry.
    /// </summary>
    Inferred,
    ManualBolus,
}

/// <summary>Insulin above schedule over one interval, U, by origin.</summary>
public sealed record NetInsulin(
    double AlgorithmTemp,
    double AutomaticBolus,
    double ManualTemp,
    double Suspend,
    double Inferred,
    double ManualBolus,
    long CoveredMs)
{
    /// <summary>The algorithm's correction alone: the figure that means the same for every AID system.</summary>
    public double NetLoopCorrection => AlgorithmTemp + AutomaticBolus;

    public double Total => AlgorithmTemp + AutomaticBolus + ManualTemp + Suspend + Inferred + ManualBolus;
}

/// <summary>
/// One stretch of the basal timeline with a constant delivered and scheduled rate. Either is null
/// where it is unknown; only a piece with both has a delta.
/// </summary>
public readonly record struct BasalPiece(
    long Start, long End, double? Delivered, double? Scheduled, NetInsulinOrigin? Origin)
{
    public bool HasDelta => Delivered.HasValue && Scheduled.HasValue;
}

/// <summary>What the pump's records say about basal delivery over a window.</summary>
/// <param name="Recorded">Time a temp basal record covers.</param>
/// <param name="Unknown">Recorded time whose delivery is unknown: a relative temp with no rate.</param>
/// <param name="Inferable">
/// Unrecorded time the schedule is taken to have run through; see
/// <see cref="NetInsulinAboveSchedule.MaxInferredGapMs"/>.
/// </param>
public sealed record BasalEvidence(IntervalSet Recorded, IntervalSet Unknown, IntervalSet Inferable);

/// <summary>
/// Delivered basal minus scheduled basal, plus every bolus, over any interval, split by
/// <see cref="NetInsulinOrigin"/>. The definition every Loop Analysis page reads its insulin from.
/// </summary>
/// <remarks>
/// Time where either delivery or the schedule is unknown contributes no basal delta, rather than
/// a delta against zero; <see cref="NetInsulin.CoveredMs"/> says how much of an interval had both.
/// Boluses count wherever they fall.
/// </remarks>
public sealed class NetInsulinAboveSchedule
{
    /// <summary>
    /// The longest stretch with no temp basal record that the schedule is taken to have run
    /// through, and then only with a temp basal record on both sides of it. Loop and the oref
    /// systems let a temp lapse back to the schedule between decisions, and a pump that reports
    /// only its temps leaves scheduled stretches between them; past a few hours without a record,
    /// an upload outage or time before the pump is the likelier story, and delivery there is
    /// unknown rather than scheduled. An APS snapshot does not bound such a stretch: it shows the
    /// loop ran, not what the pump delivered, and snapshots arriving every five minutes while the
    /// temp records are missing would otherwise fill weeks with the schedule.
    /// </summary>
    public const long MaxInferredGapMs = 3 * LocalSlotBinning.HourMs;

    private readonly BasalPiece[] _pieces;
    private readonly InsulinDose[] _doses;

    /// <param name="delivered">Non-overlapping; see <see cref="DeliveredFromSeries"/>.</param>
    /// <param name="scheduled">Non-overlapping; see <see cref="ScheduledBasal.Segments"/>.</param>
    public NetInsulinAboveSchedule(
        IEnumerable<DeliveredBasalSegment> delivered,
        IEnumerable<RateSegment> scheduled,
        IEnumerable<InsulinDose> doses)
    {
        _pieces = [.. Merge(
            [.. delivered.Where(d => d.End > d.Start).OrderBy(d => d.Start)],
            [.. scheduled.Where(s => s.End > s.Start).OrderBy(s => s.Start)])];
        _doses = [.. doses.Where(d => d.Units > 0).OrderBy(d => d.Mills)];
    }

    public IReadOnlyList<BasalPiece> Pieces => _pieces;

    /// <summary>Doses with insulin, by time.</summary>
    public IReadOnlyList<InsulinDose> Doses => _doses;

    public NetInsulin Over(long start, long end)
    {
        double algorithm = 0, manualTemp = 0, suspend = 0, inferred = 0, automaticBolus = 0, manualBolus = 0;
        long covered = 0;

        for (var i = SortedSearch.FirstEndingAfter(_pieces, start, static p => p.End); i < _pieces.Length && _pieces[i].Start < end; i++)
        {
            var piece = _pieces[i];
            if (!piece.HasDelta)
                continue;
            var ms = Math.Min(end, piece.End) - Math.Max(start, piece.Start);
            if (ms <= 0)
                continue;
            covered += ms;
            var units = (piece.Delivered!.Value - piece.Scheduled!.Value) * ms / LocalSlotBinning.HourMs;
            switch (piece.Origin)
            {
                case NetInsulinOrigin.AlgorithmTemp: algorithm += units; break;
                case NetInsulinOrigin.ManualTemp: manualTemp += units; break;
                case NetInsulinOrigin.Suspend: suspend += units; break;
                default: inferred += units; break;
            }
        }

        for (var i = SortedSearch.FirstEndingAfter(_doses, start - 1, static d => d.Mills); i < _doses.Length && _doses[i].Mills < end; i++)
        {
            if (_doses[i].Automatic)
                automaticBolus += _doses[i].Units;
            else
                manualBolus += _doses[i].Units;
        }

        return new NetInsulin(algorithm, automaticBolus, manualTemp, suspend, inferred, manualBolus, covered);
    }

    public static NetInsulinOrigin OriginOf(BasalDeliveryOrigin origin) => origin switch
    {
        BasalDeliveryOrigin.Algorithm => NetInsulinOrigin.AlgorithmTemp,
        BasalDeliveryOrigin.Manual => NetInsulinOrigin.ManualTemp,
        BasalDeliveryOrigin.Suspended => NetInsulinOrigin.Suspend,
        _ => NetInsulinOrigin.Inferred,
    };

    /// <summary>
    /// Temp basals as delivered (<see cref="StatisticsService.ClipOverlappingTempBasals"/>), with what
    /// they say about delivery, for <see cref="DeliveredFromSeries"/>. Each keeps the evidence its
    /// origin rests on, resolved (<see cref="TempBasalOriginProvenance.EvidenceOf(TempBasal)"/>), and
    /// the snapshot that set it.
    /// </summary>
    /// <param name="untilMills">Where a temp still running stops: the end of the window.</param>
    public static (List<TempBasal> Clipped, BasalEvidence Evidence) ClipTempBasals(
        IEnumerable<TempBasal> tempBasals, long untilMills)
    {
        var clipped = StatisticsService.ClipOverlappingTempBasals(tempBasals, untilMills);
        var recorded = new List<MillsInterval>();
        var unknown = new List<MillsInterval>();
        foreach (var tb in clipped)
        {
            tb.OriginEvidence = TempBasalOriginProvenance.EvidenceOf(tb);
            recorded.Add(new MillsInterval(tb.StartMills, tb.EndMills!.Value));
            if (tb.Rate is null && tb.Origin != TempBasalOrigin.Suspended)
                unknown.Add(new MillsInterval(tb.StartMills, tb.EndMills!.Value));
        }

        var recordedSet = IntervalSet.Of(recorded);
        var evidence = recorded.SelectMany(r => new[] { r.Start, r.End }).Order().ToArray();
        var inferable = new List<MillsInterval>();
        for (var i = 0; i + 1 < evidence.Length; i++)
        {
            if (evidence[i + 1] - evidence[i] <= MaxInferredGapMs)
                inferable.Add(new MillsInterval(evidence[i], evidence[i + 1]));
        }

        return (clipped, new BasalEvidence(
            recordedSet, IntervalSet.Of(unknown), IntervalSet.Of(inferable).Subtract(recordedSet)));
    }

    /// <summary>
    /// Delivered basal from a step series in which each point holds until the next, as
    /// <c>IBasalSeriesBuilder</c> builds it from temp basals with schedule-filled gaps. A recorded
    /// point holds only within <see cref="BasalEvidence.Recorded"/> and a schedule-filled one only
    /// within <see cref="BasalEvidence.Inferable"/>; everywhere else delivery is absent.
    /// </summary>
    /// <remarks>
    /// A fill that straddles <paramref name="end"/> relies on <c>IBasalSeriesBuilder</c> clamping
    /// temps to its end, so evidence fetched past it bounds the gap but never counts as delivery.
    /// </remarks>
    /// <param name="end">Where the last point stops.</param>
    public static List<DeliveredBasalSegment> DeliveredFromSeries(
        IEnumerable<BasalPoint> series, long end, BasalEvidence evidence)
    {
        var points = series.OrderBy(p => p.Timestamp).ToList();
        var result = new List<DeliveredBasalSegment>();
        for (var i = 0; i < points.Count; i++)
        {
            var start = points[i].Timestamp;
            var stop = i + 1 < points.Count ? points[i + 1].Timestamp : end;
            if (stop <= start)
                continue;

            var point = points[i];
            void Add(long pieceStart, long pieceEnd) =>
                evidence.Unknown.ForEachGap(pieceStart, pieceEnd, (knownStart, knownEnd) =>
                    result.Add(new DeliveredBasalSegment(knownStart, knownEnd, point.Rate, point.Origin)));

            (point.Origin == BasalDeliveryOrigin.Inferred ? evidence.Inferable : evidence.Recorded)
                .ForEachOverlap(start, stop, Add);
        }
        return result;
    }

    private static IEnumerable<BasalPiece> Merge(DeliveredBasalSegment[] delivered, RateSegment[] scheduled)
    {
        var bounds = new SortedSet<long>();
        foreach (var d in delivered) { bounds.Add(d.Start); bounds.Add(d.End); }
        foreach (var s in scheduled) { bounds.Add(s.Start); bounds.Add(s.End); }

        int di = 0, si = 0;
        long? previous = null;
        foreach (var bound in bounds)
        {
            if (previous is { } start)
            {
                while (di < delivered.Length && delivered[di].End <= start) di++;
                while (si < scheduled.Length && scheduled[si].End <= start) si++;

                var d = di < delivered.Length && delivered[di].Start <= start ? delivered[di] : (DeliveredBasalSegment?)null;
                var s = si < scheduled.Length && scheduled[si].Start <= start ? scheduled[si] : (RateSegment?)null;
                if (d is not null || s is not null)
                    yield return new BasalPiece(
                        start, bound, d?.Rate, s?.Rate, d is { } known ? OriginOf(known.Origin) : null);
            }
            previous = bound;
        }
    }
}
