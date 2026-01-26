using System.Text.Json.Serialization;

namespace Nocturne.Core.Models.Configuration;

/// <summary>
/// Complete UI settings configuration that can be served to frontend clients.
/// This model aggregates all settings pages data - devices, algorithm, features, notifications, and services.
/// In demo mode, these are generated from demo configuration; in production, they come from the database.
/// Note: Therapy settings are managed via Nightscout Profiles (/api/v1/profile).
/// </summary>
public class UISettingsConfiguration
{
    /// <summary>
    /// Device settings including connected devices and device preferences
    /// </summary>
    [JsonPropertyName("devices")]
    public DeviceSettings Devices { get; set; } = new();

    /// <summary>
    /// Algorithm settings including prediction, autosens, and loop configuration
    /// </summary>
    [JsonPropertyName("algorithm")]
    public AlgorithmSettings Algorithm { get; set; } = new();

    /// <summary>
    /// Feature settings including display preferences, plugins, and dashboard widgets
    /// </summary>
    [JsonPropertyName("features")]
    public FeatureSettings Features { get; set; } = new();

    /// <summary>
    /// Notification settings including alarms, quiet hours, and notification channels
    /// </summary>
    [JsonPropertyName("notifications")]
    public NotificationSettings Notifications { get; set; } = new();

    /// <summary>
    /// Services settings including connected services/connectors and sync preferences
    /// </summary>
    [JsonPropertyName("services")]
    public ServicesSettings Services { get; set; } = new();
}

#region Device Settings

/// <summary>
/// Settings for connected devices
/// </summary>
public class DeviceSettings
{
    [JsonPropertyName("connectedDevices")]
    public List<ConnectedDevice> ConnectedDevices { get; set; } = new();

    [JsonPropertyName("autoConnect")]
    public bool AutoConnect { get; set; } = true;

    [JsonPropertyName("showRawData")]
    public bool ShowRawData { get; set; }

    [JsonPropertyName("uploadEnabled")]
    public bool UploadEnabled { get; set; } = true;

    [JsonPropertyName("cgmConfiguration")]
    public CgmConfiguration CgmConfiguration { get; set; } = new();
}

public class ConnectedDevice
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty; // "cgm", "pump", "meter"

    [JsonPropertyName("status")]
    public string Status { get; set; } = "disconnected"; // "connected", "disconnected", "error"

    [JsonPropertyName("battery")]
    public int? Battery { get; set; }

    [JsonPropertyName("lastSync")]
    public DateTimeOffset? LastSync { get; set; }

    [JsonPropertyName("serialNumber")]
    public string? SerialNumber { get; set; }
}

public class CgmConfiguration
{
    [JsonPropertyName("dataSourcePriority")]
    public string DataSourcePriority { get; set; } = "cgm"; // "cgm", "meter", "average"

    [JsonPropertyName("sensorWarmupHours")]
    public int SensorWarmupHours { get; set; } = 2;
}

#endregion

#region Algorithm Settings

/// <summary>
/// Algorithm settings for predictions, autosens, and closed loop
/// </summary>
public class AlgorithmSettings
{
    [JsonPropertyName("prediction")]
    public PredictionSettings Prediction { get; set; } = new();

    [JsonPropertyName("autosens")]
    public AutosensSettings Autosens { get; set; } = new();

    [JsonPropertyName("carbAbsorption")]
    public CarbAbsorptionSettings CarbAbsorption { get; set; } = new();

    [JsonPropertyName("loop")]
    public LoopSettings Loop { get; set; } = new();

    [JsonPropertyName("safetyLimits")]
    public SafetyLimits SafetyLimits { get; set; } = new();
}

public class PredictionSettings
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("minutes")]
    public int Minutes { get; set; } = 30;

    [JsonPropertyName("model")]
    public string Model { get; set; } = "ar2"; // "ar2", "linear", "iob", "cob", "uam"
}

public class AutosensSettings
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("min")]
    public double Min { get; set; } = 0.7;

    [JsonPropertyName("max")]
    public double Max { get; set; } = 1.2;
}

public class CarbAbsorptionSettings
{
    [JsonPropertyName("defaultMinutes")]
    public int DefaultMinutes { get; set; } = 30;

    [JsonPropertyName("minRateGramsPerHour")]
    public int MinRateGramsPerHour { get; set; } = 4;
}

public class LoopSettings
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("mode")]
    public string Mode { get; set; } = "open"; // "open", "closed"

    [JsonPropertyName("maxBasalRate")]
    public double MaxBasalRate { get; set; } = 4.0;

    [JsonPropertyName("maxBolus")]
    public double MaxBolus { get; set; } = 10.0;

    [JsonPropertyName("smbEnabled")]
    public bool SmbEnabled { get; set; }

    [JsonPropertyName("uamEnabled")]
    public bool UamEnabled { get; set; }
}

public class SafetyLimits
{
    [JsonPropertyName("maxIOB")]
    public double MaxIOB { get; set; } = 10.0;

    [JsonPropertyName("maxDailyBasalMultiplier")]
    public double MaxDailyBasalMultiplier { get; set; } = 3.0;
}

#endregion

#region Feature Settings

/// <summary>
/// Feature settings including display preferences and plugins
/// </summary>
public class FeatureSettings
{
    [JsonPropertyName("display")]
    public DisplaySettings Display { get; set; } = new();

    /// <summary>
    /// Dashboard widget configurations. Array position determines display order within each category.
    /// </summary>
    [JsonPropertyName("widgets")]
    public List<WidgetConfig> Widgets { get; set; } = GetDefaultWidgets();

    [JsonPropertyName("plugins")]
    public Dictionary<string, PluginSettings> Plugins { get; set; } = new();

    [JsonPropertyName("battery")]
    public BatteryDisplaySettings Battery { get; set; } = new();

    /// <summary>
    /// Settings for displaying tracker age pills on the dashboard
    /// </summary>
    [JsonPropertyName("trackerPills")]
    public TrackerPillsSettings TrackerPills { get; set; } = new();

    private static List<WidgetConfig> GetDefaultWidgets() =>
    [
        // Top widgets (widget grid)
        new() { Id = WidgetId.BgDelta, Enabled = true, Placement = WidgetPlacement.Top },
        new() { Id = WidgetId.LastUpdated, Enabled = true, Placement = WidgetPlacement.Top },
        new() { Id = WidgetId.ConnectionStatus, Enabled = true, Placement = WidgetPlacement.Top },
        new() { Id = WidgetId.Meals, Enabled = false, Placement = WidgetPlacement.Top },
        new() { Id = WidgetId.Trackers, Enabled = false, Placement = WidgetPlacement.Top },
        new() { Id = WidgetId.TirChart, Enabled = false, Placement = WidgetPlacement.Top },
        new() { Id = WidgetId.DailySummary, Enabled = false, Placement = WidgetPlacement.Top },
        // Main sections
        new() { Id = WidgetId.GlucoseChart, Enabled = true, Placement = WidgetPlacement.Main },
        new() { Id = WidgetId.Statistics, Enabled = true, Placement = WidgetPlacement.Main },
        new() { Id = WidgetId.Predictions, Enabled = true, Placement = WidgetPlacement.Main },
        new() { Id = WidgetId.DailyStats, Enabled = true, Placement = WidgetPlacement.Main },
        new() { Id = WidgetId.Treatments, Enabled = true, Placement = WidgetPlacement.Main },
        new() { Id = WidgetId.Agp, Enabled = false, Placement = WidgetPlacement.Main },
        new() { Id = WidgetId.BatteryStatus, Enabled = true, Placement = WidgetPlacement.Main },
    ];
}

public class DisplaySettings
{
    [JsonPropertyName("nightMode")]
    public bool NightMode { get; set; }

    [JsonPropertyName("theme")]
    public string Theme { get; set; } = "system";

    [JsonPropertyName("timeFormat")]
    public string TimeFormat { get; set; } = "12";

    [JsonPropertyName("units")]
    public string Units { get; set; } = "mg/dl";

    [JsonPropertyName("showRawBG")]
    public bool ShowRawBG { get; set; }

    [JsonPropertyName("focusHours")]
    public int FocusHours { get; set; } = 3;
}

/// <summary>
/// Available widget types for the dashboard.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WidgetId
{
    // Top widgets (widget grid)
    BgDelta,
    LastUpdated,
    ConnectionStatus,
    Meals,
    Trackers,
    TirChart,
    DailySummary,
    Clock,
    Tdd,

    // Main sections
    GlucoseChart,
    Statistics,
    Treatments,
    Predictions,
    DailyStats,
    Agp,
    BatteryStatus
}

/// <summary>
/// Widget placement determines where the widget is displayed.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WidgetPlacement
{
    /// <summary>
    /// Top widget grid (small cards above the main chart)
    /// </summary>
    Top,

    /// <summary>
    /// Main dashboard sections (larger components)
    /// </summary>
    Main
}

/// <summary>
/// Widget size variants for layout.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WidgetSize
{
    Small,
    Medium,
    Large
}

/// <summary>
/// Widget UI category for grouping in settings.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WidgetUICategory
{
    Glucose,
    Meals,
    Device,
    Status
}

/// <summary>
/// Widget definition with metadata for UI display.
/// Served from the API so frontend doesn't need to maintain widget definitions.
/// </summary>
public class WidgetDefinition
{
    [JsonPropertyName("id")]
    public WidgetId Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("defaultEnabled")]
    public bool DefaultEnabled { get; set; } = true;

    /// <summary>
    /// Icon name (e.g., "TrendingUp", "Clock") - frontend maps to actual icon component
    /// </summary>
    [JsonPropertyName("icon")]
    public string Icon { get; set; } = string.Empty;

    /// <summary>
    /// UI category for grouping in settings
    /// </summary>
    [JsonPropertyName("uiCategory")]
    public WidgetUICategory UICategory { get; set; }

    /// <summary>
    /// Where the widget is displayed (top grid or main section)
    /// </summary>
    [JsonPropertyName("placement")]
    public WidgetPlacement Placement { get; set; }
}

/// <summary>
/// Configuration for a single dashboard widget.
/// Array position determines display order within each placement.
/// </summary>
public class WidgetConfig
{
    [JsonPropertyName("id")]
    public WidgetId Id { get; set; }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("placement")]
    public WidgetPlacement Placement { get; set; } = WidgetPlacement.Main;

    [JsonPropertyName("size")]
    public WidgetSize? Size { get; set; }

    /// <summary>
    /// Widget-specific settings (future extensibility)
    /// </summary>
    [JsonPropertyName("settings")]
    public Dictionary<string, object>? Settings { get; set; }
}

/// <summary>
/// Battery display settings for controlling how battery information is shown
/// </summary>
public class BatteryDisplaySettings
{
    /// <summary>
    /// Battery level at which to show a warning (yellow indicator)
    /// </summary>
    [JsonPropertyName("warnThreshold")]
    public int WarnThreshold { get; set; } = 30;

    /// <summary>
    /// Battery level at which to show urgent warning (red indicator)
    /// </summary>
    [JsonPropertyName("urgentThreshold")]
    public int UrgentThreshold { get; set; } = 20;

    /// <summary>
    /// Whether to enable battery low alerts
    /// </summary>
    [JsonPropertyName("enableAlerts")]
    public bool EnableAlerts { get; set; } = true;

    /// <summary>
    /// How many minutes of battery history to consider when determining status
    /// </summary>
    [JsonPropertyName("recentMinutes")]
    public int RecentMinutes { get; set; } = 30;

    /// <summary>
    /// Whether to show voltage in addition to percentage (when available)
    /// </summary>
    [JsonPropertyName("showVoltage")]
    public bool ShowVoltage { get; set; } = false;

    /// <summary>
    /// Whether to show statistics in the battery card (charge duration, etc.)
    /// </summary>
    [JsonPropertyName("showStatistics")]
    public bool ShowStatistics { get; set; } = true;
}

public class PluginSettings
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;
}

/// <summary>
/// Settings for displaying tracker age pills on the dashboard
/// </summary>
public class TrackerPillsSettings
{
    /// <summary>
    /// Whether to show tracker pills on the dashboard
    /// </summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Visibility threshold for tracker pills: "always", "info", "warn", "hazard", "urgent"
    /// Only shows pills at or above the specified notification level
    /// </summary>
    [JsonPropertyName("visibility")]
    public string Visibility { get; set; } = "always";
}

#endregion

#region Notification Settings

/// <summary>
/// Notification settings using the modern xDrip+-style alarm profile configuration.
/// This class now directly wraps UserAlarmConfiguration for full customization.
/// </summary>
public class NotificationSettings
{
    /// <summary>
    /// xDrip+-style alarm configuration stored as JSONB.
    /// Contains all alarm profiles, quiet hours, channels, and emergency contacts.
    /// </summary>
    [JsonPropertyName("alarmConfiguration")]
    public UserAlarmConfiguration AlarmConfiguration { get; set; } = new();
}

#endregion


#region Services Settings

/// <summary>
/// Services settings including connected services/connectors
/// </summary>
public class ServicesSettings
{
    [JsonPropertyName("connectedServices")]
    public List<ConnectedService> ConnectedServices { get; set; } = new();

    [JsonPropertyName("availableServices")]
    public List<AvailableService> AvailableServices { get; set; } = new();

    [JsonPropertyName("syncSettings")]
    public SyncSettings SyncSettings { get; set; } = new();
}

public class ConnectedService
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty; // "cgm", "pump", "data", "food"

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = "disconnected"; // "connected", "disconnected", "error", "syncing"

    [JsonPropertyName("lastSync")]
    public DateTimeOffset? LastSync { get; set; }

    [JsonPropertyName("icon")]
    public string Icon { get; set; } = string.Empty;

    [JsonPropertyName("configured")]
    public bool Configured { get; set; }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }
}

public class AvailableService
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("icon")]
    public string Icon { get; set; } = string.Empty;
}

public class SyncSettings
{
    [JsonPropertyName("autoSync")]
    public bool AutoSync { get; set; } = true;

    [JsonPropertyName("syncOnAppOpen")]
    public bool SyncOnAppOpen { get; set; } = true;

    [JsonPropertyName("backgroundRefresh")]
    public bool BackgroundRefresh { get; set; } = true;
}

#endregion
