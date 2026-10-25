using System.Globalization;
using Microsoft.Extensions.Logging;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.Core.Clinical.Treatments;

/// <summary>
/// V4-native COB calculator operating on <see cref="CarbIntake"/> records.
/// Implements the decay algorithm on V4 domain types directly.
/// </summary>
/// <seealso cref="ICobCalculator"/>
public class CobCalculator(
    ILogger<CobCalculator> logger,
    IIobCalculator iobCalculator,
    ISensitivityResolver sensitivityResolver,
    ICarbRatioResolver carbRatioResolver,
    ITherapySettingsResolver therapySettingsResolver,
    IApsSnapshotRepository apsSnapshotRepo
) : ICobCalculator
{
    private const double LIVER_SENS_RATIO = 8.0;
    private const int DELAY_MINUTES = 20;

    private const double DEFAULT_CARB_ABSORPTION_RATE = 30.0;
    private const double DEFAULT_SENSITIVITY = 95.0;
    private const double DEFAULT_CARB_RATIO = 18.0;

    /// <inheritdoc />
    public async Task<CobResult> CalculateTotalAsync(
        List<CarbIntake> carbIntakes,
        List<Bolus>? boluses,
        List<TempBasal>? tempBasals,
        long time,
        bool live = false,
        CancellationToken ct = default
    )
    {
        // A reported COB of exactly zero is a real value ("no carbs on board"), not a missing one.
        if (await GetDeviceCobAsync(time, live, ct) is { } deviceCob)
        {
            return AddDisplay(deviceCob);
        }

        var treatmentCOB =
            carbIntakes?.Any() == true
                ? await FromCarbIntakesAsync(carbIntakes, boluses, tempBasals, time, ct)
                : new CobResult();

        var result = new CobResult
        {
            Cob = treatmentCOB.Cob,
            Activity = treatmentCOB.Activity,
            DecayedBy = treatmentCOB.DecayedBy,
            IsDecaying = treatmentCOB.IsDecaying,
            CarbsHr = treatmentCOB.CarbsHr,
            RawCarbImpact = treatmentCOB.RawCarbImpact,
            LastCarbs = treatmentCOB.LastCarbs,
            Source = "Care Portal",
            TreatmentCOB = treatmentCOB,
        };

        return AddDisplay(result);
    }

    internal async Task<CobResult> FromCarbIntakesAsync(
        List<CarbIntake> carbIntakes,
        List<Bolus>? boluses,
        List<TempBasal>? tempBasals,
        long time,
        CancellationToken ct = default
    )
    {
        // Insulin activity for the dynamic carb-delay calc comes from the full IOB total
        // (boluses + temp basals + device snapshots). Carbs still absorbing ask for times after the
        // requested one, and a temp still running has delivered nothing past it.
        async Task<double> ActivityAt(long t) =>
            (
                await iobCalculator.CalculateTotalAsync(
                    boluses ?? [],
                    tempBasals,
                    t,
                    deliveredUntil: time,
                    ct: ct
                )
            )?.Activity ?? 0.0;

        var acc = new Accumulator();
        foreach (var carbIntake in SortedByTime(carbIntakes))
        {
            var profileRate = await GetCarbAbsorptionRateOrDefaultAsync(carbIntake.Mills, ct);
            if (!IsOnBoard(carbIntake, time) || Begin(acc, carbIntake, time, profileRate) is not { } step)
                continue;

            var (cCalc, decaysInHr) = step;
            if (decaysInHr > -10)
            {
                var actStart = await ActivityAt(acc.LastDecayedBy);
                var actEnd = await ActivityAt(cCalc.DecayedBy.ToUnixTimeMilliseconds());
                decaysInHr = ApplyDelay(
                    cCalc,
                    decaysInHr,
                    (actStart + actEnd) / 2.0,
                    await GetSensitivityOrDefaultAsync(carbIntake.Mills, ct),
                    await GetCarbRatioOrDefaultAsync(carbIntake.Mills, ct),
                    profileRate,
                    time
                );
            }

            Finish(acc, carbIntake, cCalc, decaysInHr, profileRate);
        }

        return ToResult(
            acc,
            await GetSensitivityOrDefaultAsync(time, ct),
            await GetCarbRatioOrDefaultAsync(time, ct),
            await GetCarbAbsorptionRateOrDefaultAsync(time, ct)
        );
    }

    /// <inheritdoc />
    public CobResult FromCarbIntakes(
        List<CarbIntake> carbIntakes,
        List<Bolus>? boluses,
        List<TempBasal>? tempBasals,
        TherapySnapshot snapshot,
        Func<TempBasal, long, double> scheduledUnder,
        long time
    )
    {
        // Every value comes from the in-memory snapshot, so the tick loop makes no DB round trips.
        double CarbAbsorptionAt(long _) =>
            snapshot.CarbsPerHour > 0 ? snapshot.CarbsPerHour : DEFAULT_CARB_ABSORPTION_RATE;
        double SensitivityAt(long t) =>
            snapshot.SensitivityAt(t) is var s && s > 0 ? s : DEFAULT_SENSITIVITY;
        double CarbRatioAt(long t) =>
            snapshot.CarbRatioAt(t) is var c && c > 0 ? c : DEFAULT_CARB_RATIO;

        // The bolus plus net basal activity the async IOB total gives, computed in-memory.
        double ActivityAt(long t) =>
            (iobCalculator.FromBoluses(boluses ?? [], snapshot, t).Activity ?? 0.0)
            + (iobCalculator.FromTempBasals(tempBasals ?? [], snapshot, scheduledUnder, t, deliveredUntil: time).Activity ?? 0);

        var acc = new Accumulator();
        foreach (var carbIntake in SortedByTime(carbIntakes))
        {
            var profileRate = CarbAbsorptionAt(carbIntake.Mills);
            if (!IsOnBoard(carbIntake, time) || Begin(acc, carbIntake, time, profileRate) is not { } step)
                continue;

            var (cCalc, decaysInHr) = step;
            if (decaysInHr > -10)
            {
                var actStart = ActivityAt(acc.LastDecayedBy);
                var actEnd = ActivityAt(cCalc.DecayedBy.ToUnixTimeMilliseconds());
                decaysInHr = ApplyDelay(
                    cCalc,
                    decaysInHr,
                    (actStart + actEnd) / 2.0,
                    SensitivityAt(carbIntake.Mills),
                    CarbRatioAt(carbIntake.Mills),
                    profileRate,
                    time
                );
            }

            Finish(acc, carbIntake, cCalc, decaysInHr, profileRate);
        }

        return ToResult(acc, SensitivityAt(time), CarbRatioAt(time), CarbAbsorptionAt(time));
    }

    #region Private Helpers

    private sealed class Accumulator
    {
        public double TotalCob;
        public CarbIntake? LastCarbs;
        public double IsDecaying;
        public long LastDecayedBy;
    }

    private static List<CarbIntake> SortedByTime(List<CarbIntake>? carbIntakes) =>
        (carbIntakes ?? []).OrderBy(c => c.Mills).ToList();

    private static bool IsOnBoard(CarbIntake carbIntake, long time) =>
        carbIntake.Carbs > 0 && carbIntake.Mills < time;

    private static double HoursUntil(long mills, long time) =>
        (mills - time) / 1000.0 / 60.0 / 60.0;

    private static double CarbsPerHour(CarbIntake carbIntake, double profileRate) =>
        carbIntake.AbsorptionTime.HasValue
            ? carbIntake.Carbs / (carbIntake.AbsorptionTime.Value / 60.0)
            : profileRate;

    private static (CobCalcResult Calc, double DecaysInHr)? Begin(
        Accumulator acc,
        CarbIntake carbIntake,
        long currentTime,
        double profileRate
    )
    {
        acc.LastCarbs = carbIntake;
        var cCalc = CobCalc(carbIntake, acc.LastDecayedBy, currentTime, profileRate);
        return cCalc is null
            ? null
            : (cCalc, HoursUntil(cCalc.DecayedBy.ToUnixTimeMilliseconds(), currentTime));
    }

    private static double ApplyDelay(
        CobCalcResult cCalc,
        double decaysInHr,
        double avgActivity,
        double sens,
        double carbRatio,
        double profileRate,
        long currentTime
    )
    {
        var delayedCarbs = carbRatio * ((avgActivity * LIVER_SENS_RATIO) / sens);
        var delayMinutes = Math.Round((delayedCarbs / profileRate) * 60);
        if (delayMinutes <= 0)
            return decaysInHr;

        cCalc.DecayedBy = cCalc.DecayedBy.AddMinutes(delayMinutes);
        return HoursUntil(cCalc.DecayedBy.ToUnixTimeMilliseconds(), currentTime);
    }

    private static void Finish(
        Accumulator acc,
        CarbIntake carbIntake,
        CobCalcResult cCalc,
        double decaysInHr,
        double profileRate
    )
    {
        acc.LastDecayedBy = cCalc.DecayedBy.ToUnixTimeMilliseconds();

        if (decaysInHr > 0)
        {
            acc.TotalCob += Math.Min(carbIntake.Carbs, decaysInHr * profileRate);
            acc.IsDecaying = cCalc.IsDecaying;
        }
        else
        {
            acc.TotalCob = 0;
        }
    }

    private static CobResult ToResult(
        Accumulator acc,
        double sens,
        double carbRatio,
        double carbAbsorptionRate
    ) =>
        new()
        {
            DecayedBy = acc.LastDecayedBy,
            IsDecaying = acc.IsDecaying,
            CarbsHr = carbAbsorptionRate,
            RawCarbImpact = (((acc.IsDecaying * sens) / carbRatio) * carbAbsorptionRate) / 60.0,
            Cob = acc.TotalCob,
            LastCarbs = acc.LastCarbs,
        };

    private static CobCalcResult? CobCalc(
        CarbIntake carbIntake,
        long lastDecayedBy,
        long time,
        double profileRate
    )
    {
        if (carbIntake.Carbs <= 0)
        {
            return null;
        }

        const int delay = DELAY_MINUTES;
        var carbTime = DateTimeOffset.FromUnixTimeMilliseconds(carbIntake.Mills);

        var carbsMin = CarbsPerHour(carbIntake, profileRate) / 60.0;
        var decayedBy = carbTime;
        var minutesleft =
            lastDecayedBy > 0 ? (lastDecayedBy - carbIntake.Mills) / 1000.0 / 60.0 : 0.0;

        var additionalMinutes = Math.Max(delay, minutesleft) + (carbIntake.Carbs / carbsMin);
        decayedBy = decayedBy.AddMinutes(additionalMinutes);

        var initialCarbs =
            delay > minutesleft
                ? Convert.ToInt32(carbIntake.Carbs)
                : Convert.ToInt32(carbIntake.Carbs) + (minutesleft * carbsMin);

        var startDecay = carbTime.AddMinutes(delay);
        var isDecaying =
            time < lastDecayedBy || time > startDecay.ToUnixTimeMilliseconds() ? 1.0 : 0.0;

        return new CobCalcResult
        {
            InitialCarbs = initialCarbs,
            DecayedBy = decayedBy,
            IsDecaying = isDecaying,
            CarbTime = carbTime,
        };
    }

    /// <summary>
    /// The device COB that holds at <paramref name="time"/> by <see cref="DeviceReadings.At"/>, read
    /// from the newest APS snapshot in its window; a pump reports no COB. Null where the calculation holds.
    /// </summary>
    internal async Task<CobResult?> GetDeviceCobAsync(long time, bool live, CancellationToken ct = default)
    {
        var (from, to) = DeviceReportedValues.Window(time, live);
        var aps = (await apsSnapshotRepo.GetAsync(
            from: DateTimeOffset.FromUnixTimeMilliseconds(from).UtcDateTime,
            to: DateTimeOffset.FromUnixTimeMilliseconds(to).UtcDateTime,
            device: null, source: null, limit: 1, offset: 0, descending: true, ct: ct))
            .Select(s => new DeviceReading(
                s.Timestamp.ToMills(), DeviceSource.Aps, s.Iob, s.Cob,
                Name: s.AidAlgorithm == AidAlgorithm.Loop ? "Loop" : "OpenAPS", Device: s.Device));

        if (new DeviceReadings(aps, []).At(time, live).Cob is not { } reading)
            return null;

        return new CobResult
        {
            Cob = reading.Cob!.Value,
            Source = reading.Name,
            Device = reading.Device,
            Mills = reading.Mills,
        };
    }

    private static CobResult AddDisplay(CobResult cob)
    {
        if (cob.Cob <= 0)
            return cob;

        // Invariant: these strings go out over the API, so they must not pick up the
        // server's decimal separator.
        var display = (Math.Round(cob.Cob * 10) / 10).ToString(CultureInfo.InvariantCulture);
        cob.Display = display;
        cob.DisplayLine = $"COB: {display}g";

        return cob;
    }

    private async Task<double> GetCarbAbsorptionRateOrDefaultAsync(long time, CancellationToken ct)
    {
        try
        {
            var value = await therapySettingsResolver.GetCarbAbsorptionRateAsync(time, null, ct);
            return value > 0 ? value : DEFAULT_CARB_ABSORPTION_RATE;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to resolve carb absorption rate at {Time}; using default.", time);
            return DEFAULT_CARB_ABSORPTION_RATE;
        }
    }

    private async Task<double> GetSensitivityOrDefaultAsync(long time, CancellationToken ct)
    {
        try
        {
            var value = await sensitivityResolver.GetSensitivityAsync(time, null, ct);
            return value > 0 ? value : DEFAULT_SENSITIVITY;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to resolve sensitivity at {Time}; using default.", time);
            return DEFAULT_SENSITIVITY;
        }
    }

    private async Task<double> GetCarbRatioOrDefaultAsync(long time, CancellationToken ct)
    {
        try
        {
            var value = await carbRatioResolver.GetCarbRatioAsync(time, null, ct);
            return value > 0 ? value : DEFAULT_CARB_RATIO;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to resolve carb ratio at {Time}; using default.", time);
            return DEFAULT_CARB_RATIO;
        }
    }

    #endregion
}
