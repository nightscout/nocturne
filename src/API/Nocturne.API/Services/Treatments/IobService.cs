using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.Treatments;

/// <summary>
/// Implementation of Insulin on Board (IOB) calculations with exact 1:1 legacy JavaScript compatibility.
/// Computes IOB from three sources: <see cref="ApsSnapshot"/> (Loop, OpenAPS, AAPS),
/// <see cref="PumpSnapshot"/> (pump-reported IOB), <see cref="Treatment"/> bolus/temp-basal records,
/// and V4 <see cref="TempBasal"/> records.
/// </summary>
/// <remarks>
/// The bolus IOB curve uses a two-phase model:
/// <list type="bullet">
///   <item>Before peak (0-75 min): curved rise with quadratic approximation.</item>
///   <item>After peak (75-180 min): curved decline to zero.</item>
/// </list>
/// Per-treatment <see cref="TreatmentInsulinContext"/> overrides profile-level DIA and peak values
/// when available, enabling accurate multi-insulin calculations.
/// </remarks>
/// <seealso cref="IIobService"/>
/// <seealso cref="CobService"/>
/// <seealso cref="TreatmentService"/>
public class IobService(
    IApsSnapshotRepository apsSnapshotRepo,
    IPumpSnapshotRepository pumpSnapshotRepo,
    ITherapyTimelineResolver therapyTimelineResolver
) : IIobService
{
    // Constants from legacy implementation
    private const long RECENCY_THRESHOLD = 30 * 60 * 1000;
    private const double SCALE_FACTOR_BASE = 3.0;
    private const double MAX_IOB_MINUTES = 180.0;

    /// <summary>
    /// Main IOB calculation function that combines device snapshot and <see cref="Treatment"/> data.
    /// Queries <see cref="IApsSnapshotRepository"/> and <see cref="IPumpSnapshotRepository"/> internally
    /// for device-reported IOB, falling back to treatment-based calculation when no recent device data exists.
    /// </summary>
    /// <remarks>
    /// Priority: APS snapshot IOB (Loop/OpenAPS/AAPS) > pump snapshot IOB > treatment-based IOB.
    /// V4 <see cref="TempBasal"/> basal IOB is always merged into the treatment result regardless of source priority.
    /// </remarks>
    public async Task<IobResult> CalculateTotalAsync(
        List<Treatment> treatments,
        long? time = null,
        string? specProfile = null,
        List<TempBasal>? tempBasals = null,
        CancellationToken ct = default
    )
    {
        var currentTime = time ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // Get IOB from device snapshots (APS, pump) - prioritized source
        var result = await GetLatestDeviceIobAsync(currentTime, ct);

        // Resolve therapy snapshot once for this calculation time, then run treatment/temp-basal
        // IOB synchronously against it.
        var snapshot = await therapyTimelineResolver.GetSnapshotAtAsync(currentTime, specProfile, ct);

        // Calculate IOB from treatments (Care Portal entries)
        var treatmentResult =
            treatments?.Count > 0
                ? FromTreatments((IReadOnlyList<Treatment>)treatments, currentTime, snapshot)
                : new IobResult();

        // Calculate basal IOB from V4 TempBasal records (parallel path to legacy treatment-based basal IOB)
        var tempBasalResult =
            tempBasals?.Count > 0
                ? FromTempBasals((IReadOnlyList<TempBasal>)tempBasals, currentTime, snapshot)
                : new IobResult();

        // Merge V4 TempBasal basal IOB into the treatment result
        if (tempBasalResult.BasalIob.HasValue)
        {
            treatmentResult.BasalIob = (treatmentResult.BasalIob ?? 0) + tempBasalResult.BasalIob.Value;
            treatmentResult.Activity = (treatmentResult.Activity ?? 0) + (tempBasalResult.Activity ?? 0);
        }

        if (IsEmpty(result))
        {
            result = treatmentResult;
        }
        else
        {
            // Add treatment IOB as separate property for device status sources
            if (treatmentResult.Iob > 0)
            {
                result.TreatmentIob = RoundToThreeDecimals(treatmentResult.Iob);
            }

            // Add treatment basal IOB to device status basal IOB if available
            if (treatmentResult.BasalIob.HasValue)
            {
                result.BasalIob = (result.BasalIob ?? 0) + treatmentResult.BasalIob.Value;
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

    /// <summary>
    /// Query <see cref="IApsSnapshotRepository"/> and <see cref="IPumpSnapshotRepository"/>
    /// for the most recent device-reported IOB within the staleness window.
    /// </summary>
    internal async Task<IobResult> GetLatestDeviceIobAsync(long time, CancellationToken ct = default)
    {
        var futureMills = time + 5 * 60 * 1000; // Allow for clocks to be a little off
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
            // Legacy V1 API only exposed two source strings: "Loop" and "OpenAPS".
            // All non-Loop AID algorithms (OpenAPS, AAPS, Trio, etc.) map to "OpenAPS".
            var source = apsSnapshot.AidAlgorithm switch
            {
                AidAlgorithm.Loop => "Loop",
                _ => "OpenAPS",
            };

            // ApsSnapshot does not carry an Activity value — the APS algorithm's metabolic
            // activity rate was not migrated to the V4 model. COB decay calculations that
            // need Activity will fall through to the treatment-based value merged by
            // CalculateTotalAsync.
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

    /// <inheritdoc />
    public IobResult FromTreatments(
        IReadOnlyList<Treatment> treatments,
        long currentTime,
        TherapySnapshot snapshot
    )
    {
        if (treatments.Count == 0)
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
        var totalBasalIob = 0.0;
        Treatment? lastBolus = null;

        foreach (var treatment in treatments)
        {
            if (treatment.Mills > currentTime)
            {
                continue;
            }

            if (treatment.Insulin.HasValue && treatment.Insulin.Value > 0)
            {
                var contribution = CalcTreatment(treatment, currentTime, snapshot);

                if (contribution.IobContrib > 0)
                {
                    lastBolus = treatment;
                }

                totalIob += contribution.IobContrib;
                totalActivity += contribution.ActivityContrib;
            }

            if (treatment.EventType == "Temp Basal" && treatment.Duration.HasValue)
            {
                var basalIob = CalcBasalTreatment(treatment, currentTime, snapshot);
                totalBasalIob += basalIob.IobContrib;
                totalActivity += basalIob.ActivityContrib;
            }
        }

        return new IobResult
        {
            Iob = RoundToThreeDecimals(totalIob),
            BasalIob = totalBasalIob > 0 ? RoundToThreeDecimals(totalBasalIob) : null,
            Activity = totalActivity,
            LastBolus = lastBolus,
            Source = "Care Portal",
        };
    }

    /// <inheritdoc />
    public IobResult FromTempBasals(
        IReadOnlyList<TempBasal> tempBasals,
        long currentTime,
        TherapySnapshot snapshot
    )
    {
        if (tempBasals.Count == 0)
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

        foreach (var tempBasal in tempBasals)
        {
            if (tempBasal.StartMills <= currentTime)
            {
                var contribution = CalcTempBasalIob(tempBasal, currentTime, snapshot);
                totalBasalIob += contribution.IobContrib;
                totalActivity += contribution.ActivityContrib;
            }
        }

        return new IobResult
        {
            Iob = 0.0,
            BasalIob = totalBasalIob > 0 ? RoundToThreeDecimals(totalBasalIob) : null,
            Activity = totalActivity,
            Source = "Care Portal",
        };
    }

    /// <inheritdoc />
    public IobContribution CalcTreatment(
        Treatment treatment,
        long currentTime,
        TherapySnapshot snapshot
    )
    {
        if (!treatment.Insulin.HasValue || treatment.Insulin.Value <= 0)
        {
            return new IobContribution { IobContrib = 0, ActivityContrib = 0 };
        }

        var dia = treatment.InsulinContext?.Dia ?? snapshot.Dia;
        double peak = treatment.InsulinContext?.Peak ?? snapshot.PeakMinutes;
        var sens = snapshot.SensitivityAt(currentTime);

        var scaleFactor = SCALE_FACTOR_BASE / dia;

        var bolusTime = treatment.Mills;
        var minAgo = (scaleFactor * (currentTime - bolusTime)) / 1000.0 / 60.0;

        if (minAgo < peak)
        {
            var x1 = minAgo / 5.0 + 1.0;
            var iobContrib = treatment.Insulin.Value * (1.0 - 0.001852 * x1 * x1 + 0.001852 * x1);
            var activityContrib =
                sens * treatment.Insulin.Value * (2.0 / dia / 60.0 / peak) * minAgo;

            return new IobContribution
            {
                IobContrib = Math.Max(0.0, iobContrib),
                ActivityContrib = activityContrib,
            };
        }

        if (minAgo < MAX_IOB_MINUTES)
        {
            var x2 = (minAgo - peak) / 5.0;
            var iobContrib =
                treatment.Insulin.Value * (0.001323 * x2 * x2 - 0.054233 * x2 + 0.55556);
            var activityContrib =
                sens
                * treatment.Insulin.Value
                * (2.0 / dia / 60.0 - ((minAgo - peak) * 2.0) / dia / 60.0 / (60.0 * 3.0 - peak));

            return new IobContribution
            {
                IobContrib = Math.Max(0.0, iobContrib),
                ActivityContrib = activityContrib,
            };
        }

        return new IobContribution { IobContrib = 0, ActivityContrib = 0 };
    }

    /// <inheritdoc />
    public IobContribution CalcBasalTreatment(
        Treatment treatment,
        long currentTime,
        TherapySnapshot snapshot
    )
    {
        if (
            treatment.EventType != "Temp Basal"
            || !treatment.Duration.HasValue
            || !treatment.Absolute.HasValue
        )
        {
            return new IobContribution { IobContrib = 0, ActivityContrib = 0 };
        }

        var dia = snapshot.Dia;
        var basalRateValue = snapshot.BasalRateAt(currentTime);

        var treatmentStart = treatment.Mills;
        var treatmentEnd = treatmentStart + (treatment.Duration.Value * 60 * 1000);

        if (currentTime <= treatmentStart)
        {
            return new IobContribution { IobContrib = 0, ActivityContrib = 0 };
        }

        var effectiveEnd = Math.Min(currentTime, treatmentEnd);
        var durationActual = (effectiveEnd - treatmentStart) / 1000.0 / 60.0;
        var tempRate = treatment.Absolute.Value;
        var excessInsulin = Math.Max(0, (tempRate - basalRateValue) * (durationActual / 60.0));

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
    public IobContribution CalcTempBasalIob(
        TempBasal tempBasal,
        long currentTime,
        TherapySnapshot snapshot
    )
    {
        if (!tempBasal.EndMills.HasValue)
        {
            return new IobContribution { IobContrib = 0, ActivityContrib = 0 };
        }

        var dia = snapshot.Dia;
        var scheduledBasalRate = tempBasal.ScheduledRate ?? snapshot.BasalRateAt(tempBasal.StartMills);

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
