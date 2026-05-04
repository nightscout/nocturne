using Microsoft.Extensions.Logging;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.Treatments;

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
    // Constants from legacy implementation - exact values required
    public const long RECENCY_THRESHOLD = 30 * 60 * 1000; // 30 minutes in milliseconds
    private const double LIVER_SENS_RATIO = 8.0; // Legacy: var liverSensRatio = 8;
    private const int DELAY_MINUTES = 20; // Legacy: const delay = 20;

    // Default profile values to use when resolver data is unavailable
    private const double DEFAULT_CARB_ABSORPTION_RATE = 30.0;
    private const double DEFAULT_SENSITIVITY = 95.0;
    private const double DEFAULT_CARB_RATIO = 18.0;

    /// <inheritdoc />
    public async Task<CobResult> CalculateTotalAsync(
        List<CarbIntake> carbIntakes,
        List<Bolus>? boluses = null,
        List<TempBasal>? tempBasals = null,
        long? time = null,
        CancellationToken ct = default
    )
    {
        var currentTime = time ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var hasData = await therapySettingsResolver.HasDataAsync(ct);

        if (hasData)
        {
            try
            {
                var sens = GetSensitivityOrDefault(currentTime);
                var carbRatio = GetCarbRatioOrDefault(currentTime);
                if (sens <= 0 || carbRatio <= 0)
                {
                    logger.LogWarning(
                        "For the COB plugin to function your treatment profile must have both sens and carbratio fields. Using defaults."
                    );
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "For the COB plugin to function your treatment profile must have both sens and carbratio fields. Using defaults."
                );
            }
        }

        // Get COB from APS snapshot (prioritized source)
        var deviceCob = await GetLatestDeviceCobAsync(currentTime, ct);

        // Legacy logic: if device COB exists and is recent (within 10 minutes), use it
        if (deviceCob != null && deviceCob.Cob > 0 && deviceCob.Mills.HasValue)
        {
            var deviceAge =
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - deviceCob.Mills.Value;
            if (deviceAge <= 10 * 60 * 1000)
            {
                return AddDisplay(deviceCob);
            }
        }

        // Fall back to carb-intake-based COB calculation
        var treatmentCOB =
            carbIntakes?.Any() == true
                ? FromCarbIntakes(carbIntakes, boluses, tempBasals, currentTime)
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

    /// <inheritdoc />
    public CobResult FromCarbIntakes(
        List<CarbIntake> carbIntakes,
        List<Bolus>? boluses = null,
        List<TempBasal>? tempBasals = null,
        long? time = null
    )
    {
        var currentTime = time ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var totalCOB = 0.0;
        CarbIntake? lastCarbs = null;
        var isDecaying = 0.0;
        var lastDecayedBy = 0L;

        var sortedIntakes = (carbIntakes ?? new List<CarbIntake>()).OrderBy(c => c.Mills).ToList();

        foreach (var carbIntake in sortedIntakes)
        {
            var carbAbsorptionRateFromProfile = GetCarbAbsorptionRateOrDefault(carbIntake.Mills);

            if (carbIntake.Carbs > 0 && carbIntake.Mills < currentTime)
            {
                lastCarbs = carbIntake;
                var cCalc = CobCalc(carbIntake, lastDecayedBy, currentTime);
                if (cCalc == null)
                    continue;

                var decaysinHr =
                    (cCalc.DecayedBy.ToUnixTimeMilliseconds() - currentTime) / 1000.0 / 60.0 / 60.0;

                if (decaysinHr > -10)
                {
                    var actStartResult = iobCalculator
                        .CalculateTotalAsync(
                            boluses ?? [],
                            tempBasals,
                            lastDecayedBy,
                            ct: default
                        ).GetAwaiter().GetResult();
                    var actStart = actStartResult?.Activity ?? double.NaN;

                    var actEndResult = iobCalculator
                        .CalculateTotalAsync(
                            boluses ?? [],
                            tempBasals,
                            cCalc.DecayedBy.ToUnixTimeMilliseconds(),
                            ct: default
                        ).GetAwaiter().GetResult();
                    var actEnd = actEndResult?.Activity ?? double.NaN;

                    var avgActivity = (actStart + actEnd) / 2.0;

                    var sensFromProfile = GetSensitivityOrDefault(carbIntake.Mills);
                    var carbRatioFromProfile = GetCarbRatioOrDefault(carbIntake.Mills);

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
                        carbIntake.Carbs,
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

        var sens = GetSensitivityOrDefault(currentTime);
        var carbRatio = GetCarbRatioOrDefault(currentTime);
        var carbAbsorptionRate = GetCarbAbsorptionRateOrDefault(currentTime);

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

    /// <inheritdoc />
    public CarbCobContribution CalcCarbIntake(CarbIntake carbIntake, long time)
    {
        var currentTime = time;

        var hasData = therapySettingsResolver.HasDataAsync().GetAwaiter().GetResult();
        if (!hasData)
        {
            logger.LogWarning("For the COB plugin to function you need a treatment profile");
            return new CarbCobContribution();
        }

        try
        {
            var sens = GetSensitivityOrDefault(currentTime);
            var carbRatio = GetCarbRatioOrDefault(currentTime);
            if (sens <= 0 || carbRatio <= 0)
            {
                logger.LogWarning(
                    "For the COB plugin to function your treatment profile must have both sens and carbratio fields"
                );
                return new CarbCobContribution();
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "For the COB plugin to function your treatment profile must have both sens and carbratio fields"
            );
            return new CarbCobContribution();
        }

        var cobContrib = 0.0;
        var activityContrib = 0.0;
        long? decayedBy = null;
        var isDecaying = false;
        if (carbIntake.Carbs > 0 && carbIntake.Mills < currentTime)
        {
            var cCalc = CobCalc(carbIntake, 0, currentTime);
            if (cCalc != null)
            {
                var decayedByTime = cCalc.DecayedBy.ToUnixTimeMilliseconds();
                var decaysinHr = (decayedByTime - currentTime) / 1000.0 / 60.0 / 60.0;
                if (decaysinHr > 0)
                {
                    var carbAbsorptionRate = carbIntake.AbsorptionTime.HasValue
                        ? (carbIntake.Carbs / (carbIntake.AbsorptionTime.Value / 60.0))
                        : GetCarbAbsorptionRateOrDefault(carbIntake.Mills);

                    cobContrib = Math.Min(
                        carbIntake.Carbs,
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
            var carbRatio = GetCarbRatioOrDefault(currentTime);
            if (carbRatio > 0)
            {
                activityContrib = cobContrib / carbRatio;
            }
        }

        return new CarbCobContribution
        {
            CobContrib = cobContrib,
            ActivityContrib = activityContrib,
            DecayedBy = decayedBy,
            IsDecaying = isDecaying,
        };
    }

    #region Private Helpers

    private CobCalcResult? CobCalc(
        CarbIntake carbIntake,
        long lastDecayedBy,
        long time
    )
    {
        if (carbIntake.Carbs <= 0)
        {
            return null;
        }

        const int delay = DELAY_MINUTES;
        var carbTime = DateTimeOffset.FromUnixTimeMilliseconds(carbIntake.Mills);

        var carbsHr = carbIntake.AbsorptionTime.HasValue
            ? (carbIntake.Carbs / (carbIntake.AbsorptionTime.Value / 60.0))
            : GetCarbAbsorptionRateOrDefault(carbIntake.Mills);

        // No ApplyAdvancedAbsorptionAdjustments — absorption rate comes purely from
        // carbIntake.AbsorptionTime or the profile default.

        var carbsMin = carbsHr / 60.0;
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
    /// Query <see cref="IApsSnapshotRepository"/> for the most recent device-reported COB
    /// within the staleness window.
    /// </summary>
    internal async Task<CobResult?> GetLatestDeviceCobAsync(long time, CancellationToken ct = default)
    {
        var futureMills = time + 5 * 60 * 1000; // Allow for clocks to be a little off
        var recentMills = time - RECENCY_THRESHOLD;

        var recentTime = DateTimeOffset.FromUnixTimeMilliseconds(recentMills).UtcDateTime;
        var futureTime = DateTimeOffset.FromUnixTimeMilliseconds(futureMills).UtcDateTime;

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
        if (apsSnapshot?.Cob is > 0)
        {
            var source = apsSnapshot.AidAlgorithm switch
            {
                AidAlgorithm.Loop => "Loop",
                _ => "OpenAPS",
            };

            return new CobResult
            {
                Cob = apsSnapshot.Cob.Value,
                Source = source,
                Device = apsSnapshot.Device,
                Mills = new DateTimeOffset(apsSnapshot.Timestamp, TimeSpan.Zero).ToUnixTimeMilliseconds(),
            };
        }

        return null;
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

    private double GetCarbAbsorptionRateOrDefault(long time)
    {
        try
        {
            var value = therapySettingsResolver.GetCarbAbsorptionRateAsync(time, null).GetAwaiter().GetResult();
            return value > 0 ? value : DEFAULT_CARB_ABSORPTION_RATE;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to resolve carb absorption rate at {Time}; using default.", time);
            return DEFAULT_CARB_ABSORPTION_RATE;
        }
    }

    private double GetSensitivityOrDefault(long time)
    {
        try
        {
            var value = sensitivityResolver.GetSensitivityAsync(time, null).GetAwaiter().GetResult();
            return value > 0 ? value : DEFAULT_SENSITIVITY;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to resolve sensitivity at {Time}; using default.", time);
            return DEFAULT_SENSITIVITY;
        }
    }

    private double GetCarbRatioOrDefault(long time)
    {
        try
        {
            var value = carbRatioResolver.GetCarbRatioAsync(time, null).GetAwaiter().GetResult();
            return value > 0 ? value : DEFAULT_CARB_RATIO;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to resolve carb ratio at {Time}; using default.", time);
            return DEFAULT_CARB_RATIO;
        }
    }

    #endregion
}
