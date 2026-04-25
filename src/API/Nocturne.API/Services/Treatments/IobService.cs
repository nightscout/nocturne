using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.Treatments;

/// <summary>
/// Implementation of Insulin on Board (IOB) calculations with exact 1:1 legacy JavaScript compatibility.
/// Computes IOB from three sources: <see cref="DeviceStatus"/> (Loop, OpenAPS, pump),
/// <see cref="Treatment"/> bolus/temp-basal records, and V4 <see cref="TempBasal"/> records.
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
    ITherapySettingsResolver therapySettings,
    ISensitivityResolver sensitivity,
    IBasalRateResolver basalRate
) : IIobService
{
    // Constants from legacy implementation
    private const long RECENCY_THRESHOLD = 30 * 60 * 1000; // 30 minutes in milliseconds
    private const double DEFAULT_DIA = 3.0; // Default Duration of Insulin Action in hours
    private const double SCALE_FACTOR_BASE = 3.0; // Base for scale factor calculation
    private const double PEAK_MINUTES = 75.0; // Peak insulin action at 75 minutes
    private const double MAX_IOB_MINUTES = 180.0; // IOB calculation cutoff at 180 minutes

    /// <summary>
    /// Main IOB calculation function that combines <see cref="DeviceStatus"/> and <see cref="Treatment"/> data.
    /// Exact implementation of legacy calcTotal function.
    /// </summary>
    /// <remarks>
    /// Priority: device status IOB (Loop/OpenAPS/pump) takes precedence. If unavailable,
    /// treatment-based IOB is used. V4 <see cref="TempBasal"/> basal IOB is always merged
    /// into the treatment result regardless of source priority.
    /// </remarks>
    public IobResult CalculateTotal(
        List<Treatment> treatments,
        List<DeviceStatus> deviceStatus,
        long? time = null,
        string? specProfile = null,
        List<TempBasal>? tempBasals = null
    )
    {
        var currentTime = time ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // Get IOB from device status (pumps, OpenAPS, Loop) - prioritized source
        var result = LastIobDeviceStatus(deviceStatus, currentTime);

        // Calculate IOB from treatments (Care Portal entries)
        var treatmentResult =
            treatments?.Any() == true
                ? FromTreatments(treatments, currentTime, specProfile)
                : new IobResult();

        // Calculate basal IOB from V4 TempBasal records (parallel path to legacy treatment-based basal IOB)
        var tempBasalResult =
            tempBasals?.Any() == true
                ? FromTempBasals(tempBasals, currentTime, specProfile)
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
    /// Get the most recent IOB from <see cref="DeviceStatus"/> entries with prioritization.
    /// Exact implementation of legacy lastIOBDeviceStatus function.
    /// </summary>
    public IobResult LastIobDeviceStatus(List<DeviceStatus> deviceStatus, long time)
    {
        if (deviceStatus?.Any() != true)
        {
            return new IobResult();
        }

        var futureMills = time + 5 * 60 * 1000; // Allow for clocks to be a little off
        var recentMills = time - RECENCY_THRESHOLD; // Get all IOBs within time range
        var iobs = deviceStatus
            .Where(status =>
                status.Mills > 0 && status.Mills <= futureMills && status.Mills >= recentMills
            )
            .Select(FromDeviceStatus)
            .Where(item => !IsEmpty(item))
            .OrderBy(iob => iob.Mills ?? 0)
            .ToList();

        if (!iobs.Any())
        {
            return new IobResult();
        }

        // Prioritize Loop IOBs if available (highest priority)
        var loopIobs = iobs.Where(iob => iob.Source == "Loop").ToList();
        if (loopIobs.Any())
        {
            return loopIobs.Last(); // Most recent Loop IOB
        }

        // Return the most recent IOB entry
        return iobs.Last();
    }

    /// <summary>
    /// Extract IOB from a single <see cref="DeviceStatus"/> entry.
    /// Priority: Loop > OpenAPS > Pump (MM Connect).
    /// </summary>
    public IobResult FromDeviceStatus(DeviceStatus deviceStatusEntry)
    {
        // Highest priority: Loop IOB
        if (HasLoopIob(deviceStatusEntry))
        {
            var loopIob = deviceStatusEntry.Loop!.Iob!;
            var timestamp = deviceStatusEntry.Mills; // fallback

            if (
                !string.IsNullOrEmpty(loopIob.Timestamp)
                && DateTimeOffset.TryParse(loopIob.Timestamp, out var parsedTime)
            )
            {
                timestamp = parsedTime.ToUnixTimeMilliseconds();
            }

            return new IobResult
            {
                Iob = loopIob.Iob ?? 0.0,
                Source = "Loop",
                Device = deviceStatusEntry.Device,
                Mills = timestamp,
            };
        }

        // Second priority: OpenAPS IOB
        if (HasOpenApsIob(deviceStatusEntry))
        {
            var openApsIob = deviceStatusEntry.OpenAps!.Iob!;

            var iobValue = openApsIob.Iob ?? 0.0;
            var basalIobValue = openApsIob.BasalIob;
            var activityValue = openApsIob.Activity;

            // Handle timestamp field variations (time vs timestamp)
            var timestampStr = openApsIob.Timestamp ?? openApsIob.Time;
            var timestamp = deviceStatusEntry.Mills; // fallback

            if (
                !string.IsNullOrEmpty(timestampStr)
                && DateTimeOffset.TryParse(timestampStr, out var parsedTime)
            )
            {
                timestamp = parsedTime.ToUnixTimeMilliseconds();
            }

            return new IobResult
            {
                Iob = iobValue,
                BasalIob = basalIobValue,
                Activity = activityValue,
                Source = "OpenAPS",
                Device = deviceStatusEntry.Device,
                Mills = timestamp,
            };
        }

        // Third priority: Pump IOB (MM Connect)
        if (HasPumpIob(deviceStatusEntry))
        {
            var pumpIob = deviceStatusEntry.Pump!.Iob!;
            var iobValue = pumpIob.Iob ?? pumpIob.BolusIob ?? 0.0;

            var source = deviceStatusEntry.Connect != null ? "MM Connect" : "Pump";

            return new IobResult
            {
                Iob = iobValue,
                Source = source,
                Device = deviceStatusEntry.Device,
                Mills = deviceStatusEntry.Mills,
            };
        }

        return new IobResult();
    }

    /// <summary>
    /// Calculate IOB from <see cref="Treatment"/> records (Care Portal entries) with exact legacy algorithm.
    /// </summary>
    public IobResult FromTreatments(
        List<Treatment> treatments,
        long? time = null,
        string? specProfile = null
    )
    {
        var currentTime = time ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        if (treatments?.Any() != true)
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
            if (treatment.Mills <= currentTime)
            {
                // Calculate bolus IOB from treatments with insulin
                if (treatment.Insulin.HasValue && treatment.Insulin.Value > 0)
                {
                    var contribution = CalcTreatment(treatment, currentTime, specProfile);

                    if (contribution.IobContrib > 0)
                    {
                        lastBolus = treatment;
                    }

                    totalIob += contribution.IobContrib;
                    totalActivity += contribution.ActivityContrib;
                }

                // Calculate basal IOB from temp basal treatments
                if (treatment.EventType == "Temp Basal" && treatment.Duration.HasValue)
                {
                    var basalIob = CalcBasalTreatment(treatment, currentTime, specProfile);
                    totalBasalIob += basalIob.IobContrib;
                    totalActivity += basalIob.ActivityContrib;
                }
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

    /// <summary>
    /// Calculate IOB contribution from a single <see cref="Treatment"/> using the exact legacy
    /// two-phase insulin curve.
    /// </summary>
    public IobContribution CalcTreatment(
        Treatment treatment,
        long? time = null,
        string? specProfile = null
    )
    {
        if (!treatment.Insulin.HasValue || treatment.Insulin.Value <= 0)
        {
            return new IobContribution { IobContrib = 0, ActivityContrib = 0 };
        }

        var currentTime = time ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // Per-treatment insulin context takes priority over profile DIA/peak
        var dia = treatment.InsulinContext?.Dia
            ?? therapySettings.GetDIAAsync(currentTime, specProfile).GetAwaiter().GetResult();
        var peak = treatment.InsulinContext?.Peak
            ?? PEAK_MINUTES;
        var sens = sensitivity.GetSensitivityAsync(currentTime, specProfile).GetAwaiter().GetResult();

        // Exact legacy algorithm constants
        var scaleFactor = SCALE_FACTOR_BASE / dia;

        var bolusTime = treatment.Mills;
        var minAgo = (scaleFactor * (currentTime - bolusTime)) / 1000.0 / 60.0;

        // Before peak (0-75 minutes): curved rise
        if (minAgo < peak)
        {
            var x1 = minAgo / 5.0 + 1.0;
            var iobContrib = treatment.Insulin.Value * (1.0 - 0.001852 * x1 * x1 + 0.001852 * x1);
            var activityContrib =
                sens * treatment.Insulin.Value * (2.0 / dia / 60.0 / peak) * minAgo;

            return new IobContribution
            {
                IobContrib = Math.Max(0.0, iobContrib), // Prevent negative IOB
                ActivityContrib = activityContrib,
            };
        }

        // After peak (75-180 minutes): curved decline
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
                IobContrib = Math.Max(0.0, iobContrib), // Prevent negative IOB
                ActivityContrib = activityContrib,
            };
        }

        // After 180 minutes: no IOB remaining
        return new IobContribution { IobContrib = 0, ActivityContrib = 0 };
    }

    /// <summary>
    /// Calculate basal IOB contribution from a temp basal <see cref="Treatment"/>.
    /// </summary>
    public IobContribution CalcBasalTreatment(
        Treatment treatment,
        long? time = null,
        string? specProfile = null
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

        var currentTime = time ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var dia = therapySettings.GetDIAAsync(currentTime, specProfile).GetAwaiter().GetResult();
        var basalRateValue = basalRate.GetBasalRateAsync(currentTime, specProfile).GetAwaiter().GetResult();

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

    /// <summary>
    /// Calculate basal IOB contribution from a V4 <see cref="TempBasal"/> record.
    /// </summary>
    public IobContribution CalcTempBasalIob(
        TempBasal tempBasal,
        long? time = null,
        string? specProfile = null
    )
    {
        var currentTime = time ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        if (!tempBasal.EndMills.HasValue)
        {
            return new IobContribution { IobContrib = 0, ActivityContrib = 0 };
        }

        var dia = therapySettings.GetDIAAsync(currentTime, specProfile).GetAwaiter().GetResult();

        var scheduledBasalRate = tempBasal.ScheduledRate
            ?? basalRate.GetBasalRateAsync(tempBasal.StartMills, specProfile).GetAwaiter().GetResult();

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

    /// <summary>
    /// Calculate aggregated basal IOB from a list of V4 <see cref="TempBasal"/> records.
    /// </summary>
    public IobResult FromTempBasals(
        List<TempBasal> tempBasals,
        long? time = null,
        string? specProfile = null
    )
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

        foreach (var tempBasal in tempBasals)
        {
            if (tempBasal.StartMills <= currentTime)
            {
                var contribution = CalcTempBasalIob(tempBasal, currentTime, specProfile);
                totalBasalIob += contribution.IobContrib;
                totalActivity += contribution.ActivityContrib;
            }
        }

        return new IobResult
        {
            Iob = 0.0, // Basal IOB does not contribute to bolus IOB
            BasalIob = totalBasalIob > 0 ? RoundToThreeDecimals(totalBasalIob) : null,
            Activity = totalActivity,
            Source = "Care Portal",
        };
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

    private static bool HasLoopIob(DeviceStatus deviceStatus)
    {
        return deviceStatus.Loop?.Iob != null;
    }

    private static bool HasOpenApsIob(DeviceStatus deviceStatus)
    {
        return deviceStatus.OpenAps?.Iob != null;
    }

    private static bool HasPumpIob(DeviceStatus deviceStatus)
    {
        return deviceStatus.Pump?.Iob != null;
    }

    #endregion
}
