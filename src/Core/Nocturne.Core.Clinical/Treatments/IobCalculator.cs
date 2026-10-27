using Nocturne.Core.Clinical.Statistics;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Basal;
using Nocturne.Core.Models.V4;

namespace Nocturne.Core.Clinical.Treatments;

/// <summary>
/// V4-native IOB calculator operating on <see cref="Bolus"/> and <see cref="TempBasal"/> records.
/// A record's own <see cref="TreatmentInsulinContext"/> overrides the profile DIA and chooses its
/// insulin curve (<see cref="CalcInsulin"/>).
/// </summary>
/// <seealso cref="IIobCalculator"/>
public class IobCalculator(
    ITherapySettingsResolver therapySettings,
    ISensitivityResolver sensitivity,
    IBasalSegmentService basalSegments,
    IApsSnapshotRepository apsSnapshotRepo,
    IPumpSnapshotRepository pumpSnapshotRepo,
    IStateSpanRepository stateSpans
) : IIobCalculator
{
    // Constants from legacy implementation (identical to IobService)
    private const double SCALE_FACTOR_BASE = 3.0;
    private const double MAX_IOB_MINUTES = 180.0;
    private const long NET_BASAL_CHUNK_MS = 5 * 60 * 1000;

    // oref0's custom peak limits, 50-120 minutes rapid-acting and 35-100 ultra-rapid, joined because a
    // record's Curve is not reliable: AAPS records are all stamped rapid-acting.
    private const int MIN_RECORD_PEAK_MINUTES = 35;
    private const int MAX_RECORD_PEAK_MINUTES = 120;

    // oref0's minimum DIA for the exponential curves, also used by AAPS and Trio. The curve needs a peak
    // under half the DIA, so some floor is required.
    private const double MIN_EXPONENTIAL_DIA_HOURS = 5.0;

    // COB asks for the total at several instants over the same temps and cutoff, so the schedule
    // they read is the same and is read once.
    private (long From, long To, ScheduledBasal Scheduled)? _scheduled;

    /// <inheritdoc />
    public async Task<IobResult> CalculateTotalAsync(
        List<Bolus> boluses,
        List<TempBasal>? tempBasals,
        long time,
        long? deliveredUntil = null,
        bool live = false,
        CancellationToken ct = default
    )
    {
        // A reported IOB of zero (or negative, after a long low temp) is a real device value.
        var deviceResult = await GetDeviceIobAsync(time, live, ct);

        var bolusResult = boluses?.Any() == true ? FromBoluses(boluses, time) : null;

        if (deviceResult is not null)
        {
            // Nightscout reports the IOB its treatments give beside a device's own.
            if (bolusResult?.Iob > 0)
            {
                deviceResult.TreatmentIob = bolusResult.Iob;
            }

            return AddDisplay(deviceResult);
        }

        return await LocalTotalAsync(bolusResult, tempBasals, time, deliveredUntil, ct);
    }

    /// <inheritdoc />
    public Task<IobResult> CalculateFromTreatmentsAsync(
        List<Bolus> boluses, List<TempBasal>? tempBasals, long time, CancellationToken ct = default) =>
        LocalTotalAsync(boluses?.Any() == true ? FromBoluses(boluses, time) : null, tempBasals, time, null, ct);

    private async Task<IobResult> LocalTotalAsync(
        IobResult? bolusResult, List<TempBasal>? tempBasals, long time, long? deliveredUntil, CancellationToken ct)
    {
        var basalResult = tempBasals?.Any() == true
            ? await FromTempBasalsAsync(tempBasals, time, Math.Min(deliveredUntil ?? time, time), ct)
            : null;

        return AddDisplay(Total(bolusResult, basalResult));
    }

    /// <inheritdoc />
    public IobContribution CalcBolus(Bolus bolus, long? time = null)
    {
        if (bolus.Insulin <= 0)
        {
            return new IobContribution { IobContrib = 0, ActivityContrib = 0 };
        }

        var currentTime = time ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var dia = bolus.InsulinContext?.Dia
            ?? therapySettings.GetDIAAsync(currentTime, null).GetAwaiter().GetResult();
        var sens = sensitivity.GetSensitivityAsync(currentTime, null).GetAwaiter().GetResult();

        return CalcInsulin(bolus.Insulin, bolus.Mills, bolus.InsulinContext, dia, sens, currentTime);
    }

    /// <inheritdoc />
    public IobContribution CalcBolus(Bolus bolus, TherapySnapshot snapshot, long time)
    {
        if (bolus.Insulin <= 0)
        {
            return new IobContribution { IobContrib = 0, ActivityContrib = 0 };
        }

        return CalcInsulin(bolus.Insulin, bolus.Mills, bolus.InsulinContext, snapshot.Dia, snapshot.SensitivityAt(time), time);
    }

    /// <summary>
    /// The DIA the curve of insulin carrying <paramref name="context"/> runs for: the record's own,
    /// raised to <see cref="MIN_EXPONENTIAL_DIA_HOURS"/> on the exponential curve, else the profile's.
    /// </summary>
    internal static double CurveDia(TreatmentInsulinContext? context, double profileDia) =>
        context is { Peak: > 0 } ? Math.Max(context.Dia, MIN_EXPONENTIAL_DIA_HOURS) : context?.Dia ?? profileDia;

    /// <summary>
    /// What remains of <paramref name="units"/> delivered at <paramref name="deliveredMills"/>, and
    /// how fast it is acting, in sensitivity units per minute. Insulin whose record has its own peak
    /// follows <see cref="ExponentialInsulinCurve"/>, as AAPS and Trio do; other insulin follows the
    /// iob.js curve (<see cref="CalcLegacyInsulin"/>). Negative units give negative IOB and activity.
    /// </summary>
    private static IobContribution CalcInsulin(
        double units, long deliveredMills, TreatmentInsulinContext? context, double profileDia, double sens, long currentTime)
    {
        var dia = CurveDia(context, profileDia);
        if (context is not { Peak: > 0 })
            return CalcLegacyInsulin(units, deliveredMills, dia, sens, currentTime);

        var minutes = (currentTime - deliveredMills) / 60_000.0;
        var peak = Math.Clamp(context.Peak, MIN_RECORD_PEAK_MINUTES, MAX_RECORD_PEAK_MINUTES);
        return new IobContribution
        {
            IobContrib = units * ExponentialInsulinCurve.IobFraction(minutes, peak, dia * 60),
            ActivityContrib = sens * units * ExponentialInsulinCurve.ActivityFraction(minutes, peak, dia * 60),
        };
    }

    /// <summary>
    /// The iob.js curve: two fitted polynomials meeting at a fixed 75-minute peak, on a time scaled
    /// by <c>3 / DIA</c> so that it ends at DIA.
    /// </summary>
    private static IobContribution CalcLegacyInsulin(
        double units, long deliveredMills, double dia, double sens, long currentTime)
    {
        const double peak = TherapySnapshot.DefaultPeakMinutes;
        var scaleFactor = SCALE_FACTOR_BASE / dia;

        var minAgo = (scaleFactor * (currentTime - deliveredMills)) / 1000.0 / 60.0;

        // Before peak (0-75 minutes): curved rise
        if (minAgo < peak)
        {
            var x1 = minAgo / 5.0 + 1.0;
            var iobContrib = units * Math.Max(0.0, 1.0 - 0.001852 * x1 * x1 + 0.001852 * x1);
            var activityContrib =
                sens * units * (2.0 / dia / 60.0 / peak) * minAgo;

            return new IobContribution
            {
                IobContrib = iobContrib,
                ActivityContrib = activityContrib,
            };
        }

        // After peak (75-180 minutes): curved decline
        if (minAgo < MAX_IOB_MINUTES)
        {
            var x2 = (minAgo - peak) / 5.0;
            var iobContrib =
                units * Math.Max(0.0, 0.001323 * x2 * x2 - 0.054233 * x2 + 0.55556);
            var activityContrib =
                sens
                * units
                * (2.0 / dia / 60.0 - ((minAgo - peak) * 2.0) / dia / 60.0 / (60.0 * 3.0 - peak));

            return new IobContribution
            {
                IobContrib = iobContrib,
                ActivityContrib = activityContrib,
            };
        }

        // After 180 minutes: no IOB remaining
        return new IobContribution { IobContrib = 0, ActivityContrib = 0 };
    }

    /// <inheritdoc />
    public IobResult FromBoluses(List<Bolus> boluses, long? time = null)
    {
        var currentTime = time ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // Every bolus reads DIA and sensitivity at currentTime, so each is resolved once for the
        // batch rather than per bolus: a resolver call is several database round-trips, and an
        // automated-bolus history holds dozens of boluses inside one DIA window. Lazy, so a batch
        // whose boluses all carry their own insulin context never asks for the profile DIA.
        var profileDia = new Lazy<double>(
            () => therapySettings.GetDIAAsync(currentTime, null).GetAwaiter().GetResult());
        var sens = new Lazy<double>(
            () => sensitivity.GetSensitivityAsync(currentTime, null).GetAwaiter().GetResult());

        return FromBolusesCore(boluses, currentTime, bolus => bolus.Insulin <= 0
            ? new IobContribution { IobContrib = 0, ActivityContrib = 0 }
            : CalcInsulin(
                bolus.Insulin,
                bolus.Mills,
                bolus.InsulinContext,
                bolus.InsulinContext?.Dia ?? profileDia.Value,
                sens.Value,
                currentTime));
    }

    /// <inheritdoc />
    public IobResult FromBoluses(List<Bolus> boluses, TherapySnapshot snapshot, long time)
    {
        return FromBolusesCore(boluses, time, bolus => CalcBolus(bolus, snapshot, time));
    }

    private static IobResult FromBolusesCore(
        List<Bolus> boluses, long currentTime, Func<Bolus, IobContribution> calc)
    {
        if (boluses?.Any() != true)
        {
            return new IobResult
            {
                Iob = 0.0,
                BolusIob = 0.0,
                Activity = 0.0,
                Source = "Care Portal",
            };
        }

        var totalIob = 0.0;
        var totalActivity = 0.0;
        Bolus? lastBolus = null;

        foreach (var bolus in boluses.Where(b => b.Mills <= currentTime && b.Insulin > 0))
        {
            var contribution = calc(bolus);

            totalIob += contribution.IobContrib;
            totalActivity += contribution.ActivityContrib;

            if (lastBolus == null || bolus.Mills > lastBolus.Mills)
            {
                lastBolus = bolus;
            }
        }

        var bolusIob = RoundToThreeDecimals(totalIob);
        return new IobResult
        {
            Iob = bolusIob,
            BolusIob = bolusIob,
            Activity = totalActivity,
            LastBolus = lastBolus,
            Source = "Care Portal",
        };
    }

    /// <inheritdoc />
    public IobResult FromTempBasals(
        List<TempBasal> pieces,
        TherapySnapshot snapshot,
        Func<TempBasal, long, double> scheduledUnder,
        long time,
        long deliveredUntil)
    {
        return FromTempBasalsCore(
            pieces,
            time,
            Math.Min(deliveredUntil, time),
            snapshot.Dia,
            snapshot.SensitivityAt(time),
            scheduledUnder);
    }

    private async Task<IobResult> FromTempBasalsAsync(
        List<TempBasal> tempBasals, long time, long cutoff, CancellationToken ct)
    {
        var pieces = StatisticsService.ClipOverlappingTempBasals(tempBasals, cutoff);
        if (pieces.Count == 0 || pieces[0].StartMills >= cutoff)
        {
            return FromTempBasalsCore([], time, cutoff, 0, 0, (_, _) => 0);
        }

        var scheduled = await ScheduledAsync(pieces[0].StartMills, cutoff, ct);
        var dia = await therapySettings.GetDIAAsync(time, null, ct);
        var sens = await sensitivity.GetSensitivityAsync(time, null, ct);

        return FromTempBasalsCore(pieces, time, cutoff, dia, sens, scheduled.Under);
    }

    private async Task<ScheduledBasal> ScheduledAsync(long from, long to, CancellationToken ct)
    {
        if (_scheduled is { } held && held.From == from && held.To == to)
            return held.Scheduled;

        var profile = new List<BasalSegment>();
        await foreach (var segment in basalSegments.GetSegmentsAsync(from, to + 1, ct))
            profile.Add(segment);
        var scheduled = new ScheduledBasal(profile, await ScheduledBasal.OverridesAsync(stateSpans, from, to, ct));
        _scheduled = (from, to, scheduled);
        return scheduled;
    }

    /// <param name="pieces">As <see cref="StatisticsService.ClipOverlappingTempBasals"/> returns them: ordered by start, each with an end.</param>
    private static IobResult FromTempBasalsCore(
        List<TempBasal> pieces,
        long time,
        long cutoff,
        double profileDia,
        double sens,
        Func<TempBasal, long, double> scheduledUnder)
    {
        double? basalIob = null;
        var activity = 0.0;

        foreach (var piece in pieces)
        {
            if (piece.StartMills >= cutoff)
                break;

            var contribution = CalcNetBasal(
                piece,
                Math.Min(piece.EndMills!.Value, cutoff),
                profileDia,
                sens,
                scheduledUnder,
                time);

            basalIob = (basalIob ?? 0) + contribution.IobContrib;
            activity += contribution.ActivityContrib;
        }

        var rounded = basalIob is { } sum ? RoundToThreeDecimals(sum) : (double?)null;
        return new IobResult
        {
            Iob = rounded ?? 0.0,
            BasalIob = rounded,
            Activity = activity,
            Source = "Care Portal",
        };
    }

    /// <summary>
    /// Net basal insulin on board from one delivered temp basal piece, on the model oref0, Loop,
    /// AAPS and Trio share. The piece's rate less the scheduled rate under it
    /// (<see cref="ScheduledBasal.Under"/>) is delivered in <see cref="NET_BASAL_CHUNK_MS"/> chunks
    /// from its start, the last one partial, and each chunk decays from its own start on the same
    /// insulin curve as a bolus (<see cref="CalcInsulin"/>).
    /// A temp below the schedule, or a suspend, withholds insulin, so its chunks are negative and so
    /// are the IOB and activity they give. Delivery stops at <paramref name="deliveryEnd"/>, the
    /// caller's cutoff at the latest; the curve is read at <paramref name="time"/>, which can be
    /// later.
    /// </summary>
    private static IobContribution CalcNetBasal(
        TempBasal piece,
        long deliveryEnd,
        double profileDia,
        double sens,
        Func<TempBasal, long, double> scheduledUnder,
        long time)
    {
        // A percent temp's Rate was resolved against the schedule at its start, so one that crosses a
        // schedule boundary keeps that rate after it.
        if ((piece.Origin == TempBasalOrigin.Suspended ? 0 : piece.Rate) is not { } rate)
            return new IobContribution { IobContrib = 0, ActivityContrib = 0 };

        var iob = 0.0;
        var activity = 0.0;

        // Chunks delivered a full DIA ago have left the curve, so the walk starts at the first that has not.
        var firstChunkStart = piece.StartMills;
        var expiredFor = time - CurveDia(piece.InsulinContext, profileDia) * 3_600_000.0 - piece.StartMills;
        if (expiredFor >= 0)
            firstChunkStart += ((long)Math.Floor(expiredFor / NET_BASAL_CHUNK_MS) + 1) * NET_BASAL_CHUNK_MS;

        for (var chunkStart = firstChunkStart; chunkStart < deliveryEnd; chunkStart += NET_BASAL_CHUNK_MS)
        {
            var chunkEnd = Math.Min(chunkStart + NET_BASAL_CHUNK_MS, deliveryEnd);

            var units = (rate - scheduledUnder(piece, chunkStart)) * (chunkEnd - chunkStart) / 3_600_000.0;

            var contribution = CalcInsulin(units, chunkStart, piece.InsulinContext, profileDia, sens, time);
            iob += contribution.IobContrib;
            activity += contribution.ActivityContrib;
        }

        return new IobContribution { IobContrib = iob, ActivityContrib = activity };
    }

    /// <summary>
    /// The local total, whose bolus and net basal parts, each rounded as reported, add up to its
    /// <see cref="IobResult.Iob"/>.
    /// </summary>
    private static IobResult Total(IobResult? bolus, IobResult? basal)
    {
        var bolusIob = bolus?.BolusIob ?? 0.0;
        var basalIob = basal?.BasalIob;
        return new IobResult
        {
            Iob = RoundToThreeDecimals(bolusIob + (basalIob ?? 0.0)),
            BolusIob = bolusIob,
            BasalIob = basalIob,
            Activity = (bolus?.Activity ?? 0.0) + (basal?.Activity ?? 0.0),
            LastBolus = bolus?.LastBolus,
            Source = "Care Portal",
        };
    }

    /// <summary>
    /// The device IOB that holds at <paramref name="time"/> by <see cref="DeviceReadings.At"/>, read
    /// from the newest APS and pump snapshot in its window; null where the calculation holds.
    /// </summary>
    internal async Task<IobResult?> GetDeviceIobAsync(long time, bool live, CancellationToken ct = default)
    {
        var (from, to) = DeviceReportedValues.Window(time, live);
        var fromTime = DateTimeOffset.FromUnixTimeMilliseconds(from).UtcDateTime;
        var toTime = DateTimeOffset.FromUnixTimeMilliseconds(to).UtcDateTime;

        var aps = (await apsSnapshotRepo.GetAsync(
            from: fromTime, to: toTime, device: null, source: null, limit: 1, offset: 0, descending: true, ct: ct))
            .Select(s => new DeviceReading(
                s.Timestamp.ToMills(), DeviceSource.Aps, s.Iob, s.Cob, s.BolusIob, s.BasalIob,
                s.AidAlgorithm == AidAlgorithm.Loop ? "Loop" : "OpenAPS", s.Device));
        var pump = (await pumpSnapshotRepo.GetAsync(
            from: fromTime, to: toTime, device: null, source: null, limit: 1, offset: 0, descending: true, ct: ct))
            .Select(s => DeviceReading.FromPump(new PumpIobPoint(s.Timestamp, s.Iob, s.BolusIob)) with { Device = s.Device });

        if (new DeviceReadings(aps, pump).At(time, live).Iob is not { } reading)
            return null;

        return new IobResult
        {
            Iob = reading.Iob!.Value,
            BolusIob = reading.BolusIob,
            BasalIob = reading.BasalIob,
            Source = reading.Name,
            Device = reading.Device,
            Mills = reading.Mills,
            ReportedByDevice = true,
        };
    }

    #region Helper Methods

    private static IobResult AddDisplay(IobResult iob)
    {
        iob.Display = IobResult.FormatDisplay(iob.Iob);
        iob.DisplayLine = $"IOB: {iob.Display}U";

        return iob;
    }

    private static double RoundToThreeDecimals(double num)
    {
        return Math.Round(num + double.Epsilon, 3);
    }

    #endregion
}
