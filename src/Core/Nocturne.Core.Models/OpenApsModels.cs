using System.Text.Json.Serialization;

namespace Nocturne.Core.Models;

/// <summary>
/// OpenAPS plugin preferences following legacy getPrefs() structure
/// Maintains 1:1 compatibility with the original implementation
/// </summary>
public class OpenApsPreferences
{
    /// <summary>
    /// Fields to display in main view
    /// </summary>
    public List<string> Fields { get; set; } =
        new() { "status-symbol", "status-label", "iob", "meal-assist", "rssi" };

    /// <summary>
    /// Fields to display in retro view
    /// </summary>
    public List<string> RetroFields { get; set; } =
        new() { "status-symbol", "status-label", "iob", "meal-assist", "rssi" };

    /// <summary>
    /// Warning threshold in minutes (default: 30)
    /// </summary>
    public int Warn { get; set; } = 30;

    /// <summary>
    /// Urgent threshold in minutes (default: 60)
    /// </summary>
    public int Urgent { get; set; } = 60;

    /// <summary>
    /// Whether alerts are enabled
    /// </summary>
    public bool EnableAlerts { get; set; }

    /// <summary>
    /// Whether to color prediction lines
    /// </summary>
    public bool ColorPredictionLines { get; set; } = true;
}

/// <summary>
/// OpenAPS loop status result from analyzeData()
/// Maintains 1:1 compatibility with the legacy implementation
/// </summary>
public class OpenApsAnalysisResult
{
    /// <summary>
    /// Dictionary of seen devices by URI
    /// </summary>
    public Dictionary<string, OpenApsDevice> SeenDevices { get; set; } = new();

    /// <summary>
    /// Last enacted command
    /// </summary>
    public OpenApsCommand? LastEnacted { get; set; }

    /// <summary>
    /// Last command that was not enacted
    /// </summary>
    public OpenApsCommand? LastNotEnacted { get; set; }

    /// <summary>
    /// Last suggested command
    /// </summary>
    public OpenApsCommand? LastSuggested { get; set; }

    /// <summary>
    /// Last IOB data
    /// </summary>
    public OpenApsIob? LastIob { get; set; }

    /// <summary>
    /// Last MM tune data
    /// </summary>
    public OpenApsMmTune? LastMmTune { get; set; }

    /// <summary>
    /// Last prediction blood glucose data
    /// </summary>
    public OpenApsPredBg? LastPredBgs { get; set; }

    /// <summary>
    /// Timestamp of last loop moment
    /// </summary>
    public DateTime? LastLoopMoment { get; set; }

    /// <summary>
    /// Last eventual BG value
    /// </summary>
    public double? LastEventualBg { get; set; }

    /// <summary>
    /// Overall loop status
    /// </summary>
    public OpenApsLoopStatus Status { get; set; } = new();
}

/// <summary>
/// OpenAPS device information
/// </summary>
public class OpenApsDevice
{
    /// <summary>
    /// Device display name
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Device URI
    /// </summary>
    public string Uri { get; set; } = string.Empty;

    /// <summary>
    /// Device status
    /// </summary>
    public OpenApsLoopStatus? Status { get; set; }

    /// <summary>
    /// Last status update time
    /// </summary>
    public DateTime? When { get; set; }

    /// <summary>
    /// MM tune information
    /// </summary>
    public OpenApsMmTune? MmTune { get; set; }
}

/// <summary>
/// OpenAPS loop status
/// </summary>
public class OpenApsLoopStatus
{
    /// <summary>
    /// Status symbol (⌁, ◉, x, ⚠)
    /// </summary>
    public string Symbol { get; set; } = "⚠";

    /// <summary>
    /// Status code (enacted, suggested, notenacted, warning)
    /// </summary>
    public string Code { get; set; } = "warning";

    /// <summary>
    /// Status label (Enacted, Suggested, Not Enacted, Warning)
    /// </summary>
    public string Label { get; set; } = "Warning";
}

/// <summary>
/// OpenAPS command (enacted/suggested)
/// </summary>
public class OpenApsCommand
{
    /// <summary>
    /// Blood glucose reading
    /// </summary>
    [JsonPropertyName("bg")]
    public double? Bg { get; set; }

    /// <summary>
    /// Temperature type (absolute, percent)
    /// </summary>
    [JsonPropertyName("temp")]
    public string? Temp { get; set; }

    /// <summary>
    /// Snooze BG value
    /// </summary>
    [JsonPropertyName("snoozeBG")]
    public double? SnoozeBg { get; set; }

    /// <summary>
    /// Timestamp of the command
    /// </summary>
    [JsonPropertyName("timestamp")]
    public string? Timestamp { get; set; }

    /// <summary>
    /// Timestamp in milliseconds
    /// </summary>
    [JsonPropertyName("mills")]
    public long? Mills { get; set; }

    /// <summary>
    /// Basal rate
    /// </summary>
    [JsonPropertyName("rate")]
    public double? Rate { get; set; }

    /// <summary>
    /// Reason for the command
    /// </summary>
    [JsonPropertyName("reason")]
    public string? Reason { get; set; }

    /// <summary>
    /// Eventual BG prediction
    /// </summary>
    [JsonPropertyName("eventualBG")]
    public double? EventualBg { get; set; }

    /// <summary>
    /// Duration in minutes
    /// </summary>
    [JsonPropertyName("duration")]
    public int? Duration { get; set; }

    /// <summary>
    /// BG tick direction
    /// </summary>
    [JsonPropertyName("tick")]
    public string? Tick { get; set; }

    /// <summary>
    /// Whether command was received (enacted commands only)
    /// </summary>
    [JsonPropertyName("received")]
    public bool? Received { get; set; }

    /// <summary>
    /// Legacy spelling - whether command was received
    /// </summary>
    [JsonPropertyName("recieved")]
    public bool? Recieved { get; set; }

    /// <summary>
    /// Meal assist information
    /// </summary>
    [JsonPropertyName("mealAssist")]
    public string? MealAssist { get; set; }

    /// <summary>
    /// Prediction blood glucose data
    /// </summary>
    [JsonPropertyName("predBGs")]
    public OpenApsPredBg? PredBgs { get; set; }

    /// <summary>
    /// DateTime moment for calculations
    /// </summary>
    public DateTime? Moment { get; set; }
}

/// <summary>
/// OpenAPS IOB data
/// </summary>
public class OpenApsIob
{
    /// <summary>
    /// Timestamp
    /// </summary>
    [JsonPropertyName("timestamp")]
    public string? Timestamp { get; set; }

    /// <summary>
    /// Bolus IOB
    /// </summary>
    [JsonPropertyName("bolusiob")]
    public double? BolusIob { get; set; }

    /// <summary>
    /// Total IOB
    /// </summary>
    [JsonPropertyName("iob")]
    public double? Iob { get; set; }

    /// <summary>
    /// Activity level
    /// </summary>
    [JsonPropertyName("activity")]
    public double? Activity { get; set; }

    /// <summary>
    /// Basal IOB
    /// </summary>
    [JsonPropertyName("basaliob")]
    public double? BasalIob { get; set; }

    /// <summary>
    /// Net basal insulin
    /// </summary>
    [JsonPropertyName("netbasalinsulin")]
    public double? NetBasalInsulin { get; set; }

    /// <summary>
    /// High temp insulin
    /// </summary>
    [JsonPropertyName("hightempinsulin")]
    public double? HighTempInsulin { get; set; }

    /// <summary>
    /// Micro bolus insulin
    /// </summary>
    [JsonPropertyName("microBolusInsulin")]
    public double? MicroBolusInsulin { get; set; }

    /// <summary>
    /// Micro bolus IOB
    /// </summary>
    [JsonPropertyName("microBolusIOB")]
    public double? MicroBolusIob { get; set; }

    /// <summary>
    /// Last bolus time in milliseconds
    /// </summary>
    [JsonPropertyName("lastBolusTime")]
    public long? LastBolusTime { get; set; }

    /// <summary>
    /// Legacy time field
    /// </summary>
    [JsonPropertyName("time")]
    public string? Time { get; set; }

    /// <summary>
    /// DateTime moment for calculations
    /// </summary>
    public DateTime? Moment { get; set; }
}

/// <summary>
/// OpenAPS prediction blood glucose data
/// </summary>
public class OpenApsPredBg
{
    /// <summary>
    /// IOB predictions
    /// </summary>
    [JsonPropertyName("IOB")]
    public List<double>? Iob { get; set; }

    /// <summary>
    /// COB predictions
    /// </summary>
    [JsonPropertyName("COB")]
    public List<double>? Cob { get; set; }

    /// <summary>
    /// ACOB predictions
    /// </summary>
    [JsonPropertyName("aCOB")]
    public List<double>? ACob { get; set; }

    /// <summary>
    /// Zero temp predictions
    /// </summary>
    [JsonPropertyName("ZT")]
    public List<double>? Zt { get; set; }

    /// <summary>
    /// UAM predictions
    /// </summary>
    [JsonPropertyName("UAM")]
    public List<double>? Uam { get; set; }

    /// <summary>
    /// Generic values (for backwards compatibility)
    /// </summary>
    [JsonPropertyName("values")]
    public List<double>? Values { get; set; }

    /// <summary>
    /// DateTime moment for calculations
    /// </summary>
    public DateTime? Moment { get; set; }
}

/// <summary>
/// OpenAPS MM tune data
/// </summary>
public class OpenApsMmTune
{
    /// <summary>
    /// Scan details array
    /// </summary>
    [JsonPropertyName("scanDetails")]
    public List<List<object>>? ScanDetails { get; set; }

    /// <summary>
    /// Set frequency
    /// </summary>
    [JsonPropertyName("setFreq")]
    public double? SetFreq { get; set; }

    /// <summary>
    /// Timestamp
    /// </summary>
    [JsonPropertyName("timestamp")]
    public string? Timestamp { get; set; }

    /// <summary>
    /// Whether default was used
    /// </summary>
    [JsonPropertyName("usedDefault")]
    public bool? UsedDefault { get; set; }

    /// <summary>
    /// DateTime moment for calculations
    /// </summary>
    public DateTime? Moment { get; set; }
}

/// <summary>
/// OpenAPS forecast point for visualization
/// </summary>
public class OpenApsForecastPoint
{
    /// <summary>
    /// X coordinate (time offset)
    /// </summary>
    public double X { get; set; }

    /// <summary>
    /// Y coordinate (blood glucose value)
    /// </summary>
    public double Y { get; set; }

    /// <summary>
    /// Color for this point
    /// </summary>
    public string Color { get; set; } = string.Empty;

    /// <summary>
    /// Forecast type (IOB, COB, etc.)
    /// </summary>
    public string Type { get; set; } = string.Empty;
}

/// <summary>
/// OpenAPS event information for visualization
/// </summary>
public class OpenApsEvent
{
    /// <summary>
    /// Event timestamp
    /// </summary>
    public DateTime Time { get; set; }

    /// <summary>
    /// Event value/description
    /// </summary>
    public string Value { get; set; } = string.Empty;

    /// <summary>
    /// Event label for display
    /// </summary>
    public string Label { get; set; } = string.Empty;
}
