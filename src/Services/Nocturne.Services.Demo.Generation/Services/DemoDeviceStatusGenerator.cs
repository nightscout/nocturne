using Nocturne.Core.Constants;
using Nocturne.Core.Models;

namespace Nocturne.Services.Demo.Services;

/// <summary>
/// Builds Trio-style <see cref="DeviceStatus"/> documents for the demo tenant:
/// an <c>openaps</c> block with suggested/enacted actions and prediction curves
/// derived from the simulation state, a pump block with reservoir and battery,
/// and an uploader (phone) block. Documents feed the same decomposer as real
/// uploads, so APS/pump/uploader snapshots, suspension spans, and battery
/// reports light up exactly as they do for a live Trio rig.
///
/// Pump consumable levels are pure functions of wall-clock time against the
/// <see cref="DemoDeviceLifecycle"/> change schedule — reservoir refills on
/// Insulin Change days, pump battery resets on Pump Battery Change days — so
/// history, realtime ticks, and re-seeds all agree without carrying state.
/// </summary>
public static class DemoDeviceStatusGenerator
{
    /// <summary>Device string carried on every demo device status.</summary>
    public const string DeviceName = "Trio";

    private const string AidVersion = "0.5.1";
    private const double ReservoirCapacityUnits = 200.0;
    private const double AssumedDailyInsulinUnits = 34.0;
    private const double PumpBatteryDrainPerDayPercent = 4.4;

    /// <summary>Prediction curve length: 42 five-minute points = 3.5 hours, matching oref.</summary>
    private const int PredictionPoints = 42;

    /// <summary>
    /// Builds the device status for one simulation step (historical) or one
    /// realtime tick. <paramref name="localTime"/> is the step's local time;
    /// the emitted mills convert via its Kind.
    /// </summary>
    public static DeviceStatus Create(
        DateTime localTime,
        double glucose,
        double iob,
        double cob,
        double? tempBasalRate,
        int? tempBasalDuration,
        double effectiveIsf,
        double effectiveCarbRatio,
        double targetGlucose,
        double scheduledBasalRate,
        DayScenario scenario)
    {
        var mills = new DateTimeOffset(localTime).ToUnixTimeMilliseconds();
        var isoTime = new DateTimeOffset(localTime).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        var rng = DayScenarios.RngFor(localTime.Date, $"devicestatus:{localTime.Hour}:{localTime.Minute}");

        var eventualBg = EstimateEventualBg(glucose, iob, cob, effectiveIsf, effectiveCarbRatio, targetGlucose);
        var sensitivityRatio = SensitivityRatioFor(scenario);
        var predictions = BuildPredictions(glucose, eventualBg, cob, targetGlucose, rng);

        // Basal IOB is the temp-basal component; the demo's simulator tracks a
        // single pool, so split it with a plausible ratio rather than pretending
        // to more precision than the simulation has.
        var bolusIob = Math.Round(iob * 0.85, 3);
        var basalIob = Math.Round(iob - bolusIob, 3);

        var insulinReq = Math.Round((glucose - targetGlucose) / effectiveIsf - iob * 0.5, 2);
        var reason = BuildReason(glucose, eventualBg, iob, cob, effectiveIsf, targetGlucose,
            sensitivityRatio, tempBasalRate, tempBasalDuration, insulinReq);

        var suggested = new OpenApsSuggested
        {
            Timestamp = isoTime,
            DeliverAt = isoTime,
            Bg = Math.Round(glucose, 0),
            EventualBG = Math.Round(eventualBg, 0),
            TargetBG = targetGlucose,
            IOB = Math.Round(iob, 3),
            COB = Math.Round(cob, 1),
            InsulinReq = insulinReq,
            SensitivityRatio = sensitivityRatio,
            Rate = tempBasalRate,
            Duration = tempBasalDuration,
            Reason = reason,
            PredBGs = predictions,
        };

        var enacted = new OpenApsEnacted
        {
            Timestamp = isoTime,
            DeliverAt = isoTime,
            Bg = suggested.Bg,
            EventualBG = suggested.EventualBG,
            TargetBG = suggested.TargetBG,
            IOB = suggested.IOB,
            COB = suggested.COB,
            InsulinReq = suggested.InsulinReq,
            SensitivityRatio = suggested.SensitivityRatio,
            Rate = tempBasalRate ?? scheduledBasalRate,
            Duration = tempBasalDuration ?? 0,
            Reason = reason,
            PredBGs = predictions,
            Received = true,
        };

        return new DeviceStatus
        {
            Device = DeviceName,
            Mills = mills,
            CreatedAt = new DateTimeOffset(localTime).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            UtcOffset = (int)TimeZoneInfo.Local.GetUtcOffset(localTime).TotalMinutes,
            OpenAps = new OpenApsStatus
            {
                Suggested = suggested,
                Enacted = enacted,
                Version = AidVersion,
                Iob = new OpenApsIobData
                {
                    Iob = Math.Round(iob, 3),
                    BolusIob = bolusIob,
                    BasalIob = basalIob,
                    Timestamp = isoTime,
                },
                Cob = Math.Round(cob, 1),
            },
            Pump = new PumpStatus
            {
                Manufacturer = "Insulet",
                Model = "Omnipod DASH",
                Reservoir = ReservoirAt(localTime),
                Battery = new PumpBattery { Percent = PumpBatteryAt(localTime) },
                Clock = isoTime,
                Status = new PumpStatusDetails
                {
                    Status = "normal",
                    Bolusing = false,
                    Suspended = false,
                },
            },
            Uploader = new UploaderStatus
            {
                Battery = UploaderBatteryAt(localTime),
                IsCharging = IsUploaderCharging(localTime),
            },
        };
    }

    /// <summary>
    /// Reservoir level as a function of time since the last Insulin Change in
    /// the lifecycle schedule, draining at the assumed daily rate.
    /// </summary>
    public static double ReservoirAt(DateTime localTime)
    {
        var sinceChange = DemoDeviceLifecycle.TimeSinceLastChange(localTime, "Insulin Change");
        var used = AssumedDailyInsulinUnits * sinceChange.TotalDays;
        return Math.Round(Math.Max(8, ReservoirCapacityUnits - used), 1);
    }

    /// <summary>Pump battery percent, resetting on Pump Battery Change days.</summary>
    public static int PumpBatteryAt(DateTime localTime)
    {
        var sinceChange = DemoDeviceLifecycle.TimeSinceLastChange(localTime, "Pump Battery Change");
        var drained = PumpBatteryDrainPerDayPercent * sinceChange.TotalDays;
        return (int)Math.Clamp(100 - drained, 4, 100);
    }

    /// <summary>
    /// Phone battery: overnight charge to 100% by ~06:30, then a daytime drain
    /// to ~30% with a deterministic per-day wobble.
    /// </summary>
    public static int UploaderBatteryAt(DateTime localTime)
    {
        var hour = localTime.Hour + localTime.Minute / 60.0;
        var wobble = DayScenarios.Roll(localTime.Date, "uploader-battery", 12) - 6;

        if (IsUploaderCharging(localTime))
        {
            // Charging window 23:00–06:30: ramp from ~35 to 100.
            var chargeHours = hour >= 23 ? hour - 23 : hour + 1;
            var pct = 35 + chargeHours / 7.5 * 65;
            return (int)Math.Clamp(pct + wobble, 20, 100);
        }

        // Discharge from 100 at 06:30 to ~32 at 23:00.
        var dischargeHours = hour - 6.5;
        var level = 100 - dischargeHours / 16.5 * 68;
        return (int)Math.Clamp(level + wobble, 20, 100);
    }

    /// <summary>Phone charges overnight (23:00–06:30).</summary>
    public static bool IsUploaderCharging(DateTime localTime)
    {
        var hour = localTime.Hour + localTime.Minute / 60.0;
        return hour >= 23 || hour < 6.5;
    }

    private static double EstimateEventualBg(
        double glucose, double iob, double cob, double effectiveIsf, double effectiveCarbRatio, double targetGlucose)
    {
        // oref's core arithmetic: remaining insulin pulls glucose down by ISF,
        // remaining carbs push it up by ISF-per-carb-ratio.
        var insulinEffect = iob * effectiveIsf;
        var carbEffect = cob / effectiveCarbRatio * effectiveIsf;
        return Math.Clamp(glucose - insulinEffect + carbEffect, 40, 320);
    }

    private static double SensitivityRatioFor(DayScenario scenario) => scenario switch
    {
        DayScenario.Exercise => 1.25,
        DayScenario.LowDay => 1.15,
        DayScenario.SickDay => 0.78,
        DayScenario.StressDay => 0.88,
        DayScenario.HighDay => 0.9,
        _ => 1.0,
    };

    private static OpenApsPredBGs BuildPredictions(
        double glucose, double eventualBg, double cob, double targetGlucose, Random rng)
    {
        // IOB curve: exponential approach from current glucose to eventualBG.
        var iobCurve = Curve(glucose, eventualBg, PredictionPoints, rng);

        // Zero-temp curve: what happens if basal stops — drifts higher than the
        // IOB curve, bottoming out above it.
        var ztTarget = Math.Max(eventualBg + 25, Math.Min(glucose, targetGlucose + 10));
        var ztCurve = Curve(glucose, ztTarget, 15, rng);

        // UAM curve: unannounced-meal detection decays a little below eventual.
        var uamCurve = Curve(glucose, Math.Max(40, eventualBg - 12), PredictionPoints - 1, rng);

        var predictions = new OpenApsPredBGs { IOB = iobCurve, ZT = ztCurve, UAM = uamCurve };

        // COB curve only exists while carbs remain, as in real oref output:
        // absorption pushes the near curve up before insulin brings it down.
        if (cob > 0.5)
            predictions.COB = Curve(glucose + Math.Min(40, cob * 1.5), eventualBg, PredictionPoints, rng);

        return predictions;
    }

    private static List<double?> Curve(double from, double to, int points, Random rng)
    {
        var curve = new List<double?>(points);
        for (var i = 0; i < points; i++)
        {
            // 1 - e^-kt approach with ~85% completion at the end of the curve.
            var progress = 1 - Math.Exp(-2.0 * i / points);
            var value = from + (to - from) * progress + (rng.NextDouble() - 0.5) * 2;
            curve.Add(Math.Round(Math.Clamp(value, 39, 400), 0));
        }

        return curve;
    }

    private static string BuildReason(
        double glucose, double eventualBg, double iob, double cob, double effectiveIsf,
        double targetGlucose, double sensitivityRatio, double? tempBasalRate, int? tempBasalDuration,
        double insulinReq)
    {
        var action = tempBasalRate is { } rate && tempBasalDuration is { } duration
            ? $"setting {duration}m temp basal of {rate:0.0#}U/h."
            : "temp basal within range, no action.";

        return $"Autosens ratio: {sensitivityRatio:0.0#}, ISF: {effectiveIsf:0}, COB: {cob:0.#}, "
            + $"IOB: {iob:0.0##}, Target: {targetGlucose:0}, "
            + $"Eventual BG {eventualBg:0} vs BG {glucose:0}, insulinReq {insulinReq:0.0#}; {action}";
    }
}
