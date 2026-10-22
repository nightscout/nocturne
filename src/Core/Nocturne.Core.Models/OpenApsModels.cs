using System.Text.Json.Serialization;
using Nocturne.Core.Models.Serializers;

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

    public bool EnableAlerts { get; set; }

    public bool ColorPredictionLines { get; set; } = true;
}

/// <summary>
/// OpenAPS loop status result from analyzeData().
/// Maintains 1:1 compatibility with the legacy implementation.
/// </summary>
/// <seealso cref="OpenApsCommand"/>
/// <seealso cref="OpenApsIob"/>
/// <seealso cref="OpenApsPredBg"/>
/// <seealso cref="OpenApsLoopStatus"/>
public class OpenApsAnalysisResult
{
    public Dictionary<string, OpenApsDevice> SeenDevices { get; set; } = new();

    public OpenApsCommand? LastEnacted { get; set; }

    /// <summary>
    /// Last command that was not enacted
    /// </summary>
    public OpenApsCommand? LastNotEnacted { get; set; }

    public OpenApsCommand? LastSuggested { get; set; }

    public OpenApsIob? LastIob { get; set; }

    public OpenApsMmTune? LastMmTune { get; set; }

    /// <summary>
    /// Last prediction blood glucose data
    /// </summary>
    public OpenApsPredBg? LastPredBgs { get; set; }

    public DateTime? LastLoopMoment { get; set; }

    public double? LastEventualBg { get; set; }

    public OpenApsLoopStatus Status { get; set; } = new();
}

public class OpenApsDevice
{
    public string Name { get; set; } = string.Empty;

    public string Uri { get; set; } = string.Empty;

    public OpenApsLoopStatus? Status { get; set; }

    /// <summary>
    /// Last status update time
    /// </summary>
    public DateTime? When { get; set; }

    public OpenApsMmTune? MmTune { get; set; }
}

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

    [JsonPropertyName("snoozeBG")]
    public double? SnoozeBg { get; set; }

    [JsonPropertyName("timestamp")]
    public string? Timestamp { get; set; }

    /// <summary>
    /// Timestamp in milliseconds
    /// </summary>
    [JsonPropertyName("mills")]
    public long? Mills { get; set; }

    [JsonPropertyName("rate")]
    public double? Rate { get; set; }

    [JsonPropertyName("reason")]
    public string? Reason { get; set; }

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
    [JsonConverter(typeof(FlexibleStringConverter))]
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

    [JsonPropertyName("mealAssist")]
    public string? MealAssist { get; set; }

    /// <summary>
    /// Prediction blood glucose data
    /// </summary>
    [JsonPropertyName("predBGs")]
    public OpenApsPredBg? PredBgs { get; set; }

    public DateTime? Moment { get; set; }
}

public class OpenApsIob
{
    [JsonPropertyName("timestamp")]
    public string? Timestamp { get; set; }

    [JsonPropertyName("bolusiob")]
    public double? BolusIob { get; set; }

    /// <summary>
    /// Total IOB
    /// </summary>
    [JsonPropertyName("iob")]
    public double? Iob { get; set; }

    [JsonPropertyName("activity")]
    public double? Activity { get; set; }

    [JsonPropertyName("basaliob")]
    public double? BasalIob { get; set; }

    [JsonPropertyName("netbasalinsulin")]
    public double? NetBasalInsulin { get; set; }

    [JsonPropertyName("hightempinsulin")]
    public double? HighTempInsulin { get; set; }

    [JsonPropertyName("microBolusInsulin")]
    public double? MicroBolusInsulin { get; set; }

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

    public DateTime? Moment { get; set; }
}

/// <summary>
/// OpenAPS prediction blood glucose data
/// </summary>
public class OpenApsPredBg
{
    [JsonPropertyName("IOB")]
    public List<double>? Iob { get; set; }

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

    [JsonPropertyName("UAM")]
    public List<double>? Uam { get; set; }

    /// <summary>
    /// Generic values (for backwards compatibility)
    /// </summary>
    [JsonPropertyName("values")]
    public List<double>? Values { get; set; }

    public DateTime? Moment { get; set; }
}

public class OpenApsMmTune
{
    [JsonPropertyName("scanDetails")]
    public List<List<object>>? ScanDetails { get; set; }

    [JsonPropertyName("setFreq")]
    public double? SetFreq { get; set; }

    [JsonPropertyName("timestamp")]
    public string? Timestamp { get; set; }

    /// <summary>
    /// Whether default was used
    /// </summary>
    [JsonPropertyName("usedDefault")]
    public bool? UsedDefault { get; set; }

    public DateTime? Moment { get; set; }
}

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

    public string Color { get; set; } = string.Empty;

    /// <summary>
    /// Forecast type (IOB, COB, etc.)
    /// </summary>
    public string Type { get; set; } = string.Empty;
}

public class OpenApsEvent
{
    public DateTime Time { get; set; }

    public string Value { get; set; } = string.Empty;

    public string Label { get; set; } = string.Empty;
}
