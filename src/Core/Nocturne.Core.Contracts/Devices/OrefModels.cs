using System.Text.Json.Serialization;

namespace Nocturne.Core.Contracts.Devices;

/// <summary>
/// Profile settings for oref calculations.
/// Maps to Rust oref::types::Profile
/// </summary>
public class OrefProfile
{
    /// <summary>Duration of insulin action in hours</summary>
    [JsonPropertyName("dia")]
    public double Dia { get; set; } = 3.0;

    /// <summary>Current scheduled basal rate (U/hr)</summary>
    [JsonPropertyName("currentBasal")]
    public double CurrentBasal { get; set; }

    /// <summary>Maximum IOB allowed (units)</summary>
    [JsonPropertyName("maxIob")]
    public double MaxIob { get; set; } = 10.0;

    /// <summary>Maximum daily basal rate from schedule</summary>
    [JsonPropertyName("maxDailyBasal")]
    public double MaxDailyBasal { get; set; }

    /// <summary>Absolute maximum basal rate (U/hr)</summary>
    [JsonPropertyName("maxBasal")]
    public double MaxBasal { get; set; } = 4.0;

    /// <summary>Minimum BG target (mg/dL)</summary>
    [JsonPropertyName("minBg")]
    public double MinBg { get; set; } = 100.0;

    /// <summary>Maximum BG target (mg/dL)</summary>
    [JsonPropertyName("maxBg")]
    public double MaxBg { get; set; } = 120.0;

    /// <summary>Insulin sensitivity factor (mg/dL per unit)</summary>
    [JsonPropertyName("sens")]
    public double Sens { get; set; } = 50.0;

    /// <summary>Carb ratio (grams per unit)</summary>
    [JsonPropertyName("carbRatio")]
    public double CarbRatio { get; set; } = 10.0;

    /// <summary>Insulin curve type: "bilinear", "rapid-acting", "ultra-rapid"</summary>
    [JsonPropertyName("curve")]
    public string Curve { get; set; } = "rapid-acting";

    /// <summary>Insulin peak time (minutes)</summary>
    [JsonPropertyName("peak")]
    public int Peak { get; set; } = 75;

    [JsonPropertyName("useCustomPeakTime")]
    public bool UseCustomPeakTime { get; set; }

    /// <summary>Custom insulin peak time (minutes)</summary>
    [JsonPropertyName("insulinPeakTime")]
    public int InsulinPeakTime { get; set; } = 75;

    /// <summary>Minimum autosens ratio</summary>
    [JsonPropertyName("autosensMin")]
    public double AutosensMin { get; set; } = 0.7;

    /// <summary>Maximum autosens ratio</summary>
    [JsonPropertyName("autosensMax")]
    public double AutosensMax { get; set; } = 1.2;

    /// <summary>Minimum 5-minute carb impact (mg/dL/5min)</summary>
    [JsonPropertyName("min5mCarbimpact")]
    public double Min5mCarbimpact { get; set; } = 8.0;

    /// <summary>Maximum COB (grams)</summary>
    [JsonPropertyName("maxCob")]
    public double MaxCob { get; set; } = 120.0;

    /// <summary>Maximum meal absorption time (hours)</summary>
    [JsonPropertyName("maxMealAbsorptionTime")]
    public double MaxMealAbsorptionTime { get; set; } = 6.0;

    // SMB Settings
    /// <summary>Always allow Super Micro Boluses (SMB), regardless of COB or temp target.</summary>
    [JsonPropertyName("enableSmbAlways")]
    public bool EnableSmbAlways { get; set; }

    /// <summary>Allow SMB when carbs on board are present.</summary>
    [JsonPropertyName("enableSmbWithCob")]
    public bool EnableSmbWithCob { get; set; }

    /// <summary>Allow SMB when an active temp target is set.</summary>
    [JsonPropertyName("enableSmbWithTemptarget")]
    public bool EnableSmbWithTemptarget { get; set; }

    /// <summary>Allow SMB for a period after carbs are entered.</summary>
    [JsonPropertyName("enableSmbAfterCarbs")]
    public bool EnableSmbAfterCarbs { get; set; }

    /// <summary>Allow SMB when BG is above <see cref="EnableSmbHighBgTarget"/>.</summary>
    [JsonPropertyName("enableSmbHighBg")]
    public bool EnableSmbHighBg { get; set; }

    /// <summary>BG threshold (mg/dL) above which SMB is allowed when <see cref="EnableSmbHighBg"/> is true.</summary>
    [JsonPropertyName("enableSmbHighBgTarget")]
    public double EnableSmbHighBgTarget { get; set; } = 110.0;

    /// <summary>Allow SMB even when a high temp target is active.</summary>
    [JsonPropertyName("allowSmbWithHighTemptarget")]
    public bool AllowSmbWithHighTemptarget { get; set; }

    /// <summary>Maximum SMB size expressed as minutes of the current basal rate.</summary>
    [JsonPropertyName("maxSmbBasalMinutes")]
    public int MaxSmbBasalMinutes { get; set; } = 30;

    /// <summary>Maximum UAM SMB size expressed as minutes of the current basal rate.</summary>
    [JsonPropertyName("maxUamSmbBasalMinutes")]
    public int MaxUamSmbBasalMinutes { get; set; } = 30;

    /// <summary>Minimum interval between SMBs (minutes).</summary>
    [JsonPropertyName("smbInterval")]
    public int SmbInterval { get; set; } = 3;

    /// <summary>Pump bolus increment (units) for rounding SMB amounts.</summary>
    [JsonPropertyName("bolusIncrement")]
    public double BolusIncrement { get; set; } = 0.1;

    /// <summary>Fraction of the calculated insulin requirement delivered as an SMB (0–1).</summary>
    [JsonPropertyName("smbDeliveryRatio")]
    public double SmbDeliveryRatio { get; set; } = 0.5;

    // UAM Settings
    /// <summary>Enable Un-Announced Meal (UAM) detection for unlogged carb absorption.</summary>
    [JsonPropertyName("enableUam")]
    public bool EnableUam { get; set; }

    // Dynamic ISF Settings
    /// <summary>Enable dynamic insulin sensitivity factor (Dynamic ISF / Autosens ISF).</summary>
    [JsonPropertyName("useDynamicIsf")]
    public bool UseDynamicIsf { get; set; }

    /// <summary>Use sigmoid function for Dynamic ISF scaling instead of linear scaling.</summary>
    [JsonPropertyName("sigmoid")]
    public bool Sigmoid { get; set; }

    /// <summary>Adjustment factor for Dynamic ISF linear scaling.</summary>
    [JsonPropertyName("adjustmentFactor")]
    public double AdjustmentFactor { get; set; } = 1.0;

    /// <summary>Adjustment factor for Dynamic ISF sigmoid scaling.</summary>
    [JsonPropertyName("adjustmentFactorSigmoid")]
    public double AdjustmentFactorSigmoid { get; set; } = 0.5;

    /// <summary>Weight percentage blending Dynamic ISF with the scheduled ISF (0–1).</summary>
    [JsonPropertyName("weightPercentage")]
    public double WeightPercentage { get; set; } = 0.65;

    /// <summary>Adjust basal rate based on total daily dose (TDD).</summary>
    [JsonPropertyName("tddAdjBasal")]
    public bool TddAdjBasal { get; set; }

    // Temp Target Settings
    /// <summary>Whether a temporary BG target is currently active.</summary>
    [JsonPropertyName("temptargetSet")]
    public bool TemptargetSet { get; set; }

    /// <summary>When true, a high temp target increases insulin sensitivity (lowers ISF).</summary>
    [JsonPropertyName("highTemptargetRaisesSensitivity")]
    public bool HighTemptargetRaisesSensitivity { get; set; }

    /// <summary>When true, a low temp target decreases insulin sensitivity (raises ISF).</summary>
    [JsonPropertyName("lowTemptargetLowersSensitivity")]
    public bool LowTemptargetLowersSensitivity { get; set; }

    /// <summary>Enable exercise mode, which adjusts sensitivity during physical activity.</summary>
    [JsonPropertyName("exerciseMode")]
    public bool ExerciseMode { get; set; }

    /// <summary>BG target (mg/dL) at which exercise mode reduces basal to half the scheduled rate.</summary>
    [JsonPropertyName("halfBasalExerciseTarget")]
    public double HalfBasalExerciseTarget { get; set; } = 160.0;

    // Safety Settings
    /// <summary>Skip setting neutral temp basals when the algorithm would otherwise cancel a running temp.</summary>
    [JsonPropertyName("skipNeutralTemps")]
    public bool SkipNeutralTemps { get; set; }

    /// <summary>Reset autosens calculation when a pump rewind event is detected.</summary>
    [JsonPropertyName("rewindResetsAutosens")]
    public bool RewindResetsAutosens { get; set; } = true;

    /// <summary>Enable the A52 risk calculation for additional safety checks.</summary>
    [JsonPropertyName("a52RiskEnable")]
    public bool A52RiskEnable { get; set; }

    /// <summary>Zero out IOB when the pump is suspended.</summary>
    [JsonPropertyName("suspendZerosIob")]
    public bool SuspendZerosIob { get; set; } = true;
}

/// <summary>
/// Treatment event for oref calculations.
/// Maps to Rust oref::types::Treatment
/// </summary>
public class OrefTreatment
{
    /// <summary>ISO timestamp string</summary>
    [JsonPropertyName("timestamp")]
    public string? Timestamp { get; set; }

    /// <summary>Unix milliseconds</summary>
    [JsonPropertyName("date")]
    public long Date { get; set; }

    /// <summary>Started at timestamp (for temp basals)</summary>
    [JsonPropertyName("startedAt")]
    public string? StartedAt { get; set; }

    [JsonPropertyName("createdAt")]
    public string? CreatedAt { get; set; }

    /// <summary>Insulin amount (units) - for boluses</summary>
    [JsonPropertyName("insulin")]
    public double? Insulin { get; set; }

    /// <summary>Carb amount (grams)</summary>
    [JsonPropertyName("carbs")]
    public double? Carbs { get; set; }

    /// <summary>Nightscout carbs</summary>
    [JsonPropertyName("nsCarbs")]
    public double? NsCarbs { get; set; }

    /// <summary>Bolus Wizard carbs</summary>
    [JsonPropertyName("bwCarbs")]
    public double? BwCarbs { get; set; }

    [JsonPropertyName("journalCarbs")]
    public double? JournalCarbs { get; set; }

    /// <summary>Temp basal rate (U/hr)</summary>
    [JsonPropertyName("rate")]
    public double? Rate { get; set; }

    /// <summary>Duration in minutes</summary>
    [JsonPropertyName("duration")]
    public double? Duration { get; set; }

    [JsonPropertyName("_type")]
    public string? EventType { get; set; }

    public static OrefTreatment Bolus(double insulin, DateTimeOffset timestamp) =>
        new()
        {
            Insulin = insulin,
            Date = timestamp.ToUnixTimeMilliseconds(),
            Timestamp = timestamp.ToString("O"),
            StartedAt = timestamp.ToString("O"),
            EventType = "Bolus",
        };

    public static OrefTreatment TempBasal(
        double rate,
        double durationMinutes,
        DateTimeOffset timestamp
    ) =>
        new()
        {
            Rate = rate,
            Duration = durationMinutes,
            Date = timestamp.ToUnixTimeMilliseconds(),
            Timestamp = timestamp.ToString("O"),
            StartedAt = timestamp.ToString("O"),
            EventType = "TempBasal",
        };

    public static OrefTreatment CarbEntry(double carbs, DateTimeOffset timestamp) =>
        new()
        {
            Carbs = carbs,
            NsCarbs = carbs,
            Date = timestamp.ToUnixTimeMilliseconds(),
            Timestamp = timestamp.ToString("O"),
            EventType = "Carbs",
        };
}

/// <summary>
/// Glucose reading for oref calculations.
/// Maps to Rust oref::types::GlucoseReading
/// </summary>
public class OrefGlucoseReading
{
    /// <summary>Glucose value (mg/dL)</summary>
    [JsonPropertyName("glucose")]
    public double Glucose { get; set; }

    /// <summary>Unix milliseconds</summary>
    [JsonPropertyName("date")]
    public long Date { get; set; }

    /// <summary>ISO date string</summary>
    [JsonPropertyName("dateString")]
    public string? DateString { get; set; }

    [JsonPropertyName("displayTime")]
    public string? DisplayTime { get; set; }

    /// <summary>Noise level (0-4)</summary>
    [JsonPropertyName("noise")]
    public double? Noise { get; set; }

    [JsonPropertyName("direction")]
    public string? Direction { get; set; }
}

/// <summary>
/// Glucose status with deltas.
/// Maps to Rust oref::types::GlucoseStatus
/// </summary>
public class OrefGlucoseStatus
{
    /// <summary>Current glucose (mg/dL)</summary>
    [JsonPropertyName("glucose")]
    public double Glucose { get; set; }

    /// <summary>5-minute delta (mg/dL)</summary>
    [JsonPropertyName("delta")]
    public double Delta { get; set; }

    /// <summary>Short average delta (15 min)</summary>
    [JsonPropertyName("shortAvgdelta")]
    public double ShortAvgdelta { get; set; }

    /// <summary>Long average delta (45 min)</summary>
    [JsonPropertyName("longAvgdelta")]
    public double LongAvgdelta { get; set; }

    /// <summary>Unix milliseconds of reading</summary>
    [JsonPropertyName("date")]
    public long Date { get; set; }

    [JsonPropertyName("noise")]
    public double? Noise { get; set; }
}

/// <summary>
/// Current temp basal state.
/// Maps to Rust oref::types::CurrentTemp
/// </summary>
public class OrefCurrentTemp
{
    /// <summary>Duration remaining (minutes)</summary>
    [JsonPropertyName("duration")]
    public double Duration { get; set; }

    /// <summary>Rate (U/hr)</summary>
    [JsonPropertyName("rate")]
    public double Rate { get; set; }

    /// <summary>Type: "absolute" or "percent"</summary>
    [JsonPropertyName("temp")]
    public string Temp { get; set; } = "absolute";

    /// <summary>A zero-duration, zero-rate absolute temp basal representing no active temp.</summary>
    public static OrefCurrentTemp None =>
        new()
        {
            Duration = 0,
            Rate = 0,
            Temp = "absolute",
        };
}

/// <summary>
/// IOB calculation result.
/// Maps to Rust oref::types::IOBData
/// </summary>
public class OrefIobResult
{
    /// <summary>Total IOB (units)</summary>
    [JsonPropertyName("iob")]
    public double Iob { get; set; }

    /// <summary>Insulin activity (units/minute)</summary>
    [JsonPropertyName("activity")]
    public double Activity { get; set; }

    [JsonPropertyName("basalIob")]
    public double BasalIob { get; set; }

    [JsonPropertyName("bolusIob")]
    public double BolusIob { get; set; }

    [JsonPropertyName("netBasalInsulin")]
    public double NetBasalInsulin { get; set; }

    /// <summary>Total bolus insulin delivered</summary>
    [JsonPropertyName("bolusInsulin")]
    public double BolusInsulin { get; set; }

    /// <summary>Time of calculation (Unix millis)</summary>
    [JsonPropertyName("time")]
    public long Time { get; set; }

    /// <summary>Time of last bolus (Unix millis)</summary>
    [JsonPropertyName("lastBolusTime")]
    public long? LastBolusTime { get; set; }
}

/// <summary>
/// COB calculation result.
/// Maps to Rust oref::types::COBResult
/// </summary>
public class OrefCobResult
{
    /// <summary>Remaining carbs on board (grams)</summary>
    [JsonPropertyName("mealCob")]
    public double MealCob { get; set; }

    /// <summary>Carbs absorbed so far (grams)</summary>
    [JsonPropertyName("carbsAbsorbed")]
    public double CarbsAbsorbed { get; set; }

    [JsonPropertyName("currentDeviation")]
    public double CurrentDeviation { get; set; }

    [JsonPropertyName("maxDeviation")]
    public double MaxDeviation { get; set; }

    [JsonPropertyName("minDeviation")]
    public double MinDeviation { get; set; }

    [JsonPropertyName("slopeFromMax")]
    public double SlopeFromMax { get; set; }

    [JsonPropertyName("slopeFromMin")]
    public double SlopeFromMin { get; set; }
}

/// <summary>
/// Autosens calculation result.
/// Maps to Rust oref::types::AutosensData
/// </summary>
public class OrefAutosensResult
{
    /// <summary>Sensitivity ratio (1.0 = normal, >1 = resistant, &lt;1 = sensitive)</summary>
    [JsonPropertyName("ratio")]
    public double Ratio { get; set; } = 1.0;
}

/// <summary>
/// Meal data for determine basal.
/// Maps to Rust oref::types::MealData
/// </summary>
public class OrefMealData
{
    /// <summary>Total carbs entered</summary>
    [JsonPropertyName("carbs")]
    public double Carbs { get; set; }

    /// <summary>Current Carbs on Board (grams)</summary>
    [JsonPropertyName("mealCob")]
    public double MealCob { get; set; }

    /// <summary>Current BG deviation from expected</summary>
    [JsonPropertyName("currentDeviation")]
    public double CurrentDeviation { get; set; }

    /// <summary>Maximum deviation seen</summary>
    [JsonPropertyName("maxDeviation")]
    public double MaxDeviation { get; set; }

    /// <summary>Minimum deviation seen</summary>
    [JsonPropertyName("minDeviation")]
    public double MinDeviation { get; set; }
}

/// <summary>
/// Complete inputs for determine basal algorithm.
/// Maps to Rust oref::determine_basal::DetermineBasalInputs
/// </summary>
public class OrefDetermineBasalInputs
{
    [JsonPropertyName("glucoseStatus")]
    public required OrefGlucoseStatus GlucoseStatus { get; set; }

    [JsonPropertyName("currentTemp")]
    public required OrefCurrentTemp CurrentTemp { get; set; }

    [JsonPropertyName("iobData")]
    public required OrefIobResult IobData { get; set; }

    [JsonPropertyName("profile")]
    public required OrefProfile Profile { get; set; }

    [JsonPropertyName("autosensData")]
    public OrefAutosensResult AutosensData { get; set; } = new();

    [JsonPropertyName("mealData")]
    public OrefMealData MealData { get; set; } = new();

    /// <summary>Whether micro bolus (SMB) is allowed</summary>
    [JsonPropertyName("microBolusAllowed")]
    public bool MicroBolusAllowed { get; set; }

    /// <summary>Current time in milliseconds (optional)</summary>
    [JsonPropertyName("currentTimeMillis")]
    public long? CurrentTimeMillis { get; set; }
}

/// <summary>
/// Result from determine basal algorithm.
/// Maps to Rust oref::types::DetermineBasalResult
/// </summary>
public class OrefDetermineBasalResult
{
    /// <summary>Recommended temp basal rate (U/hr)</summary>
    [JsonPropertyName("rate")]
    public double? Rate { get; set; }

    /// <summary>Recommended temp basal duration (minutes)</summary>
    [JsonPropertyName("duration")]
    public int? Duration { get; set; }

    /// <summary>Reason string explaining the decision</summary>
    [JsonPropertyName("reason")]
    public string Reason { get; set; } = string.Empty;

    /// <summary>Current COB (grams)</summary>
    [JsonPropertyName("cob")]
    public double Cob { get; set; }

    /// <summary>Current IOB (units)</summary>
    [JsonPropertyName("iob")]
    public double Iob { get; set; }

    /// <summary>Eventual BG prediction (mg/dL)</summary>
    [JsonPropertyName("eventualBg")]
    public double EventualBg { get; set; }

    /// <summary>Insulin required to reach target</summary>
    [JsonPropertyName("insulinReq")]
    public double? InsulinReq { get; set; }

    /// <summary>SMB amount to deliver (units)</summary>
    [JsonPropertyName("units")]
    public double? Units { get; set; }

    /// <summary>Tick indicator for UI</summary>
    [JsonPropertyName("tick")]
    public string? Tick { get; set; }

    /// <summary>Error message if calculation failed</summary>
    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("sensitivityRatio")]
    public double? SensitivityRatio { get; set; }

    /// <summary>Variable sensitivity (adjusted ISF)</summary>
    [JsonPropertyName("variableSens")]
    public double? VariableSens { get; set; }

    [JsonPropertyName("predictedBg")]
    public double[]? PredictedBg { get; set; }

    /// <summary>Predicted BG with UAM</summary>
    [JsonPropertyName("predBgsUam")]
    public double[]? PredBgsUam { get; set; }

    /// <summary>Predicted BG with IOB only</summary>
    [JsonPropertyName("predBgsIob")]
    public double[]? PredBgsIob { get; set; }

    /// <summary>Predicted BG with zero temp</summary>
    [JsonPropertyName("predBgsZt")]
    public double[]? PredBgsZt { get; set; }

    /// <summary>Predicted BG with COB</summary>
    [JsonPropertyName("predBgsCob")]
    public double[]? PredBgsCob { get; set; }

    /// <summary>Minutes ago of current BG</summary>
    [JsonPropertyName("bgMinsAgo")]
    public double? BgMinsAgo { get; set; }

    [JsonPropertyName("targetBg")]
    public double? TargetBg { get; set; }

    [JsonPropertyName("smbEnabled")]
    public bool? SmbEnabled { get; set; }

    [JsonPropertyName("carbsReq")]
    public double? CarbsReq { get; set; }

    [JsonPropertyName("threshold")]
    public double? Threshold { get; set; }

    /// <summary>Check if an SMB is recommended</summary>
    public bool HasSmb => Units.HasValue && Units.Value > 0;

    /// <summary>Check if a temp basal change is recommended</summary>
    public bool HasTemp => Rate.HasValue && Duration.HasValue;

    /// <summary>Check if there was an error</summary>
    public bool HasError => !string.IsNullOrEmpty(Error);
}
