using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.Core.Contracts.Treatments;

public interface IIobCalculator
{
    Task<IobResult> CalculateTotalAsync(
        List<Bolus> boluses,
        List<TempBasal>? tempBasals,
        long time,
        CancellationToken ct = default);

    IobResult FromBoluses(List<Bolus> boluses, long? time = null);
    /// <summary>
    /// Basal IOB from temp basals as delivered: the input is first clipped by
    /// <c>StatisticsService.ClipOverlappingTempBasals</c>, so stored records and pieces that clip
    /// already both count each minute of delivery once. A temp basal still running counts up to
    /// <paramref name="time"/>.
    /// </summary>
    IobResult FromTempBasals(List<TempBasal> tempBasals, long time);
    IobContribution CalcBolus(Bolus bolus, long? time = null);
    IobContribution CalcTempBasal(TempBasal tempBasal, long time);

    // --- Async-free overloads for the chart IOB/COB tick loop ---
    // These resolve DIA, peak, sensitivity and the scheduled basal rate from a pre-built,
    // in-memory <see cref="TherapySnapshot"/> instead of awaiting profile resolvers per call.
    // The chart computes one snapshot per tick (TherapyTimeline.SnapshotAt) and evaluates
    // hundreds of treatments against it without any DB round trip; see IobCobSeries.

    /// <inheritdoc cref="CalcBolus(Bolus, long?)"/>
    IobContribution CalcBolus(Bolus bolus, TherapySnapshot snapshot, long time);

    /// <inheritdoc cref="CalcTempBasal(TempBasal, long)"/>
    IobContribution CalcTempBasal(TempBasal tempBasal, TherapySnapshot snapshot, long time);

    /// <inheritdoc cref="FromBoluses(List{Bolus}, long?)"/>
    IobResult FromBoluses(List<Bolus> boluses, TherapySnapshot snapshot, long time);

    /// <summary>
    /// Basal IOB from temp basals that are already pieces, as <c>StatisticsService.ClipOverlappingTempBasals</c>
    /// returns them. Unlike <see cref="FromTempBasals(List{TempBasal}, long)"/> it does not clip,
    /// since the tick loop calls it for every tick over the same pieces.
    /// </summary>
    IobResult FromTempBasals(List<TempBasal> tempBasals, TherapySnapshot snapshot, long time);
}
