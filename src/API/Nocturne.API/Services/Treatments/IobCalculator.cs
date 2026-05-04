using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.Treatments;

/// <summary>
/// V4-native IOB calculator operating on <see cref="Bolus"/> and <see cref="TempBasal"/> records.
/// Implements the two-phase exponential decay curve, accepting V4 domain types directly.
/// </summary>
/// <remarks>
/// Per-bolus <see cref="TreatmentInsulinContext"/> overrides profile-level DIA and peak values
/// when available, enabling accurate multi-insulin IOB calculations without data loss.
/// </remarks>
/// <seealso cref="IIobCalculator"/>
public class IobCalculator(
    ITherapySettingsResolver therapySettings,
    ISensitivityResolver sensitivity,
    IBasalRateResolver basalRate,
    IApsSnapshotRepository apsSnapshotRepo,
    IPumpSnapshotRepository pumpSnapshotRepo
) : IIobCalculator
{
    // Constants from legacy implementation (identical to IobService)
    private const long RECENCY_THRESHOLD = 30 * 60 * 1000; // 30 minutes in milliseconds
    private const double DEFAULT_DIA = 3.0;
    private const double SCALE_FACTOR_BASE = 3.0;
    private const double PEAK_MINUTES = 75.0;
    private const double MAX_IOB_MINUTES = 180.0;

    /// <inheritdoc />
    public async Task<IobResult> CalculateTotalAsync(
        List<Bolus> boluses,
        List<TempBasal>? tempBasals = null,
        long? time = null,
        CancellationToken ct = default
    )
    {
        var currentTime = time ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // Get IOB from device snapshots (APS, pump) - prioritized source
        var result = await GetLatestDeviceIobAsync(currentTime, ct);

        // Calculate IOB from boluses
        var bolusResult =
            boluses?.Any() == true
                ? FromBoluses(boluses, currentTime)
                : new IobResult();

        // Calculate basal IOB from V4 TempBasal records
        var tempBasalResult =
            tempBasals?.Any() == true
                ? FromTempBasals(tempBasals, currentTime)
                : new IobResult();

        // Merge TempBasal basal IOB into the bolus result
        if (tempBasalResult.BasalIob.HasValue)
        {
            bolusResult.BasalIob = (bolusResult.BasalIob ?? 0) + tempBasalResult.BasalIob.Value;
            bolusResult.Activity = (bolusResult.Activity ?? 0) + (tempBasalResult.Activity ?? 0);
        }

        if (IsEmpty(result))
        {
            result = bolusResult;
        }
        else
        {
            // Add bolus IOB as separate property for device status sources
            if (bolusResult.Iob > 0)
            {
                result.TreatmentIob = RoundToThreeDecimals(bolusResult.Iob);
            }

            // Add bolus basal IOB to device status basal IOB if available
            if (bolusResult.BasalIob.HasValue)
            {
                result.BasalIob = (result.BasalIob ?? 0) + bolusResult.BasalIob.Value;
                result.BasalIob = RoundToThreeDecimals(result.BasalIob.Value);
            }
        }

        // Apply final rounding to IOB
        if (result.Iob > 0)
        {
            result.Iob = RoundToThreeDecimals(result.Iob);
        }

        return AddDisplay(result);
    }

    /// <inheritdoc />
    public IobContribution CalcBolus(Bolus bolus, long? time = null)
    {
        if (bolus.Insulin <= 0)
        {
            return new IobContribution { IobContrib = 0, ActivityContrib = 0 };
        }

        var currentTime = time ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // Per-bolus insulin context takes priority over profile DIA/peak
        var dia = bolus.InsulinContext?.Dia
            ?? therapySettings.GetDIAAsync(currentTime, null).GetAwaiter().GetResult();
        var peak = bolus.InsulinContext?.Peak
            ?? PEAK_MINUTES;
        var sens = sensitivity.GetSensitivityAsync(currentTime, null).GetAwaiter().GetResult();

        // Exact legacy algorithm constants
        var scaleFactor = SCALE_FACTOR_BASE / dia;

        var bolusTime = bolus.Mills;
        var minAgo = (scaleFactor * (currentTime - bolusTime)) / 1000.0 / 60.0;

        // Before peak (0-75 minutes): curved rise
        if (minAgo < peak)
        {
            var x1 = minAgo / 5.0 + 1.0;
            var iobContrib = bolus.Insulin * (1.0 - 0.001852 * x1 * x1 + 0.001852 * x1);
            var activityContrib =
                sens * bolus.Insulin * (2.0 / dia / 60.0 / peak) * minAgo;

            return new IobContribution
            {
                IobContrib = Math.Max(0.0, iobContrib),
                ActivityContrib = activityContrib,
            };
        }

        // After peak (75-180 minutes): curved decline
        if (minAgo < MAX_IOB_MINUTES)
        {
            var x2 = (minAgo - peak) / 5.0;
            var iobContrib =
                bolus.Insulin * (0.001323 * x2 * x2 - 0.054233 * x2 + 0.55556);
            var activityContrib =
                sens
                * bolus.Insulin
                * (2.0 / dia / 60.0 - ((minAgo - peak) * 2.0) / dia / 60.0 / (60.0 * 3.0 - peak));

            return new IobContribution
            {
                IobContrib = Math.Max(0.0, iobContrib),
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

        if (boluses?.Any() != true)
        {
            return new IobResult
            {
                Iob = 0.0,
                Activity = 0.0,
                Source = "Care Portal",
            };
        }

        var totalIob = 0.0;
        var totalActivity = 0.0;
        Bolus? lastBolus = null;

        foreach (var bolus in boluses.Where(b => b.Mills <= currentTime && b.Insulin > 0))
        {
            var contribution = CalcBolus(bolus, currentTime);

            totalIob += contribution.IobContrib;
            totalActivity += contribution.ActivityContrib;

            if (lastBolus == null || bolus.Mills > lastBolus.Mills)
            {
                lastBolus = bolus;
            }
        }

        return new IobResult
        {
            Iob = RoundToThreeDecimals(totalIob),
            Activity = totalActivity,
            LastBolus = lastBolus,
            Source = "Care Portal",
        };
    }

    /// <inheritdoc />
    public IobContribution CalcTempBasal(TempBasal tempBasal, long? time = null)
    {
        var currentTime = time ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        if (!tempBasal.EndMills.HasValue)
        {
            return new IobContribution { IobContrib = 0, ActivityContrib = 0 };
        }

        var dia = therapySettings.GetDIAAsync(currentTime, null).GetAwaiter().GetResult();

        var scheduledBasalRate = tempBasal.ScheduledRate
            ?? basalRate.GetBasalRateAsync(tempBasal.StartMills, null).GetAwaiter().GetResult();

        var treatmentStart = tempBasal.StartMills;
        var treatmentEnd = tempBasal.EndMills.Value;

        if (currentTime <= treatmentStart)
        {
            return new IobContribution { IobContrib = 0, ActivityContrib = 0 };
        }

        var effectiveEnd = Math.Min(currentTime, treatmentEnd);
        var durationActual = (effectiveEnd - treatmentStart) / 1000.0 / 60.0;

        var rate = tempBasal.Origin == TempBasalOrigin.Suspended ? 0 : tempBasal.Rate;
        var excessInsulin = Math.Max(0, (rate - scheduledBasalRate) * (durationActual / 60.0));

        if (excessInsulin <= 0)
        {
            return new IobContribution { IobContrib = 0, ActivityContrib = 0 };
        }

        var minAgo = (currentTime - treatmentStart) / 1000.0 / 60.0;
        var diaMinutes = dia * 60.0;

        if (minAgo < diaMinutes)
        {
            var decayFactor = Math.Max(0, 1.0 - (minAgo / diaMinutes));
            var basalIob = excessInsulin * decayFactor;

            return new IobContribution
            {
                IobContrib = RoundToThreeDecimals(basalIob),
                ActivityContrib = 0,
            };
        }

        return new IobContribution { IobContrib = 0, ActivityContrib = 0 };
    }

    /// <inheritdoc />
    public IobResult FromTempBasals(List<TempBasal> tempBasals, long? time = null)
    {
        var currentTime = time ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        if (tempBasals?.Any() != true)
        {
            return new IobResult
            {
                Iob = 0.0,
                Activity = 0.0,
                Source = "Care Portal",
            };
        }

        var totalBasalIob = 0.0;
        var totalActivity = 0.0;

        foreach (var tempBasal in tempBasals.Where(tb => tb.StartMills <= currentTime))
        {
            var contribution = CalcTempBasal(tempBasal, currentTime);
            totalBasalIob += contribution.IobContrib;
            totalActivity += contribution.ActivityContrib;
        }

        return new IobResult
        {
            Iob = 0.0, // Basal IOB does not contribute to bolus IOB
            BasalIob = totalBasalIob > 0 ? RoundToThreeDecimals(totalBasalIob) : null,
            Activity = totalActivity,
            Source = "Care Portal",
        };
    }

    /// <summary>
    /// Query <see cref="IApsSnapshotRepository"/> and <see cref="IPumpSnapshotRepository"/>
    /// for the most recent device-reported IOB within the staleness window.
    /// </summary>
    internal async Task<IobResult> GetLatestDeviceIobAsync(long time, CancellationToken ct = default)
    {
        var futureMills = time + 5 * 60 * 1000;
        var recentMills = time - RECENCY_THRESHOLD;

        var recentTime = DateTimeOffset.FromUnixTimeMilliseconds(recentMills).UtcDateTime;
        var futureTime = DateTimeOffset.FromUnixTimeMilliseconds(futureMills).UtcDateTime;

        // Try APS snapshot first (highest priority: Loop, OpenAPS, AAPS, Trio)
        var apsSnapshots = await apsSnapshotRepo.GetAsync(
            from: recentTime,
            to: futureTime,
            device: null,
            source: null,
            limit: 1,
            offset: 0,
            descending: true,
            ct: ct
        );

        var apsSnapshot = apsSnapshots.FirstOrDefault();
        if (apsSnapshot != null)
        {
            var source = apsSnapshot.AidAlgorithm switch
            {
                AidAlgorithm.Loop => "Loop",
                _ => "OpenAPS",
            };

            return new IobResult
            {
                Iob = apsSnapshot.Iob ?? 0.0,
                BasalIob = apsSnapshot.BasalIob,
                Source = source,
                Device = apsSnapshot.Device,
                Mills = new DateTimeOffset(apsSnapshot.Timestamp, TimeSpan.Zero).ToUnixTimeMilliseconds(),
            };
        }

        // Fall back to pump snapshot
        var pumpSnapshots = await pumpSnapshotRepo.GetAsync(
            from: recentTime,
            to: futureTime,
            device: null,
            source: null,
            limit: 1,
            offset: 0,
            descending: true,
            ct: ct
        );

        var pumpSnapshot = pumpSnapshots.FirstOrDefault();
        if (pumpSnapshot != null)
        {
            var iobValue = pumpSnapshot.Iob ?? pumpSnapshot.BolusIob ?? 0.0;

            return new IobResult
            {
                Iob = iobValue,
                Source = "Pump",
                Device = pumpSnapshot.Device,
                Mills = new DateTimeOffset(pumpSnapshot.Timestamp, TimeSpan.Zero).ToUnixTimeMilliseconds(),
            };
        }

        return new IobResult();
    }

    #region Helper Methods

    private static IobResult AddDisplay(IobResult iob)
    {
        if (IsEmpty(iob) || iob.Iob <= 0)
        {
            return iob;
        }

        var display = iob.Iob.ToString("F2");
        iob.Display = display;
        iob.DisplayLine = $"IOB: {display}U";

        return iob;
    }

    private static bool IsEmpty(IobResult? iob)
    {
        return iob == null || (iob.Iob <= 0 && !iob.BasalIob.HasValue && !iob.Activity.HasValue);
    }

    private static double RoundToThreeDecimals(double num)
    {
        return Math.Round(num + double.Epsilon, 3);
    }

    #endregion
}
