using System.Text.Json.Serialization;

namespace Nocturne.Connectors.Glooko.Models;

/// <summary>
///     Response from /api/v3/graph/data endpoint
/// </summary>
public class GlookoV3GraphResponse
{
    [JsonPropertyName("series")] public GlookoV3Series? Series { get; set; }
}

/// <summary>
///     All series returned by the v3 graph/data endpoint
/// </summary>
public class GlookoV3Series
{
    // CGM readings (pre-categorized by threshold)
    [JsonPropertyName("cgmHigh")] public GlookoV3GlucoseDataPoint[]? CgmHigh { get; set; }

    [JsonPropertyName("cgmNormal")] public GlookoV3GlucoseDataPoint[]? CgmNormal { get; set; }

    [JsonPropertyName("cgmLow")] public GlookoV3GlucoseDataPoint[]? CgmLow { get; set; }

    // CGM Calibrations
    [JsonPropertyName("cgmCalibrationHigh")]
    public GlookoV3GlucoseDataPoint[]? CgmCalibrationHigh { get; set; }

    [JsonPropertyName("cgmCalibrationNormal")]
    public GlookoV3GlucoseDataPoint[]? CgmCalibrationNormal { get; set; }

    [JsonPropertyName("cgmCalibrationLow")]
    public GlookoV3GlucoseDataPoint[]? CgmCalibrationLow { get; set; }

    // BGM readings
    [JsonPropertyName("bgHigh")] public GlookoV3GlucoseDataPoint[]? BgHigh { get; set; }

    [JsonPropertyName("bgNormal")] public GlookoV3GlucoseDataPoint[]? BgNormal { get; set; }

    [JsonPropertyName("bgLow")] public GlookoV3GlucoseDataPoint[]? BgLow { get; set; }

    [JsonPropertyName("bgAbove400")] public GlookoV3GlucoseDataPoint[]? BgAbove400 { get; set; }

    // Boluses
    [JsonPropertyName("automaticBolus")] public GlookoV3BolusDataPoint[]? AutomaticBolus { get; set; }

    [JsonPropertyName("deliveredBolus")] public GlookoV3BolusDataPoint[]? DeliveredBolus { get; set; }

    [JsonPropertyName("injectionBolus")] public GlookoV3BolusDataPoint[]? InjectionBolus { get; set; }

    [JsonPropertyName("extendedBolusStep")]
    public GlookoV3BolusDataPoint[]? ExtendedBolusStep { get; set; }

    // Insulin by type
    [JsonPropertyName("gkInsulinBasal")] public GlookoV3InsulinDataPoint[]? GkInsulinBasal { get; set; }

    [JsonPropertyName("gkInsulinBolus")] public GlookoV3InsulinDataPoint[]? GkInsulinBolus { get; set; }

    [JsonPropertyName("gkInsulinOther")] public GlookoV3InsulinDataPoint[]? GkInsulinOther { get; set; }

    [JsonPropertyName("gkInsulinPremixed")]
    public GlookoV3InsulinDataPoint[]? GkInsulinPremixed { get; set; }

    // Basals
    [JsonPropertyName("scheduledBasal")] public GlookoV3BasalDataPoint[]? ScheduledBasal { get; set; }

    [JsonPropertyName("temporaryBasal")] public GlookoV3BasalDataPoint[]? TemporaryBasal { get; set; }

    [JsonPropertyName("suspendBasal")] public GlookoV3BasalDataPoint[]? SuspendBasal { get; set; }

    // Carbs
    [JsonPropertyName("carbAll")] public GlookoV3CarbDataPoint[]? CarbAll { get; set; }

    // Alarms and Alerts
    [JsonPropertyName("pumpAlarm")] public GlookoV3AlarmDataPoint[]? PumpAlarm { get; set; }

    // Profile Changes
    [JsonPropertyName("profileChange")] public GlookoV3ProfileChangeDataPoint[]? ProfileChange { get; set; }

    // Consumable Changes
    [JsonPropertyName("reservoirChange")] public GlookoV3ConsumableDataPoint[]? ReservoirChange { get; set; }

    [JsonPropertyName("setSiteChange")] public GlookoV3ConsumableDataPoint[]? SetSiteChange { get; set; }

    // LGS/PLGS events (Low Glucose Suspend / Predictive LGS)
    [JsonPropertyName("lgsPlgs")] public GlookoV3LgsPlgsDataPoint[]? LgsPlgs { get; set; }

    // Target ranges
    [JsonPropertyName("bgTargets")] public GlookoV3TargetDataPoint[]? BgTargets { get; set; }
}

/// <summary>
///     Base data point with common fields for all v3 series
/// </summary>
public abstract class GlookoV3DataPointBase
{
    /// <summary>
    ///     Unix timestamp in seconds
    /// </summary>
    [JsonPropertyName("x")]
    public long X { get; set; }

    /// <summary>
    ///     ISO 8601 timestamp
    /// </summary>
    [JsonPropertyName("timestamp")]
    public string Timestamp { get; set; } = string.Empty;
}

/// <summary>
///     Glucose reading data point (CGM or BGM)
/// </summary>
public class GlookoV3GlucoseDataPoint : GlookoV3DataPointBase
{
    /// <summary>
    ///     Glucose value in user's preferred units (check meterUnits in user profile)
    /// </summary>
    [JsonPropertyName("y")]
    public double Y { get; set; }

    /// <summary>
    ///     Meal tag association
    /// </summary>
    [JsonPropertyName("mealTag")]
    public string? MealTag { get; set; }

    /// <summary>
    ///     Internal Glooko value (scaled)
    /// </summary>
    [JsonPropertyName("value")]
    public long Value { get; set; }

    /// <summary>
    ///     Whether this value was interpolated/calculated
    /// </summary>
    [JsonPropertyName("calculated")]
    public bool Calculated { get; set; }
}

/// <summary>
///     Bolus data point
/// </summary>
public class GlookoV3BolusDataPoint : GlookoV3DataPointBase
{
    /// <summary>
    ///     Insulin units delivered
    /// </summary>
    [JsonPropertyName("y")]
    public double Y { get; set; }

    /// <summary>
    ///     Bolus type label
    /// </summary>
    [JsonPropertyName("label")]
    public string? Label { get; set; }

    /// <summary>
    ///     Additional bolus data
    /// </summary>
    [JsonPropertyName("data")]
    public GlookoV3BolusData? Data { get; set; }
}

/// <summary>
///     Additional bolus data
/// </summary>
public class GlookoV3BolusData
{
    [JsonPropertyName("programmedUnits")] public double? ProgrammedUnits { get; set; }

    [JsonPropertyName("deliveredUnits")] public double? DeliveredUnits { get; set; }

    [JsonPropertyName("carbsInput")] public double? CarbsInput { get; set; }

    [JsonPropertyName("bgInput")] public double? BgInput { get; set; }

    [JsonPropertyName("correctionUnits")] public double? CorrectionUnits { get; set; }

    [JsonPropertyName("foodUnits")] public double? FoodUnits { get; set; }
}

/// <summary>
///     Insulin data point (by type)
/// </summary>
public class GlookoV3InsulinDataPoint : GlookoV3DataPointBase
{
    [JsonPropertyName("y")] public double? Y { get; set; }

    [JsonPropertyName("label")] public string? Label { get; set; }
}

/// <summary>
///     Basal data point
/// </summary>
public class GlookoV3BasalDataPoint : GlookoV3DataPointBase
{
    /// <summary>
    ///     Basal rate (U/hr)
    /// </summary>
    [JsonPropertyName("y")]
    public double? Y { get; set; }

    /// <summary>
    ///     Duration in seconds
    /// </summary>
    [JsonPropertyName("duration")]
    public int? Duration { get; set; }

    [JsonPropertyName("label")] public string? Label { get; set; }
}

/// <summary>
///     Carb data point
/// </summary>
public class GlookoV3CarbDataPoint : GlookoV3DataPointBase
{
    /// <summary>
    ///     Y value for graphing (normalized to 50, do NOT use for actual carbs)
    /// </summary>
    [JsonPropertyName("y")]
    public double? Y { get; set; }

    /// <summary>
    ///     Original carb value in grams (use this for actual carbs)
    /// </summary>
    [JsonPropertyName("yOrig")]
    public double? YOrig { get; set; }

    /// <summary>
    ///     Alternative carb value field (use as fallback if yOrig is null)
    /// </summary>
    [JsonPropertyName("carbs")]
    public double? Carbs { get; set; }

    /// <summary>
    ///     Gets the actual carb value, preferring yOrig, then carbs
    /// </summary>
    public double? ActualCarbs => YOrig ?? Carbs;

    [JsonPropertyName("mealTag")] public string? MealTag { get; set; }

    /// <summary>
    ///     Name/source of the carb entry
    /// </summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }
}

/// <summary>
///     Pump alarm data point
/// </summary>
public class GlookoV3AlarmDataPoint : GlookoV3DataPointBase
{
    [JsonPropertyName("label")] public string? Label { get; set; }

    [JsonPropertyName("alarmType")] public string? AlarmType { get; set; }

    [JsonPropertyName("data")] public GlookoV3AlarmData? Data { get; set; }
}

/// <summary>
///     Additional alarm data
/// </summary>
public class GlookoV3AlarmData
{
    [JsonPropertyName("alarmCode")] public string? AlarmCode { get; set; }

    [JsonPropertyName("alarmDescription")] public string? AlarmDescription { get; set; }
}

/// <summary>
///     Profile change data point
/// </summary>
public class GlookoV3ProfileChangeDataPoint : GlookoV3DataPointBase
{
    [JsonPropertyName("label")] public string? Label { get; set; }

    [JsonPropertyName("profileName")] public string? ProfileName { get; set; }
}

/// <summary>
///     Consumable change data point (reservoir, infusion set)
/// </summary>
public class GlookoV3ConsumableDataPoint : GlookoV3DataPointBase
{
    [JsonPropertyName("label")] public string? Label { get; set; }

    [JsonPropertyName("data")] public GlookoV3ConsumableData? Data { get; set; }
}

/// <summary>
///     Additional consumable data
/// </summary>
public class GlookoV3ConsumableData
{
    [JsonPropertyName("primeUnits")] public double? PrimeUnits { get; set; }

    [JsonPropertyName("fillUnits")] public double? FillUnits { get; set; }
}

/// <summary>
///     LGS/PLGS event data point
/// </summary>
public class GlookoV3LgsPlgsDataPoint : GlookoV3DataPointBase
{
    [JsonPropertyName("label")] public string? Label { get; set; }

    [JsonPropertyName("eventType")] public string? EventType { get; set; }

    [JsonPropertyName("duration")] public int? Duration { get; set; }
}

/// <summary>
///     Target range data point
/// </summary>
public class GlookoV3TargetDataPoint : GlookoV3DataPointBase
{
    [JsonPropertyName("low")] public double? Low { get; set; }

    [JsonPropertyName("high")] public double? High { get; set; }
}