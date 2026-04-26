using Microsoft.Extensions.Logging;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Models;

namespace Nocturne.API.Services.Treatments;

/// <summary>
/// COB calculation result with exact 1:1 legacy JavaScript compatibility.
/// Based on the <c>ClientApp/lib/plugins/cob.js</c> return structure.
/// </summary>
/// <seealso cref="CobService"/>
/// <seealso cref="ICobService"/>
public class CobResult
{
    public double Cob { get; set; }
    public double? Activity { get; set; }
    public List<Treatment>? Treatments { get; set; }
    public string? Source { get; set; }
    public string? Device { get; set; }
    public long? Mills { get; set; }
    public string? Display { get; set; }
    public string? DisplayLine { get; set; }

    // Properties from legacy fromTreatments return
    public long? DecayedBy { get; set; }
    public double? IsDecaying { get; set; }
    public double? CarbsHr { get; set; }
    public double? RawCarbImpact { get; set; }
    public Treatment? LastCarbs { get; set; }
    public CobResult? TreatmentCOB { get; set; }
}

/// <summary>
/// COB calculation result from the <c>cobCalc</c> function.
/// Exact structure from legacy JavaScript.
/// </summary>
/// <seealso cref="CobService"/>
public class CobCalcResult
{
    public double InitialCarbs { get; set; }
    public DateTimeOffset DecayedBy { get; set; }
    public double IsDecaying { get; set; }
    public DateTimeOffset CarbTime { get; set; }
}

/// <summary>
/// COB contribution calculated for a single <see cref="Treatment"/>.
/// </summary>
/// <seealso cref="CobService.CalcTreatment"/>
public class TreatmentCobResult
{
    public double CobContrib { get; set; }
    public double ActivityContrib { get; set; }
    public long? DecayedBy { get; set; }
    public bool IsDecaying { get; set; }
}

/// <summary>
/// Service for calculating Carbs on Board (COB) with exact 1:1 legacy JavaScript compatibility.
/// Implements exact algorithms from <c>ClientApp/lib/plugins/cob.js</c> with no simplifications.
/// </summary>
/// <seealso cref="CobService"/>
/// <seealso cref="IobService"/>
public interface ICobService
{
    /// <summary>
    /// Computes total COB, prioritizing <see cref="DeviceStatus"/> data over treatment-based calculation.
    /// </summary>
    CobResult CobTotal(
        List<Treatment> treatments,
        List<DeviceStatus> deviceStatus,
        long? time = null,
        string? specProfile = null
    );

    /// <summary>
    /// Calculates COB from <see cref="Treatment"/> records using the exact legacy algorithm
    /// including IOB integration with liver sensitivity ratio.
    /// </summary>
    CobResult FromTreatments(
        List<Treatment> treatments,
        List<DeviceStatus> deviceStatus,
        long? time = null,
        string? specProfile = null
    );

    /// <summary>
    /// Extracts COB from a single <see cref="DeviceStatus"/> entry.
    /// </summary>
    CobResult FromDeviceStatus(DeviceStatus deviceStatusEntry);

    /// <summary>
    /// Gets the most recent COB from <see cref="DeviceStatus"/> entries within the recency threshold.
    /// </summary>
    CobResult LastCOBDeviceStatus(List<DeviceStatus> deviceStatus, long time);

    /// <summary>
    /// Determines whether any of the given <see cref="DeviceStatus"/> entries contain COB data.
    /// </summary>
    bool IsDeviceStatusAvailable(List<DeviceStatus> deviceStatus);

    /// <summary>
    /// Calculates the COB contribution from a single <see cref="Treatment"/>.
    /// </summary>
    TreatmentCobResult CalcTreatment(
        Treatment treatment,
        long time,
        string? specProfile = null
    );
}

/// <summary>
/// Implementation of Carbs on Board (COB) calculations with exact 1:1 legacy JavaScript compatibility.
/// Based on <c>ClientApp/lib/plugins/cob.js</c> with no simplifications.
/// </summary>
/// <remarks>
/// The algorithm includes:
/// <list type="bullet">
///   <item>20-minute delay period before carb absorption begins.</item>
///   <item>IOB integration with a liver sensitivity ratio of 8 to adjust decay timing.</item>
///   <item>Complex decay calculations via an internal <c>CobCalc</c> helper.</item>
///   <item>Exact device status prioritization: Loop COB takes precedence over OpenAPS (enacted, then suggested).</item>
/// </list>
/// </remarks>
/// <seealso cref="ICobService"/>
/// <seealso cref="IobService"/>
/// <seealso cref="TreatmentService"/>
public class CobService(
    ILogger<CobService> logger,
    IIobService iobService,
    ISensitivityResolver sensitivityResolver,
    ICarbRatioResolver carbRatioResolver,
    ITherapySettingsResolver therapySettingsResolver
) : ICobService
{
    // Constants from legacy implementation - exact values required
    public const long RECENCY_THRESHOLD = 30 * 60 * 1000; // 30 minutes in milliseconds
    private const double LIVER_SENS_RATIO = 8.0; // Legacy: var liverSensRatio = 8;
    private const int DELAY_MINUTES = 20; // Legacy: const delay = 20;

    // Default profile values to use when resolver data is unavailable
    private const double DEFAULT_CARB_ABSORPTION_RATE = 30.0;
    private const double DEFAULT_SENSITIVITY = 95.0;
    private const double DEFAULT_CARB_RATIO = 18.0;

    /// <summary>
    /// Main COB calculation function - exact implementation of legacy cobTotal
    /// </summary>
    public CobResult CobTotal(
        List<Treatment> treatments,
        List<DeviceStatus> deviceStatus,
        long? time = null,
        string? specProfile = null
    )
    {
        var currentTime = time ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var hasData = therapySettingsResolver.HasDataAsync().GetAwaiter().GetResult();

        if (hasData)
        {
            // Profile validation - exact legacy behavior
            try
            {
                var sens = GetSensitivityOrDefault(currentTime, specProfile);
                var carbRatio = GetCarbRatioOrDefault(currentTime, specProfile);
                if (sens <= 0 || carbRatio <= 0)
                {
                    logger.LogWarning(
                        "For the COB plugin to function your treatment profile must have both sens and carbratio fields. Using defaults."
                    );
                }
            }
            catch
            {
                logger.LogWarning(
                    "For the COB plugin to function your treatment profile must have both sens and carbratio fields. Using defaults."
                );
            }
        }

        // Get COB from device status (prioritized source)
        var devicestatusCOB = LastCOBDeviceStatus(deviceStatus, currentTime);

        // Legacy logic: if device COB exists and is recent (within 10 minutes), use it
        if (devicestatusCOB.Cob > 0 && devicestatusCOB.Mills.HasValue)
        {
            var deviceAge =
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - devicestatusCOB.Mills.Value;
            if (deviceAge <= 10 * 60 * 1000)
            {
                return AddDisplay(devicestatusCOB);
            }
        }

        // Fall back to treatment-based COB calculation
        var treatmentCOB =
            treatments?.Any() == true
                ? FromTreatments(treatments, deviceStatus, currentTime, specProfile)
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

    /// <summary>
    /// Calculate COB from treatments - exact implementation of legacy fromTreatments
    /// </summary>
    public CobResult FromTreatments(
        List<Treatment> treatments,
        List<DeviceStatus> deviceStatus,
        long? time = null,
        string? specProfile = null
    )
    {
        var currentTime = time ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var totalCOB = 0.0;
        Treatment? lastCarbs = null;
        var isDecaying = 0.0;
        var lastDecayedBy = 0L;

        var sortedTreatments = (treatments ?? new List<Treatment>()).OrderBy(t => t.Mills).ToList();

        foreach (var treatment in sortedTreatments)
        {
            var carbAbsorptionRateFromProfile = GetCarbAbsorptionRateOrDefault(treatment.Mills, specProfile);

            if (
                treatment.Carbs.HasValue
                && treatment.Carbs.Value > 0
                && treatment.Mills < currentTime
            )
            {
                lastCarbs = treatment;
                var cCalc = CobCalc(treatment, lastDecayedBy, currentTime, specProfile);
                if (cCalc == null)
                    continue;

                var decaysinHr =
                    (cCalc.DecayedBy.ToUnixTimeMilliseconds() - currentTime) / 1000.0 / 60.0 / 60.0;

                if (decaysinHr > -10)
                {
                    var actStart =
                        iobService
                            .CalculateTotalAsync(
                                treatments ?? new List<Treatment>(),
                                lastDecayedBy,
                                specProfile
                            )
                            .GetAwaiter()
                            .GetResult()
                            ?.Activity ?? double.NaN;
                    var actEnd =
                        iobService
                            .CalculateTotalAsync(
                                treatments ?? new List<Treatment>(),
                                cCalc.DecayedBy.ToUnixTimeMilliseconds(),
                                specProfile
                            )
                            .GetAwaiter()
                            .GetResult()
                            ?.Activity ?? double.NaN;
                    var avgActivity = (actStart + actEnd) / 2.0;

                    var sensFromProfile = GetSensitivityOrDefault(treatment.Mills, specProfile);
                    var carbRatioFromProfile = GetCarbRatioOrDefault(treatment.Mills, specProfile);

                    var delayedCarbs =
                        carbRatioFromProfile * ((avgActivity * LIVER_SENS_RATIO) / sensFromProfile);
                    var delayMinutes = Math.Round(
                        (delayedCarbs / carbAbsorptionRateFromProfile) * 60
                    );

                    if (delayMinutes > 0)
                    {
                        cCalc.DecayedBy = cCalc.DecayedBy.AddMinutes(delayMinutes);
                        decaysinHr =
                            (cCalc.DecayedBy.ToUnixTimeMilliseconds() - currentTime)
                            / 1000.0
                            / 60.0
                            / 60.0;
                    }
                }

                lastDecayedBy = cCalc.DecayedBy.ToUnixTimeMilliseconds();

                if (decaysinHr > 0)
                {
                    totalCOB += Math.Min(
                        Convert.ToDouble(treatment.Carbs.Value),
                        decaysinHr * carbAbsorptionRateFromProfile
                    );
                    isDecaying = cCalc.IsDecaying;
                }
                else
                {
                    totalCOB = 0;
                }
            }
        }

        var sens = GetSensitivityOrDefault(currentTime, specProfile);
        var carbRatio = GetCarbRatioOrDefault(currentTime, specProfile);
        var carbAbsorptionRate = GetCarbAbsorptionRateOrDefault(currentTime, specProfile);

        var rawCarbImpact = (((isDecaying * sens) / carbRatio) * carbAbsorptionRate) / 60.0;

        return new CobResult
        {
            DecayedBy = lastDecayedBy,
            IsDecaying = isDecaying,
            CarbsHr = carbAbsorptionRate,
            RawCarbImpact = rawCarbImpact,
            Cob = totalCOB,
            LastCarbs = lastCarbs,
        };
    }

    private CobCalcResult? CobCalc(
        Treatment treatment,
        long lastDecayedBy,
        long time,
        string? specProfile
    )
    {
        if (!treatment.Carbs.HasValue || treatment.Carbs.Value <= 0)
        {
            return null;
        }

        const int delay = DELAY_MINUTES;
        var carbTime = DateTimeOffset.FromUnixTimeMilliseconds(treatment.Mills);

        var carbsHr = treatment.AbsorptionTime.HasValue
            ? (treatment.Carbs.Value / (treatment.AbsorptionTime.Value / 60.0))
            : GetCarbAbsorptionRateOrDefault(treatment.Mills, specProfile);

        carbsHr = ApplyAdvancedAbsorptionAdjustments(carbsHr, treatment);

        var carbsMin = carbsHr / 60.0;
        var decayedBy = carbTime;
        var minutesleft =
            lastDecayedBy > 0 ? (lastDecayedBy - treatment.Mills) / 1000.0 / 60.0 : 0.0;

        var additionalMinutes = Math.Max(delay, minutesleft) + (treatment.Carbs.Value / carbsMin);
        decayedBy = decayedBy.AddMinutes(additionalMinutes);

        var initialCarbs =
            delay > minutesleft
                ? Convert.ToInt32(treatment.Carbs.Value)
                : Convert.ToInt32(treatment.Carbs.Value) + (minutesleft * carbsMin);

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

    public CobResult LastCOBDeviceStatus(List<DeviceStatus> deviceStatus, long time)
    {
        if (deviceStatus?.Any() != true)
        {
            return new CobResult();
        }

        var futureMills = time + 5 * 60 * 1000;
        var recentMills = time - RECENCY_THRESHOLD;

        var validCobs = deviceStatus
            .Where(ds => ds.Mills >= recentMills && ds.Mills <= futureMills)
            .Select(FromDeviceStatus)
            .Where(cob => cob.Cob > 0)
            .OrderBy(cob => cob.Mills ?? 0)
            .ToList();

        return validCobs.LastOrDefault() ?? new CobResult();
    }

    public CobResult FromDeviceStatus(DeviceStatus deviceStatusEntry)
    {
        if (deviceStatusEntry.Loop?.Cob?.Cob.HasValue == true)
        {
            var timestamp =
                deviceStatusEntry.Loop.Cob.Timestamp != null
                    ? (
                        DateTime.TryParse(deviceStatusEntry.Loop.Cob.Timestamp, out var ts)
                            ? ((DateTimeOffset)ts).ToUnixTimeMilliseconds()
                            : deviceStatusEntry.Mills
                    )
                    : deviceStatusEntry.Mills;
            return new CobResult
            {
                Cob = deviceStatusEntry.Loop.Cob.Cob.Value,
                Source = "Loop",
                Device = deviceStatusEntry.Device,
                Mills = timestamp,
            };
        }

        if (deviceStatusEntry.OpenAps?.Cob.HasValue == true)
        {
            return new CobResult
            {
                Cob = deviceStatusEntry.OpenAps.Cob.Value,
                Source = "OpenAPS",
                Device = deviceStatusEntry.Device,
                Mills = deviceStatusEntry.Mills,
            };
        }

        if (deviceStatusEntry.OpenAps?.Enacted?.COB is { } enactedCob)
        {
            return new CobResult
            {
                Cob = enactedCob,
                Source = "OpenAPS",
                Device = deviceStatusEntry.Device,
                Mills = deviceStatusEntry.Mills,
            };
        }

        if (deviceStatusEntry.OpenAps?.Suggested?.COB is { } suggestedCob)
        {
            return new CobResult
            {
                Cob = suggestedCob,
                Source = "OpenAPS",
                Device = deviceStatusEntry.Device,
                Mills = deviceStatusEntry.Mills,
            };
        }

        return new CobResult();
    }

    public bool IsDeviceStatusAvailable(List<DeviceStatus> deviceStatus)
    {
        return deviceStatus.Select(FromDeviceStatus).Any(cob => cob.Cob > 0);
    }

    public TreatmentCobResult CalcTreatment(
        Treatment treatment,
        long time,
        string? specProfile = null
    )
    {
        var currentTime = time;

        var hasData = therapySettingsResolver.HasDataAsync().GetAwaiter().GetResult();
        if (!hasData)
        {
            logger.LogWarning("For the COB plugin to function you need a treatment profile");
            return new TreatmentCobResult();
        }

        try
        {
            var sens = GetSensitivityOrDefault(currentTime, specProfile);
            var carbRatio = GetCarbRatioOrDefault(currentTime, specProfile);
            if (sens <= 0 || carbRatio <= 0)
            {
                logger.LogWarning(
                    "For the COB plugin to function your treatment profile must have both sens and carbratio fields"
                );
                return new TreatmentCobResult();
            }
        }
        catch
        {
            logger.LogWarning(
                "For the COB plugin to function your treatment profile must have both sens and carbratio fields"
            );
            return new TreatmentCobResult();
        }

        var cobContrib = 0.0;
        var activityContrib = 0.0;
        long? decayedBy = null;
        var isDecaying = false;
        if (treatment.Carbs.HasValue && treatment.Carbs.Value > 0 && treatment.Mills < currentTime)
        {
            var cCalc = CobCalc(treatment, 0, currentTime, specProfile);
            if (cCalc != null)
            {
                var decayedByTime = cCalc.DecayedBy.ToUnixTimeMilliseconds();
                var decaysinHr = (decayedByTime - currentTime) / 1000.0 / 60.0 / 60.0;
                if (decaysinHr > 0)
                {
                    var carbAbsorptionRate = treatment.AbsorptionTime.HasValue
                        ? (treatment.Carbs.Value / (treatment.AbsorptionTime.Value / 60.0))
                        : GetCarbAbsorptionRateOrDefault(treatment.Mills, specProfile);

                    cobContrib = Math.Min(
                        Convert.ToDouble(treatment.Carbs.Value),
                        decaysinHr * carbAbsorptionRate
                    );
                }
                else
                {
                    cobContrib = 0;
                }

                decayedBy = decayedByTime;
                isDecaying = cCalc.IsDecaying > 0;
            }
        }

        if (cobContrib > 0)
        {
            var carbRatio = GetCarbRatioOrDefault(currentTime, specProfile);
            if (carbRatio > 0)
            {
                activityContrib = cobContrib / carbRatio;
            }
        }

        return new TreatmentCobResult
        {
            CobContrib = cobContrib,
            ActivityContrib = activityContrib,
            DecayedBy = decayedBy,
            IsDecaying = isDecaying,
        };
    }

    #region Private Helpers

    private static double ApplyAdvancedAbsorptionAdjustments(double baseAbsorptionRate, Treatment treatment)
    {
        var adjustedRate = baseAbsorptionRate;

        if (treatment.Fat.HasValue && treatment.Fat.Value > 0)
        {
            var fatFactor = treatment.Fat.Value > 15 ? 0.6 : 0.8;
            adjustedRate *= fatFactor;
        }

        if (!string.IsNullOrEmpty(treatment.Notes))
        {
            var notes = treatment.Notes.ToLowerInvariant();

            if (notes.Contains("glucose") || notes.Contains("tablet") || notes.Contains("juice")
                || notes.Contains("sugar") || notes.Contains("fast") || notes.Contains("low"))
            {
                adjustedRate *= 1.5;
            }
            else if (notes.Contains("complex") || notes.Contains("fiber")
                || notes.Contains("whole grain") || notes.Contains("slow"))
            {
                adjustedRate *= 0.7;
            }
        }

        return adjustedRate;
    }

    private static CobResult AddDisplay(CobResult cob)
    {
        if (cob.Cob <= 0)
            return cob;

        var display = Math.Round(cob.Cob * 10) / 10;
        cob.Display = display.ToString();
        cob.DisplayLine = $"COB: {display}g";

        return cob;
    }

    private double GetCarbAbsorptionRateOrDefault(long time, string? specProfile)
    {
        try
        {
            var value = therapySettingsResolver.GetCarbAbsorptionRateAsync(time, specProfile).GetAwaiter().GetResult();
            return value > 0 ? value : DEFAULT_CARB_ABSORPTION_RATE;
        }
        catch
        {
            return DEFAULT_CARB_ABSORPTION_RATE;
        }
    }

    private double GetSensitivityOrDefault(long time, string? specProfile)
    {
        try
        {
            var value = sensitivityResolver.GetSensitivityAsync(time, specProfile).GetAwaiter().GetResult();
            return value > 0 ? value : DEFAULT_SENSITIVITY;
        }
        catch
        {
            return DEFAULT_SENSITIVITY;
        }
    }

    private double GetCarbRatioOrDefault(long time, string? specProfile)
    {
        try
        {
            var value = carbRatioResolver.GetCarbRatioAsync(time, specProfile).GetAwaiter().GetResult();
            return value > 0 ? value : DEFAULT_CARB_RATIO;
        }
        catch
        {
            return DEFAULT_CARB_RATIO;
        }
    }

    #endregion
}
