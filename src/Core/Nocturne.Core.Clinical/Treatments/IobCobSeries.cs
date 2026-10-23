using Nocturne.Core.Clinical.Statistics;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.Core.Clinical.Treatments;

/// <summary>IOB and COB at one tick, and where each came from.</summary>
public readonly record struct IobCobTick(
    long Mills, double Iob, IobCobSource IobSource, double Cob, IobCobSource CobSource);

/// <summary>How a calculated tick counts temp basal insulin on board.</summary>
public enum BasalIobModel
{
    /// <summary>oref's, as IOB is shown: <see cref="IIobCalculator.FromTempBasals(List{TempBasal}, TherapySnapshot, long)"/>.</summary>
    Displayed,

    /// <summary>Signed, on the model of net insulin above schedule: <see cref="CarriedBasalInsulin"/>.</summary>
    Carried,
}

/// <summary>
/// IOB and COB at every tick of a window, preferring the AID system's own values.
/// </summary>
/// <remarks>
/// <para>
/// A tick covered by an APS snapshot no older than <see cref="DeviceReportedValues.RecencyThresholdMs"/>
/// takes the device-reported IOB/COB verbatim: they are the values the AID system acted on, and
/// matching them keeps every consumer consistent with the uploader and the status pill. A tick with
/// no recent snapshot (pre-AID history, upload gaps, care-portal-only tenants) is recomputed
/// locally, per component, so a snapshot carrying IOB but no COB still has its COB computed.
/// </para>
/// <para>
/// Treatments are kept in time-sorted arrays and the active window is tracked with two-pointer
/// indices that advance with each tick: only boluses and temp basals within DIA hours of the tick
/// contribute to IOB, and only carb intakes within 6 hours to COB, so the inner-loop work is
/// O(ticks + active window) rather than O(ticks × treatments). DIA is read from the timeline at
/// <c>endTime</c>. Temp basals count as delivered
/// (<see cref="StatisticsService.ClipOverlappingTempBasals"/>), one still running up to <c>untilMills</c>.
/// Under <see cref="BasalIobModel.Carried"/> a piece counts until DIA after its end, not its start.
/// </para>
/// </remarks>
public static class IobCobSeries
{
    private const long CobAbsorptionMs = 6L * 60 * 60 * 1000;

    /// <param name="timeline">Covers at least <c>[startTime, endTime + 1)</c>.</param>
    /// <param name="untilMills">Where a temp basal still running stops; see <see cref="StatisticsService.ClipOverlappingTempBasals"/>.</param>
    /// <returns>One tick at <paramref name="startTime"/> and every <paramref name="intervalMs"/> after it, up to and including <paramref name="endTime"/>.</returns>
    public static List<IobCobTick> Build(
        IIobCalculator iobCalculator,
        ICobCalculator cobCalculator,
        IEnumerable<Bolus> boluses,
        IEnumerable<CarbIntake> carbIntakes,
        IEnumerable<TempBasal>? tempBasals,
        TherapyTimeline timeline,
        IEnumerable<ApsIobCobPoint>? apsSnapshots,
        long startTime,
        long endTime,
        long untilMills,
        long intervalMs,
        BasalIobModel basalModel = BasalIobModel.Displayed,
        CancellationToken ct = default)
    {
        var ticks = new List<IobCobTick>();

        var diaMs = (long)(timeline.SnapshotAt(endTime).Dia * 60 * 60 * 1000);

        var sortedBoluses = boluses
            .Where(b => b.Insulin > 0)
            .OrderBy(b => b.Mills)
            .ToList();
        var sortedCarbs = carbIntakes
            .Where(c => c.Carbs > 0)
            .OrderBy(c => c.Mills)
            .ToList();
        var sortedTempBasals = tempBasals is null ? null : StatisticsService.ClipOverlappingTempBasals(tempBasals, untilMills);

        var sortedSnapshots = (apsSnapshots ?? [])
            .Select(s => (
                Mills: new DateTimeOffset(DateTime.SpecifyKind(s.Timestamp, DateTimeKind.Utc), TimeSpan.Zero).ToUnixTimeMilliseconds(),
                s.Iob,
                s.Cob
            ))
            .OrderBy(s => s.Mills)
            .ToList();

        int insulinHi = 0,
            insulinLo = 0;
        int carbHi = 0,
            carbLo = 0;
        int basalHi = 0,
            basalLo = 0,
            carriedLo = 0;
        int snapshotHi = 0;

        for (long t = startTime; t <= endTime; t += intervalMs)
        {
            ct.ThrowIfCancellationRequested();

            var snapshot = timeline.SnapshotAt(t);

            while (insulinHi < sortedBoluses.Count && sortedBoluses[insulinHi].Mills <= t)
                insulinHi++;
            while (carbHi < sortedCarbs.Count && sortedCarbs[carbHi].Mills <= t)
                carbHi++;
            if (sortedTempBasals is not null)
            {
                while (basalHi < sortedTempBasals.Count && sortedTempBasals[basalHi].StartMills <= t)
                    basalHi++;
            }

            while (insulinLo < insulinHi && sortedBoluses[insulinLo].Mills < t - diaMs)
                insulinLo++;
            while (carbLo < carbHi && sortedCarbs[carbLo].Mills < t - CobAbsorptionMs)
                carbLo++;
            if (sortedTempBasals is not null)
            {
                while (basalLo < basalHi && sortedTempBasals[basalLo].StartMills < t - diaMs)
                    basalLo++;
                while (carriedLo < basalHi && (sortedTempBasals[carriedLo].EndMills ?? sortedTempBasals[carriedLo].StartMills) <= t - diaMs)
                    carriedLo++;
            }

            while (snapshotHi < sortedSnapshots.Count && sortedSnapshots[snapshotHi].Mills <= t)
                snapshotHi++;
            var deviceValues = snapshotHi > 0
                && t - sortedSnapshots[snapshotHi - 1].Mills <= DeviceReportedValues.RecencyThresholdMs
                    ? sortedSnapshots[snapshotHi - 1]
                    : default((long Mills, double? Iob, double? Cob)?);

            var insulinCount = insulinHi - insulinLo;
            var basalCount = basalHi - basalLo;

            double iob;
            IobCobSource iobSource;
            if (deviceValues?.Iob is { } deviceIob)
            {
                // Device IOB is the total the AID system acted on (bolus + temp-basal delta).
                iob = deviceIob;
                iobSource = IobCobSource.Device;
            }
            else
            {
                var iobResult = insulinCount > 0
                    ? iobCalculator.FromBoluses(sortedBoluses.GetRange(insulinLo, insulinCount), snapshot, t)
                    : new IobResult { Iob = 0 };

                var basalIob = 0.0;
                if (sortedTempBasals is not null && basalModel == BasalIobModel.Carried)
                {
                    basalIob = CarriedBasalInsulin.At(sortedTempBasals.Skip(carriedLo).Take(basalHi - carriedLo), snapshot, t);
                }
                else if (sortedTempBasals is not null && basalCount > 0)
                {
                    var basalResult = iobCalculator.FromTempBasals(
                        sortedTempBasals.GetRange(basalLo, basalCount),
                        snapshot,
                        t
                    );
                    basalIob = basalResult.BasalIob ?? 0;
                }

                iob = iobResult.Iob + basalIob;
                iobSource = IobCobSource.Calculated;
            }

            double cob;
            IobCobSource cobSource;
            if (deviceValues?.Cob is { } deviceCob)
            {
                cob = deviceCob;
                cobSource = IobCobSource.Device;
            }
            else
            {
                var carbCount = carbHi - carbLo;
                var cobResult = carbCount > 0
                    ? cobCalculator.FromCarbIntakes(
                        sortedCarbs.GetRange(carbLo, carbCount),
                        sortedBoluses.GetRange(insulinLo, insulinCount),
                        sortedTempBasals is not null && basalCount > 0
                            ? sortedTempBasals.GetRange(basalLo, basalCount)
                            : null,
                        snapshot,
                        t
                    )
                    : new CobResult { Cob = 0 };
                cob = cobResult.Cob;
                cobSource = IobCobSource.Calculated;
            }

            ticks.Add(new IobCobTick(t, iob, iobSource, cob, cobSource));
        }

        return ticks;
    }
}
