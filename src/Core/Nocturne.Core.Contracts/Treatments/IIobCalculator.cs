using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.Core.Contracts.Treatments;

public interface IIobCalculator
{
    Task<IobResult> CalculateTotalAsync(
        List<Bolus> boluses,
        List<TempBasal>? tempBasals = null,
        long? time = null,
        CancellationToken ct = default);

    IobResult FromBoluses(List<Bolus> boluses, long? time = null);
    IobResult FromTempBasals(List<TempBasal> tempBasals, long? time = null);
    IobContribution CalcBolus(Bolus bolus, long? time = null);
    IobContribution CalcTempBasal(TempBasal tempBasal, long? time = null);

    // --- Async-free overloads for the chart IOB/COB tick loop ---
    // These resolve DIA, peak, sensitivity and the scheduled basal rate from a pre-built,
    // in-memory <see cref="TherapySnapshot"/> instead of awaiting profile resolvers per call.
    // The chart computes one snapshot per tick (TherapyTimeline.SnapshotAt) and evaluates
    // hundreds of treatments against it without any DB round trip — see IobCobComputeStage.

    /// <inheritdoc cref="CalcBolus(Bolus, long?)"/>
    IobContribution CalcBolus(Bolus bolus, TherapySnapshot snapshot, long time);

    /// <inheritdoc cref="CalcTempBasal(TempBasal, long?)"/>
    IobContribution CalcTempBasal(TempBasal tempBasal, TherapySnapshot snapshot, long time);

    /// <inheritdoc cref="FromBoluses(List{Bolus}, long?)"/>
    IobResult FromBoluses(List<Bolus> boluses, TherapySnapshot snapshot, long time);

    /// <inheritdoc cref="FromTempBasals(List{TempBasal}, long?)"/>
    IobResult FromTempBasals(List<TempBasal> tempBasals, TherapySnapshot snapshot, long time);
}
