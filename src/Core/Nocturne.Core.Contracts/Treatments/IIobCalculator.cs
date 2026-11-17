using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.Core.Contracts.Treatments;

public interface IIobCalculator
{
    /// <summary>
    /// Total IOB at <paramref name="time"/>: a recent device's own, else the boluses plus the net basal
    /// IOB of the temp basals as delivered (<c>StatisticsService.ClipOverlappingTempBasals</c>), on the
    /// model <c>IobCalculator.CalcNetBasal</c> describes.
    /// </summary>
    /// <param name="deliveredUntil">When temp basals stop delivering; <paramref name="time"/> when null
    /// or later. A reading after the present, such as the carb delay's activity at a future decay time,
    /// passes the present so a running temp does not deliver into the future.</param>
    /// <param name="live">Whether <paramref name="time"/> is the newest instant, as a live endpoint reads
    /// it; see <c>DeviceReportedValues</c>.</param>
    Task<IobResult> CalculateTotalAsync(
        List<Bolus> boluses,
        List<TempBasal>? tempBasals,
        long time,
        long? deliveredUntil = null,
        bool live = false,
        CancellationToken ct = default);

    /// <summary>
    /// The calculated total at <paramref name="time"/>, as <see cref="CalculateTotalAsync"/> gives it when
    /// no device snapshot is recent, whether or not one is.
    /// </summary>
    Task<IobResult> CalculateFromTreatmentsAsync(
        List<Bolus> boluses, List<TempBasal>? tempBasals, long time, CancellationToken ct = default);

    IobResult FromBoluses(List<Bolus> boluses, long currentTime);
    IobContribution CalcBolus(Bolus bolus, long currentTime);

    // --- Async-free overloads for the chart IOB/COB tick loop ---
    // These resolve DIA, peak and sensitivity from a pre-built, in-memory <see cref="TherapySnapshot"/>
    // instead of awaiting profile resolvers per call. The chart computes one snapshot per tick
    // (TherapyTimeline.SnapshotAt) and evaluates hundreds of treatments against it without any DB
    // round trip; see IobCobSeries.

    /// <inheritdoc cref="CalcBolus(Bolus, long)"/>
    IobContribution CalcBolus(Bolus bolus, TherapySnapshot snapshot, long time);

    /// <inheritdoc cref="FromBoluses(List{Bolus}, long)"/>
    IobResult FromBoluses(List<Bolus> boluses, TherapySnapshot snapshot, long time);

    /// <summary>
    /// Net basal IOB at <paramref name="time"/> of temp basals delivered up to
    /// <paramref name="deliveredUntil"/>, on the model <c>IobCalculator.CalcNetBasal</c> describes.
    /// </summary>
    /// <param name="pieces">As <c>StatisticsService.ClipOverlappingTempBasals</c> returns them. This
    /// does not clip, since the tick loop calls it for every tick over the same pieces.</param>
    /// <param name="scheduledUnder">The scheduled rate under a piece at an instant, as
    /// <c>ScheduledBasal.Under</c> gives it. It is read under every chunk delivered before
    /// <paramref name="deliveredUntil"/>.</param>
    IobResult FromTempBasals(
        List<TempBasal> pieces,
        TherapySnapshot snapshot,
        Func<TempBasal, long, double> scheduledUnder,
        long time,
        long deliveredUntil);
}
