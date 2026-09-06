using Nocturne.Core.Contracts.Devices;
using Nocturne.Core.Models;

namespace Nocturne.Services.Demo.Services;

/// <summary>
/// Simulates physiological glucose dynamics using oref-style insulin curves and absorption models.
/// This provides more realistic glucose predictions than simple linear models.
/// </summary>
public class OrefPhysiologySimulator
{
    private readonly ILogger<OrefPhysiologySimulator> _logger;
    private readonly OrefProfile _profile;

    // Track active insulin and carbs
    private readonly List<InsulinDose> _activeDoses = new();
    private readonly List<CarbDose> _activeCarbs = new();

    public OrefPhysiologySimulator(
        ILogger<OrefPhysiologySimulator> logger,
        OrefProfile? profile = null
    )
    {
        _logger = logger;
        _profile = profile ?? CreateDefaultProfile();
    }

    /// <summary>
    /// Creates a default T1D profile with typical settings
    /// </summary>
    private static OrefProfile CreateDefaultProfile() =>
        new()
        {
            Dia = 5.0, // 5 hours duration of insulin action
            CurrentBasal = 0.9, // 0.9 U/hr typical basal
            MaxIob = 8.0,
            MaxBasal = 4.0,
            MinBg = 80,
            MaxBg = 120,
            Sens = 45, // 45 mg/dL per unit
            CarbRatio = 10, // 10g carbs per unit
            Curve = "rapid-acting",
            Peak = 75, // 75 minutes peak
            Min5mCarbimpact = 8, // minimum 8 mg/dL per 5 min carb impact
            MaxCob = 120,
            AutosensMin = 0.7,
            AutosensMax = 1.2,
        };

    /// <summary>
    /// Records an insulin dose (bolus or temp basal)
    /// </summary>
    public void AddInsulinDose(
        DateTime time,
        double units,
        bool isTempBasal = false,
        double? duration = null
    )
    {
        _activeDoses.Add(
            new InsulinDose
            {
                Time = time,
                Units = units,
                IsTempBasal = isTempBasal,
                Duration = duration ?? 0,
            }
        );
    }

    /// <summary>
    /// Records a carb intake
    /// </summary>
    public void AddCarbs(DateTime time, double carbs, double absorptionTime = 3.0)
    {
        _activeCarbs.Add(
            new CarbDose
            {
                Time = time,
                Carbs = carbs,
                AbsorptionTimeHours = absorptionTime,
            }
        );
    }

    /// <summary>
    /// Calculate current IOB using oref exponential insulin model
    /// </summary>
    public double CalculateIob(DateTime time)
    {
        var diaMinutes = _profile.Dia * 60;
        var peakMinutes = _profile.Peak;
        double totalIob = 0;

        foreach (var dose in _activeDoses.ToList())
        {
            var minutesSince = (time - dose.Time).TotalMinutes;

            if (minutesSince < 0 || minutesSince > diaMinutes)
            {
                if (minutesSince > diaMinutes)
                    _activeDoses.Remove(dose); // Clean up expired doses
                continue;
            }

            // oref exponential model
            var iobRemaining = CalculateExponentialIob(minutesSince, peakMinutes, diaMinutes);
            totalIob += dose.Units * iobRemaining;
        }

        return totalIob;
    }

    /// <summary>
    /// Calculate current COB using linear decay model
    /// </summary>
    public double CalculateCob(DateTime time)
    {
        double totalCob = 0;

        foreach (var carb in _activeCarbs.ToList())
        {
            var hoursSince = (time - carb.Time).TotalHours;

            if (hoursSince < 0 || hoursSince > carb.AbsorptionTimeHours * 1.5)
            {
                if (hoursSince > carb.AbsorptionTimeHours * 1.5)
                    _activeCarbs.Remove(carb); // Clean up absorbed carbs
                continue;
            }

            // Linear decay with floor at min_5m_carbimpact rate
            var absorbed = Math.Min(carb.Carbs, carb.Carbs * hoursSince / carb.AbsorptionTimeHours);
            var remaining = carb.Carbs - absorbed;
            totalCob += remaining;
        }

        return totalCob;
    }

    /// <summary>
    /// Calculate the insulin activity (glucose lowering effect) at a given time.
    /// Returns mg/dL drop per 5-minute interval.
    /// </summary>
    public double CalculateInsulinActivity(DateTime time)
    {
        var diaMinutes = _profile.Dia * 60;
        var peakMinutes = _profile.Peak;
        double totalActivity = 0;

        foreach (var dose in _activeDoses)
        {
            var minutesSince = (time - dose.Time).TotalMinutes;

            if (minutesSince < 0 || minutesSince > diaMinutes)
                continue;

            // Calculate activity (derivative of IOB curve)
            var activity = CalculateExponentialActivity(minutesSince, peakMinutes, diaMinutes);

            // Convert to glucose effect per 5-minute interval
            // ISF represents total glucose drop per unit over entire DIA
            // Activity curve integrates to 1 over DIA, so we multiply by ISF directly
            // and scale by the number of 5-minute intervals in DIA
            var intervalsInDia = diaMinutes / 5.0;
            var effectPerUnit = activity * _profile.Sens * intervalsInDia * (5.0 / 60.0);
            totalActivity += dose.Units * effectPerUnit;
        }

        return totalActivity;
    }

    /// <summary>
    /// Calculate the carb absorption effect (glucose raising) at a given time.
    /// Returns mg/dL rise per 5-minute interval.
    /// </summary>
    public double CalculateCarbAbsorptionRate(DateTime time)
    {
        double totalEffect = 0;

        foreach (var carb in _activeCarbs)
        {
            var hoursSince = (time - carb.Time).TotalHours;
            var absorptionTimeHours = carb.AbsorptionTimeHours;

            if (hoursSince < 0 || hoursSince > absorptionTimeHours * 1.5)
                continue;

            // Gamma-like curve for carb absorption (peaks at ~1/3 of absorption time)
            var peakTime = absorptionTimeHours / 3.0;
            var normalizedTime = hoursSince / peakTime;

            double absorptionRate;
            if (normalizedTime > 0)
            {
                // Gamma distribution with k=2 shape
                var k = 2.0;
                absorptionRate =
                    Math.Pow(normalizedTime, k - 1) * Math.Exp(-normalizedTime * (k - 1) / 1.2);
                absorptionRate = Math.Max(0, absorptionRate);
            }
            else
            {
                absorptionRate = 0;
            }

            // Convert carbs to glucose rise
            // 1g carb raises BG by approximately ISF/CarbRatio (by definition)
            var carbSensitivity = _profile.Sens / _profile.CarbRatio; // ~4.5 mg/dL per gram
            var totalExpectedRise = carb.Carbs * carbSensitivity;

            // Spread over absorption time, convert to 5-minute interval
            var intervalsInAbsorption = absorptionTimeHours * 12; // 12 five-minute intervals per hour
            // Use 1.2 multiplier for realistic glucose rise (was 2.5 which caused max spikes)
            var risePerInterval =
                absorptionRate * (totalExpectedRise / intervalsInAbsorption) * 1.2;

            totalEffect += risePerInterval;
        }

        return totalEffect;
    }

    /// <summary>
    /// Simulate the next glucose value based on current physiological state.
    /// </summary>
    /// <param name="currentGlucose">Current glucose level (mg/dL)</param>
    /// <param name="time">Current time</param>
    /// <param name="basalRate">Current basal rate (U/hr), null to use profile default</param>
    /// <returns>Predicted glucose after 5 minutes</returns>
    public double SimulateNextGlucose(
        double currentGlucose,
        DateTime time,
        double? basalRate = null
    )
    {
        // Insulin effect (glucose lowering)
        var insulinEffect = CalculateInsulinActivity(time);

        // Carb effect (glucose raising)
        var carbEffect = CalculateCarbAbsorptionRate(time);

        // Basal effect - covers liver glucose output
        // Without basal, liver produces ~1 mg/dL per 5 min
        var basal = basalRate ?? _profile.CurrentBasal;
        var liverOutput = 1.0;
        var basalCoverage = basal / _profile.CurrentBasal; // Normalized to profile basal
        var netBasalEffect = liverOutput * (1 - basalCoverage);

        // Dawn phenomenon (4-8 AM)
        var hour = time.Hour + time.Minute / 60.0;
        var dawnEffect = 0.0;
        if (hour >= 4 && hour < 8)
        {
            var intensity = Math.Sin((hour - 4) * Math.PI / 4);
            dawnEffect = intensity * 0.5; // Up to 0.5 mg/dL per 5 min
        }

        // Net change
        var netChange = carbEffect - insulinEffect + netBasalEffect + dawnEffect;

        // Clamp to realistic rate of change (max ~4 mg/dL per minute = 20 per 5 min)
        netChange = Math.Clamp(netChange, -20, 20);

        return currentGlucose + netChange;
    }

    /// <summary>
    /// Exponential IOB model from oref0
    /// Returns fraction of insulin still active (0-1)
    /// </summary>
    private static double CalculateExponentialIob(
        double minutesSince,
        double peakMinutes,
        double diaMinutes
    )
    {
        var tau = peakMinutes * (1 - peakMinutes / diaMinutes) / (1 - 2 * peakMinutes / diaMinutes);
        var a = 2 * tau / diaMinutes;
        var S = 1 / (1 - a + (1 + a) * Math.Exp(-diaMinutes / tau));

        var t = minutesSince;
        var iob =
            1
            - S
                * (1 - a)
                * ((t * t / (tau * diaMinutes * (1 - a)) - t / tau - 1) * Math.Exp(-t / tau) + 1);

        return Math.Max(0, Math.Min(1, iob));
    }

    /// <summary>
    /// Exponential insulin activity model from oref0
    /// Returns activity level (higher = more glucose lowering)
    /// </summary>
    private static double CalculateExponentialActivity(
        double minutesSince,
        double peakMinutes,
        double diaMinutes
    )
    {
        var tau = peakMinutes * (1 - peakMinutes / diaMinutes) / (1 - 2 * peakMinutes / diaMinutes);
        var a = 2 * tau / diaMinutes;
        var S = 1 / (1 - a + (1 + a) * Math.Exp(-diaMinutes / tau));

        var t = minutesSince;
        var activity = S * (t / (tau * tau)) * Math.Exp(-t / tau);

        return Math.Max(0, activity);
    }

    /// <summary>
    /// Clear all tracked doses (for starting a new simulation)
    /// </summary>
    public void Reset()
    {
        _activeDoses.Clear();
        _activeCarbs.Clear();
    }

    /// <summary>
    /// Remove expired doses and carbs
    /// </summary>
    public void CleanupExpired(DateTime time)
    {
        var diaMinutes = _profile.Dia * 60;
        _activeDoses.RemoveAll(d => (time - d.Time).TotalMinutes > diaMinutes + 30);
        _activeCarbs.RemoveAll(c => (time - c.Time).TotalHours > c.AbsorptionTimeHours * 1.5 + 0.5);
    }

    private record InsulinDose
    {
        public DateTime Time { get; init; }
        public double Units { get; init; }
        public bool IsTempBasal { get; init; }
        public double Duration { get; init; } // minutes, for temp basals
    }

    private record CarbDose
    {
        public DateTime Time { get; init; }
        public double Carbs { get; init; }
        public double AbsorptionTimeHours { get; init; }
    }
}
