using Nocturne.Core.Clinical.Statistics;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.Core.Clinical.Treatments;

/// <summary>IOB and COB at one tick, and where each came from.</summary>
/// <param name="BolusIob">The bolus part of a calculated <paramref name="Iob"/>; null on a device's.</param>
/// <param name="BasalIob">The net basal part of a calculated <paramref name="Iob"/>; null on a device's.</param>
public readonly record struct IobCobTick(
    long Mills,
    double Iob,
    IobCobSource IobSource,
    double Cob,
    IobCobSource CobSource,
    double? BolusIob = null,
    double? BasalIob = null);

/// <summary>
/// IOB and COB at every tick of a window, preferring the devices' own values.
/// </summary>
/// <remarks>
/// <para>
/// Each tick takes, per component, the device value <see cref="DeviceReadings.At"/> chooses, as
/// every other IOB and COB reader does, and calculates the rest. The tick holding now is read live.
/// </para>
/// <para>
/// Treatments are kept in time-sorted arrays and the active window is tracked with two-pointer
/// indices that advance with each tick: only boluses and temp basals within DIA hours of the tick
/// contribute to IOB, and only carb intakes within 6 hours to COB, so the inner-loop work is
/// O(ticks + active window) rather than O(ticks × treatments). The window is the longest DIA in
/// play: the timeline's at <c>endTime</c> or any bolus's or temp basal's own
/// <see cref="TreatmentInsulinContext.Dia"/>, which the calculator reads in preference. Temp basals count as delivered
/// (<see cref="StatisticsService.ClipOverlappingTempBasals"/>), one still running up to now or the
/// window's end, whichever comes first,
/// and each tick takes their net basal IOB up to the tick on the model
/// <see cref="IobCalculator.CalcNetBasal"/> describes. A piece counts until DIA after its end, so a
/// suspend that started more than DIA ago and still runs is counted.
/// </para>
/// </remarks>
public static class IobCobSeries
{
    private const long CobAbsorptionMs = 6L * 60 * 60 * 1000;

    /// <param name="timeline">Covers at least <c>[startTime - DIA, endTime + 1)</c>.</param>
    /// <param name="scheduled">Over the same time as <paramref name="timeline"/>: it is read under every chunk of temp basal still on board at a tick.</param>
    /// <param name="deviceReadings">The APS and pump snapshots from <see cref="DeviceReportedValues.RecencyThresholdMs"/> before <paramref name="startTime"/>; null for none.</param>
    /// <param name="nowMills">The present. A temp basal still running stops at it or at <paramref name="endTime"/>; see <see cref="StatisticsService.ClipOverlappingTempBasals"/>.</param>
    /// <returns>One tick at <paramref name="startTime"/> and every <paramref name="intervalMs"/> after it, up to and including <paramref name="endTime"/>.</returns>
    public static List<IobCobTick> Build(
        IIobCalculator iobCalculator,
        ICobCalculator cobCalculator,
        IEnumerable<Bolus> boluses,
        IEnumerable<CarbIntake> carbIntakes,
        IEnumerable<TempBasal>? tempBasals,
        TherapyTimeline timeline,
        ScheduledBasal scheduled,
        DeviceReadings? deviceReadings,
        long startTime,
        long endTime,
        long nowMills,
        long intervalMs,
        CancellationToken ct = default)
    {
        var ticks = new List<IobCobTick>();

        var sortedBoluses = boluses
            .Where(b => b.Insulin > 0)
            .OrderBy(b => b.Mills)
            .ToList();
        var sortedCarbs = carbIntakes
            .Where(c => c.Carbs > 0)
            .OrderBy(c => c.Mills)
            .ToList();
        var sortedTempBasals = tempBasals is null ? null : StatisticsService.ClipOverlappingTempBasals(tempBasals, Math.Min(endTime, nowMills));

        var diaHours = timeline.SnapshotAt(endTime).Dia;
        foreach (var own in sortedBoluses.Select(b => b.InsulinContext?.Dia)
                     .Concat(sortedTempBasals?.Select(p => p.InsulinContext?.Dia) ?? []))
        {
            if (own > diaHours)
                diaHours = own.Value;
        }
        var diaMs = (long)(diaHours * 60 * 60 * 1000);

        int insulinHi = 0,
            insulinLo = 0;
        int carbHi = 0,
            carbLo = 0;
        int basalHi = 0,
            basalLo = 0;

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
                // Pieces do not overlap, so they end in the order they start.
                while (basalLo < basalHi && sortedTempBasals[basalLo].EndMills!.Value <= t - diaMs)
                    basalLo++;
            }

            var device = deviceReadings?.At(t, live: t <= nowMills && nowMills < t + intervalMs);

            var insulinCount = insulinHi - insulinLo;
            var basalCount = basalHi - basalLo;

            double iob;
            IobCobSource iobSource;
            double? bolusIob = null,
                basalIob = null;
            if (device?.Iob?.Iob is { } deviceIob)
            {
                iob = deviceIob;
                iobSource = IobCobSource.Device;
            }
            else
            {
                var bolus = insulinCount > 0
                    ? iobCalculator.FromBoluses(sortedBoluses.GetRange(insulinLo, insulinCount), snapshot, t).Iob
                    : 0;
                var basal = sortedTempBasals is not null && basalCount > 0
                    ? iobCalculator.FromTempBasals(sortedTempBasals.GetRange(basalLo, basalCount), snapshot, scheduled.Under, t, t).BasalIob ?? 0
                    : 0;

                iob = bolus + basal;
                (bolusIob, basalIob) = (bolus, basal);
                iobSource = IobCobSource.Calculated;
            }

            double cob;
            IobCobSource cobSource;
            if (device?.Cob?.Cob is { } deviceCob)
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
                        scheduled.Under,
                        t
                    )
                    : new CobResult { Cob = 0 };
                cob = cobResult.Cob;
                cobSource = IobCobSource.Calculated;
            }

            ticks.Add(new IobCobTick(t, iob, iobSource, cob, cobSource, bolusIob, basalIob));
        }

        return ticks;
    }
}
