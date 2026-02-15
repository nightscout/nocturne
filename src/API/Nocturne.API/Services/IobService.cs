using Nocturne.Core.Contracts;
using Nocturne.Core.Models;

namespace Nocturne.API.Services;

/// <summary>
/// Implementation of IOB calculations with exact 1:1 legacy JavaScript compatibility
/// </summary>
public class IobService : IIobService
{
    // Constants from legacy implementation
    private const long RECENCY_THRESHOLD = 30 * 60 * 1000; // 30 minutes in milliseconds
    private const double DEFAULT_DIA = 3.0; // Default Duration of Insulin Action in hours
    private const double SCALE_FACTOR_BASE = 3.0; // Base for scale factor calculation
    private const double PEAK_MINUTES = 75.0; // Peak insulin action at 75 minutes
    private const double MAX_IOB_MINUTES = 180.0; // IOB calculation cutoff at 180 minutes

    /// <summary>
    /// Main IOB calculation function that combines device status and treatment data
    /// Exact implementation of legacy calcTotal function
    /// </summary>
    public IobResult CalculateTotal(
        List<Treatment> treatments,
        List<DeviceStatus> deviceStatus,
        IProfileService? profile = null,
        long? time = null,
        string? specProfile = null
    )
    {
        var currentTime = time ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // Get IOB from device status (pumps, OpenAPS, Loop) - prioritized source
        var result = LastIobDeviceStatus(deviceStatus, currentTime);

        // Calculate IOB from treatments (Care Portal entries)
        var treatmentResult =
            treatments?.Any() == true
                ? FromTreatments(treatments, profile, currentTime, specProfile)
                : new IobResult();

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
    /// Get the most recent IOB from device status entries with prioritization
    /// Exact implementation of legacy lastIOBDeviceStatus function
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
    /// Extract IOB from device status entry - exact implementation of legacy fromDeviceStatus
    /// Priority: Loop > OpenAPS > Pump (MM Connect)
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
    /// Calculate IOB from treatments (Care Portal entries) with exact legacy algorithm
    /// Implements exact calculations from ClientApp/lib/plugins/iob.js fromTreatments function
    /// </summary>
    public IobResult FromTreatments(
        List<Treatment> treatments,
        IProfileService? profile = null,
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
                    var contribution = CalcTreatment(treatment, profile, currentTime, specProfile);

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
                    var basalIob = CalcBasalTreatment(treatment, profile, currentTime, specProfile);
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
    /// Calculate IOB contribution from a single treatment - exact legacy algorithm
    /// Implements exact curve from ClientApp/lib/plugins/iob.js calcTreatment
    /// </summary>
    public IobContribution CalcTreatment(
        Treatment treatment,
        IProfileService? profile = null,
        long? time = null,
        string? specProfile = null
    )
    {
        if (!treatment.Insulin.HasValue || treatment.Insulin.Value <= 0)
        {
            return new IobContribution { IobContrib = 0, ActivityContrib = 0 };
        }

        var currentTime = time ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var dia = profile?.GetDIA(currentTime, specProfile) ?? DEFAULT_DIA;
        var sens = profile?.GetSensitivity(currentTime, specProfile) ?? 50.0;

        // Exact legacy algorithm constants
        var scaleFactor = SCALE_FACTOR_BASE / dia;
        var peak = PEAK_MINUTES;

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
            var x2 = (minAgo - 75.0) / 5.0;
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
    /// Calculate basal IOB contribution from temp basal treatment
    /// Uses simplified algorithm based on insulin delivery and decay
    /// </summary>
    public IobContribution CalcBasalTreatment(
        Treatment treatment,
        IProfileService? profile = null,
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
        var dia = profile?.GetDIA(currentTime, specProfile) ?? DEFAULT_DIA;
        var basalRate = profile?.GetBasalRate(currentTime, specProfile) ?? 1.0;

        var treatmentStart = treatment.Mills;
        var treatmentEnd = treatmentStart + (treatment.Duration.Value * 60 * 1000); // Duration in minutes to milliseconds

        // Only calculate if current time is after treatment start
        if (currentTime <= treatmentStart)
        {
            return new IobContribution { IobContrib = 0, ActivityContrib = 0 };
        }

        // Calculate effective insulin delivered so far
        var effectiveEnd = Math.Min(currentTime, treatmentEnd);
        var durationActual = (effectiveEnd - treatmentStart) / 1000.0 / 60.0; // minutes
        var tempRate = treatment.Absolute.Value;
        var excessInsulin = Math.Max(0, (tempRate - basalRate) * (durationActual / 60.0)); // excess insulin in units

        if (excessInsulin <= 0)
        {
            return new IobContribution { IobContrib = 0, ActivityContrib = 0 };
        }

        // Use simplified decay similar to bolus IOB but with different parameters for basal
        var minAgo = (currentTime - treatmentStart) / 1000.0 / 60.0;
        var diaMinutes = dia * 60.0;

        // Simple linear decay over DIA period
        if (minAgo < diaMinutes)
        {
            var decayFactor = Math.Max(0, 1.0 - (minAgo / diaMinutes));
            var basalIob = excessInsulin * decayFactor;

            return new IobContribution
            {
                IobContrib = RoundToThreeDecimals(basalIob),
                ActivityContrib = 0, // Simplified - no activity calculation for basal
            };
        }

        return new IobContribution { IobContrib = 0, ActivityContrib = 0 };
    }

    #region Helper Methods

    /// <summary>
    /// Add display formatting to IOB result - exact legacy implementation
    /// </summary>
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

    /// <summary>
    /// Check if IOB result is empty
    /// </summary>
    private static bool IsEmpty(IobResult? iob)
    {
        return iob == null || (iob.Iob <= 0 && !iob.BasalIob.HasValue && !iob.Activity.HasValue);
    }

    /// <summary>
    /// Round to three decimal places with exact legacy precision
    /// </summary>
    private static double RoundToThreeDecimals(double num)
    {
        return Math.Round(num + double.Epsilon, 3);
    }

    /// <summary>
    /// Type guard for Loop IOB data
    /// </summary>
    private static bool HasLoopIob(DeviceStatus deviceStatus)
    {
        return deviceStatus.Loop?.Iob != null;
    }

    /// <summary>
    /// Type guard for OpenAPS IOB data
    /// </summary>
    private static bool HasOpenApsIob(DeviceStatus deviceStatus)
    {
        return deviceStatus.OpenAps?.Iob != null;
    }

    /// <summary>
    /// Type guard for Pump IOB data
    /// </summary>
    private static bool HasPumpIob(DeviceStatus deviceStatus)
    {
        return deviceStatus.Pump?.Iob != null;
    }

    #endregion
}
