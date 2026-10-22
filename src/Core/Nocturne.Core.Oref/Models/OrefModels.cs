using System.Text.Json.Serialization;

namespace Nocturne.Core.Oref.Models;

/// <summary>
/// Profile containing user's insulin pump settings.
/// </summary>
public class OrefProfile
{
    /// <summary>Duration of insulin action in hours</summary>
    [JsonPropertyName("dia")]
    public double Dia { get; set; } = 3.0;

    /// <summary>Current basal rate (U/hr)</summary>
    [JsonPropertyName("currentBasal")]
    public double CurrentBasal { get; set; }

    /// <summary>Maximum IOB allowed (U)</summary>
    [JsonPropertyName("maxIob")]
    public double MaxIob { get; set; } = 10.0;

    /// <summary>Maximum daily basal (U/hr)</summary>
    [JsonPropertyName("maxDailyBasal")]
    public double MaxDailyBasal { get; set; } = 2.0;

    /// <summary>Maximum basal rate (U/hr)</summary>
    [JsonPropertyName("maxBasal")]
    public double MaxBasal { get; set; } = 4.0;

    /// <summary>Minimum target BG (mg/dL)</summary>
    [JsonPropertyName("minBg")]
    public double MinBg { get; set; } = 100.0;

    /// <summary>Maximum target BG (mg/dL)</summary>
    [JsonPropertyName("maxBg")]
    public double MaxBg { get; set; } = 120.0;

    /// <summary>Target BG (mg/dL) - used when min/max are equal</summary>
    [JsonPropertyName("targetBg")]
    public double? TargetBg { get; set; }

    /// <summary>Insulin sensitivity factor (mg/dL per unit)</summary>
    [JsonPropertyName("sens")]
    public double Sens { get; set; } = 50.0;

    /// <summary>Carb ratio (g per unit)</summary>
    [JsonPropertyName("carbRatio")]
    public double CarbRatio { get; set; } = 10.0;

    /// <summary>Insulin curve type (bilinear, rapid-acting, ultra-rapid)</summary>
    [JsonPropertyName("curve")]
    public string? Curve { get; set; }

    /// <summary>Insulin peak time (minutes)</summary>
    [JsonPropertyName("peak")]
    public int? Peak { get; set; }

    [JsonPropertyName("carbAbsorptionRate")]
    public double? CarbAbsorptionRate { get; set; }

    /// <summary>Whether SMB (Super Micro Bolus) is enabled</summary>
    [JsonPropertyName("enableSMBAlways")]
    public bool EnableSmbAlways { get; set; }

    /// <summary>Whether UAM (Unannounced Meals) is enabled</summary>
    [JsonPropertyName("enableUAM")]
    public bool EnableUam { get; set; }
}

/// <summary>
/// Current glucose status including delta calculations.
/// </summary>
public class GlucoseStatus
{
    /// <summary>Current glucose reading (mg/dL)</summary>
    [JsonPropertyName("glucose")]
    public double Glucose { get; set; }

    /// <summary>Change in glucose per 5 minutes (mg/dL)</summary>
    [JsonPropertyName("delta")]
    public double Delta { get; set; }

    /// <summary>Short average delta (~15 min)</summary>
    [JsonPropertyName("shortAvgdelta")]
    public double ShortAvgDelta { get; set; }

    /// <summary>Long average delta (~45 min)</summary>
    [JsonPropertyName("longAvgdelta")]
    public double LongAvgDelta { get; set; }

    /// <summary>Timestamp as Unix milliseconds</summary>
    [JsonPropertyName("date")]
    public long Date { get; set; }

    /// <summary>Glucose noise level (optional)</summary>
    [JsonPropertyName("noise")]
    public int? Noise { get; set; }
}

/// <summary>
/// Insulin on Board data.
/// </summary>
public class IobData
{
    /// <summary>Total IOB (U)</summary>
    [JsonPropertyName("iob")]
    public double Iob { get; set; }

    /// <summary>Current insulin activity (U/min)</summary>
    [JsonPropertyName("activity")]
    public double Activity { get; set; }

    /// <summary>Basal IOB (U)</summary>
    [JsonPropertyName("basalIob")]
    public double BasalIob { get; set; }

    /// <summary>Bolus IOB (U)</summary>
    [JsonPropertyName("bolusIob")]
    public double BolusIob { get; set; }

    /// <summary>Net basal insulin being delivered (U/hr)</summary>
    [JsonPropertyName("netBasalInsulin")]
    public double NetBasalInsulin { get; set; }

    /// <summary>Timestamp as Unix milliseconds</summary>
    [JsonPropertyName("time")]
    public long Time { get; set; }
}

/// <summary>
/// Current temp basal state.
/// </summary>
public class CurrentTemp
{
    /// <summary>Current temp basal rate (U/hr)</summary>
    [JsonPropertyName("rate")]
    public double Rate { get; set; }

    /// <summary>Duration remaining (minutes)</summary>
    [JsonPropertyName("duration")]
    public double Duration { get; set; }

    /// <summary>Temp basal type (absolute or percent)</summary>
    [JsonPropertyName("temp")]
    public string Temp { get; set; } = "absolute";
}

public class AutosensData
{
    /// <summary>Sensitivity ratio (1.0 = normal, &lt;1.0 = more sensitive, &gt;1.0 = more resistant)</summary>
    [JsonPropertyName("ratio")]
    public double Ratio { get; set; } = 1.0;
}

/// <summary>
/// Meal data for carb calculations.
/// </summary>
public class MealData
{
    /// <summary>Carbs on board (g)</summary>
    [JsonPropertyName("mealCob")]
    public double Cob { get; set; }

    /// <summary>Carbs absorbed (g)</summary>
    [JsonPropertyName("carbsAbsorbed")]
    public double CarbsAbsorbed { get; set; }

    /// <summary>Minutes until all carbs absorbed</summary>
    [JsonPropertyName("mealCarbTime")]
    public int? MealCarbTime { get; set; }
}

/// <summary>
/// Input for the determine_basal algorithm.
/// </summary>
public class DetermineBasalInputs
{
    [JsonPropertyName("glucoseStatus")]
    public GlucoseStatus GlucoseStatus { get; set; } = new();

    [JsonPropertyName("currentTemp")]
    public CurrentTemp CurrentTemp { get; set; } = new();

    [JsonPropertyName("iobData")]
    public IobData IobData { get; set; } = new();

    [JsonPropertyName("profile")]
    public OrefProfile Profile { get; set; } = new();

    [JsonPropertyName("autosensData")]
    public AutosensData AutosensData { get; set; } = new();

    [JsonPropertyName("mealData")]
    public MealData MealData { get; set; } = new();

    [JsonPropertyName("microBolusAllowed")]
    public bool MicroBolusAllowed { get; set; }

    [JsonPropertyName("currentTimeMillis")]
    public long? CurrentTimeMillis { get; set; }
}

/// <summary>
/// Result from the determine_basal algorithm including predictions.
/// </summary>
public class DetermineBasalResult
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

    /// <summary>SMB amount to deliver (units)</summary>
    [JsonPropertyName("units")]
    public double? Units { get; set; }

    /// <summary>Error message if calculation failed</summary>
    [JsonPropertyName("error")]
    public string? Error { get; set; }

    /// <summary>Predicted BG values (5-min intervals)</summary>
    [JsonPropertyName("predictedBg")]
    public List<double>? PredictedBg { get; set; }

    /// <summary>Predicted BG with IOB only</summary>
    [JsonPropertyName("predBgsIob")]
    public List<double>? PredBgsIob { get; set; }

    /// <summary>Predicted BG with UAM</summary>
    [JsonPropertyName("predBgsUam")]
    public List<double>? PredBgsUam { get; set; }

    /// <summary>Predicted BG with zero temp</summary>
    [JsonPropertyName("predBgsZt")]
    public List<double>? PredBgsZt { get; set; }

    /// <summary>Predicted BG with COB</summary>
    [JsonPropertyName("predBgsCob")]
    public List<double>? PredBgsCob { get; set; }

    [JsonPropertyName("targetBg")]
    public double? TargetBg { get; set; }

    [JsonPropertyName("sensitivityRatio")]
    public double? SensitivityRatio { get; set; }
}

/// <summary>
/// Glucose reading for input to oref algorithms.
/// </summary>
public class GlucoseReading
{
    /// <summary>Glucose value (mg/dL)</summary>
    [JsonPropertyName("sgv")]
    public double Sgv { get; set; }

    /// <summary>Timestamp as Unix milliseconds</summary>
    [JsonPropertyName("date")]
    public long Date { get; set; }

    /// <summary>Timestamp as ISO string</summary>
    [JsonPropertyName("dateString")]
    public string? DateString { get; set; }

    [JsonPropertyName("direction")]
    public string? Direction { get; set; }
}

/// <summary>
/// Treatment record (bolus, temp basal, carbs).
/// </summary>
public class OrefTreatment
{
    [JsonPropertyName("_type")]
    public string? EventType { get; set; }

    /// <summary>Timestamp as ISO string</summary>
    [JsonPropertyName("timestamp")]
    public string? Timestamp { get; set; }

    /// <summary>Timestamp as ISO string</summary>
    [JsonPropertyName("startedAt")]
    public string? StartedAt { get; set; }

    /// <summary>Timestamp as ISO string</summary>
    [JsonPropertyName("createdAt")]
    public string? CreatedAt { get; set; }

    /// <summary>Timestamp as Unix milliseconds</summary>
    [JsonIgnore]
    public long? Mills { get; set; }

    /// <summary>Timestamp as Unix milliseconds</summary>
    [JsonPropertyName("date")]
    public long? Date
    {
        get => Mills;
        set => Mills = value;
    }

    /// <summary>Insulin amount (U)</summary>
    [JsonPropertyName("insulin")]
    public double? Insulin { get; set; }

    /// <summary>Carbs amount (g)</summary>
    [JsonPropertyName("carbs")]
    public double? Carbs { get; set; }

    /// <summary>Temp basal rate (U/hr)</summary>
    [JsonPropertyName("rate")]
    public double? Rate { get; set; }

    /// <summary>Duration (minutes)</summary>
    [JsonPropertyName("duration")]
    public int? Duration { get; set; }

    /// <summary>Percentage for temp basal</summary>
    [JsonPropertyName("percent")]
    public double? Percent { get; set; }
}

public class CobResult
{
    /// <summary>Carbs on board (g)</summary>
    [JsonPropertyName("cob")]
    public double Cob { get; set; }

    /// <summary>Time carbs will be absorbed</summary>
    [JsonPropertyName("carbAbsorptionTime")]
    public double? CarbAbsorptionTime { get; set; }
}
