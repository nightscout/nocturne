using Microsoft.Extensions.Logging;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

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
/// Service for calculating Carbs on Board (COB) with exact 1:1 legacy JavaScript compatibility.
/// Implements exact algorithms from <c>ClientApp/lib/plugins/cob.js</c> with no simplifications.
/// </summary>
/// <seealso cref="CobService"/>
/// <seealso cref="IobService"/>
public interface ICobService
{
    /// <summary>
    /// Computes total COB, prioritizing <see cref="ApsSnapshot"/> data over treatment-based calculation.
    /// Queries <see cref="IApsSnapshotRepository"/> internally for device-reported COB.
    /// </summary>
    Task<CobResult> CobTotalAsync(
        List<Treatment> treatments,
        long? time = null,
        string? specProfile = null,
        CancellationToken ct = default
    );

    /// <summary>
    /// Calculates COB from <see cref="Treatment"/> records using the exact legacy algorithm
    /// including IOB integration with liver sensitivity ratio.
    /// </summary>
    Task<CobResult> FromTreatmentsAsync(
        List<Treatment> treatments,
        long? time = null,
        string? specProfile = null,
        CancellationToken ct = default
    );

    /// <summary>
    /// Sync <c>CobTotal</c> driven by a request-scoped <see cref="TherapySnapshot"/> and a
    /// pre-fetched <see cref="DeviceCobSnapshot"/>. Eliminates the per-tick async chain
    /// (APS lookup, resolver awaits) that the legacy <see cref="CobTotalAsync"/> incurs.
    /// </summary>
    /// <param name="treatments">Carb-bearing <see cref="Treatment"/> records contributing to COB.</param>
    /// <param name="currentTime">Calculation time in Unix milliseconds.</param>
    /// <param name="snapshot">Frozen therapy state covering <paramref name="currentTime"/>.</param>
    /// <param name="deviceCob">Most recent device-reported COB observation, or <c>null</c> when none was found.</param>
    /// <param name="nowMills">Wall-clock time used for the device-COB freshness check (kept as a parameter for testability).</param>
    CobResult CobTotal(
        IReadOnlyList<Treatment> treatments,
        long currentTime,
        TherapySnapshot snapshot,
        DeviceCobSnapshot? deviceCob,
        long nowMills
    );

    /// <summary>
    /// Returns the most recent APS-reported COB observation in <c>[queryAtMills - 30min, queryAtMills + 5min]</c>,
    /// mapped to a <see cref="DeviceCobSnapshot"/>. Returns <c>null</c> when no fresh snapshot has Cob &gt; 0.
    /// </summary>
    Task<DeviceCobSnapshot?> GetDeviceCobAsync(long queryAtMills, CancellationToken ct = default);
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
///   <item>APS snapshot prioritization: recent COB from Loop/OpenAPS/AAPS takes precedence over treatment-based calculation.</item>
/// </list>
/// </remarks>
/// <seealso cref="ICobService"/>
/// <seealso cref="IobService"/>
/// <seealso cref="TreatmentService"/>
public class CobService(
    ILogger<CobService> logger,
    IIobService iobService,
    IApsSnapshotRepository apsSnapshotRepo,
    ITherapyTimelineResolver therapyTimelineResolver
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
    /// One-shot COB orchestrator: resolves a request-scoped <see cref="TherapySnapshot"/>,
    /// pre-fetches the latest <see cref="DeviceCobSnapshot"/>, and delegates to the
    /// sync <see cref="CobTotal"/> overload.
    /// </summary>
    public async Task<CobResult> CobTotalAsync(
        List<Treatment> treatments,
        long? time = null,
        string? specProfile = null,
        CancellationToken ct = default
    )
    {
        var currentTime = time ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var snapshot = await therapyTimelineResolver.GetSnapshotAtAsync(currentTime, specProfile, ct);
        var nowMills = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var deviceCob = await GetDeviceCobAsync(currentTime, ct);
        return CobTotal(treatments ?? new List<Treatment>(), currentTime, snapshot, deviceCob, nowMills);
    }

    /// <inheritdoc />
    public async Task<DeviceCobSnapshot?> GetDeviceCobAsync(long queryAtMills, CancellationToken ct = default)
    {
        var futureMills = queryAtMills + 5 * 60 * 1000;
        var recentMills = queryAtMills - RECENCY_THRESHOLD;
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
            return new DeviceCobSnapshot(
                Cob: apsSnapshot.Cob.Value,
                Mills: new DateTimeOffset(apsSnapshot.Timestamp, TimeSpan.Zero).ToUnixTimeMilliseconds(),
                Source: source,
                Device: apsSnapshot.Device
            );
        }

        return null;
    }

    /// <summary>
    /// Treatment-based COB calculation orchestrator. Resolves the request-scoped therapy
    /// snapshot once and delegates to the sync <c>FromTreatments</c> path.
    /// </summary>
    public async Task<CobResult> FromTreatmentsAsync(
        List<Treatment> treatments,
        long? time = null,
        string? specProfile = null,
        CancellationToken ct = default
    )
    {
        var currentTime = time ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var snapshot = await therapyTimelineResolver.GetSnapshotAtAsync(currentTime, specProfile, ct);
        return FromTreatments(treatments ?? new List<Treatment>(), currentTime, snapshot);
    }

    /// <inheritdoc />
    public CobResult CobTotal(
        IReadOnlyList<Treatment> treatments,
        long currentTime,
        TherapySnapshot snapshot,
        DeviceCobSnapshot? deviceCob,
        long nowMills
    )
    {
        var sens = snapshot.SensitivityAt(currentTime);
        var carbRatio = snapshot.CarbRatioAt(currentTime);
        if (sens <= 0 || carbRatio <= 0)
        {
            logger.LogWarning(
                "For the COB plugin to function your treatment profile must have both sens and carbratio fields. Using defaults."
            );
        }

        if (deviceCob is { Cob: > 0 } d && nowMills - d.Mills <= 10 * 60 * 1000)
        {
            return AddDisplay(new CobResult
            {
                Cob = d.Cob,
                Source = d.Source,
                Device = d.Device,
                Mills = d.Mills,
            });
        }

        var treatmentCob = treatments.Count > 0
            ? FromTreatments(treatments, currentTime, snapshot)
            : new CobResult();

        var result = new CobResult
        {
            Cob = treatmentCob.Cob,
            Activity = treatmentCob.Activity,
            DecayedBy = treatmentCob.DecayedBy,
            IsDecaying = treatmentCob.IsDecaying,
            CarbsHr = treatmentCob.CarbsHr,
            RawCarbImpact = treatmentCob.RawCarbImpact,
            LastCarbs = treatmentCob.LastCarbs,
            Source = "Care Portal",
            TreatmentCOB = treatmentCob,
        };

        return AddDisplay(result);
    }

    private CobResult FromTreatments(
        IReadOnlyList<Treatment> treatments,
        long currentTime,
        TherapySnapshot snapshot
    )
    {
        var totalCOB = 0.0;
        Treatment? lastCarbs = null;
        var isDecaying = 0.0;
        var lastDecayedBy = 0L;

        var sortedTreatments = treatments.OrderBy(t => t.Mills).ToList();
        var carbsPerHour = snapshot.CarbsPerHour > 0 ? snapshot.CarbsPerHour : DEFAULT_CARB_ABSORPTION_RATE;

        foreach (var treatment in sortedTreatments)
        {
            if (
                !treatment.Carbs.HasValue
                || treatment.Carbs.Value <= 0
                || treatment.Mills >= currentTime
            )
            {
                continue;
            }

            lastCarbs = treatment;
            var cCalc = CobCalc(treatment, lastDecayedBy, currentTime, snapshot);
            if (cCalc == null)
                continue;

            var decaysinHr =
                (cCalc.DecayedBy.ToUnixTimeMilliseconds() - currentTime) / 1000.0 / 60.0 / 60.0;

            if (decaysinHr > -10)
            {
                var actStart = iobService.FromTreatments(sortedTreatments, lastDecayedBy, snapshot).Activity ?? double.NaN;
                var actEnd = iobService.FromTreatments(sortedTreatments, cCalc.DecayedBy.ToUnixTimeMilliseconds(), snapshot).Activity ?? double.NaN;

                var avgActivity = (actStart + actEnd) / 2.0;

                var sensFromSnapshot = snapshot.SensitivityAt(treatment.Mills);
                if (sensFromSnapshot <= 0)
                    sensFromSnapshot = DEFAULT_SENSITIVITY;
                var carbRatioFromSnapshot = snapshot.CarbRatioAt(treatment.Mills);
                if (carbRatioFromSnapshot <= 0)
                    carbRatioFromSnapshot = DEFAULT_CARB_RATIO;

                var delayedCarbs = carbRatioFromSnapshot * ((avgActivity * LIVER_SENS_RATIO) / sensFromSnapshot);
                var delayMinutes = Math.Round((delayedCarbs / carbsPerHour) * 60);

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
                    decaysinHr * carbsPerHour
                );
                isDecaying = cCalc.IsDecaying;
            }
            else
            {
                totalCOB = 0;
            }
        }

        var sensNow = snapshot.SensitivityAt(currentTime);
        if (sensNow <= 0)
            sensNow = DEFAULT_SENSITIVITY;
        var carbRatioNow = snapshot.CarbRatioAt(currentTime);
        if (carbRatioNow <= 0)
            carbRatioNow = DEFAULT_CARB_RATIO;

        var rawCarbImpact = (((isDecaying * sensNow) / carbRatioNow) * carbsPerHour) / 60.0;

        return new CobResult
        {
            DecayedBy = lastDecayedBy,
            IsDecaying = isDecaying,
            CarbsHr = carbsPerHour,
            RawCarbImpact = rawCarbImpact,
            Cob = totalCOB,
            LastCarbs = lastCarbs,
        };
    }

    private CobCalcResult? CobCalc(
        Treatment treatment,
        long lastDecayedBy,
        long time,
        TherapySnapshot snapshot
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
            : (snapshot.CarbsPerHour > 0 ? snapshot.CarbsPerHour : DEFAULT_CARB_ABSORPTION_RATE);

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

    #endregion
}
