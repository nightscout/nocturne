using System.Text.Json.Serialization;

namespace Nocturne.Core.Models.Configuration;

/// <summary>
/// Complete UI settings configuration that can be served to frontend clients.
/// This model aggregates all settings pages data - devices, therapy, algorithm, features, notifications, and services.
/// In demo mode, these are generated from demo configuration; in production, they come from the database.
/// </summary>
public class UISettingsConfiguration
{
    /// <summary>
    /// Device settings including connected devices and device preferences
    /// </summary>
    [JsonPropertyName("devices")]
    public DeviceSettings Devices { get; set; } = new();

    /// <summary>
    /// Therapy settings including insulin ratios, sensitivity factors, and glucose targets
    /// </summary>
    [JsonPropertyName("therapy")]
    public TherapySettings Therapy { get; set; } = new();

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

#region Therapy Settings

/// <summary>
/// Therapy settings including profiles for insulin ratios, sensitivity, and targets
/// </summary>
public class TherapySettings
{
    [JsonPropertyName("units")]
    public string Units { get; set; } = "mg/dl";

    [JsonPropertyName("carbRatios")]
    public List<TimeBasedValue> CarbRatios { get; set; } = new();

    [JsonPropertyName("insulinSensitivity")]
    public List<TimeBasedValue> InsulinSensitivity { get; set; } = new();

    [JsonPropertyName("basalRates")]
    public List<TimeBasedValue> BasalRates { get; set; } = new();

    [JsonPropertyName("bgTargets")]
    public BgTargets BgTargets { get; set; } = new();

    [JsonPropertyName("activeInsulin")]
    public ActiveInsulinSettings ActiveInsulin { get; set; } = new();
}

public class TimeBasedValue
{
    [JsonPropertyName("time")]
    public string Time { get; set; } = "00:00";

    [JsonPropertyName("value")]
    public double Value { get; set; }
}

public class BgTargets
{
    [JsonPropertyName("targetLow")]
    public int TargetLow { get; set; } = 80;

    [JsonPropertyName("targetHigh")]
    public int TargetHigh { get; set; } = 120;

    [JsonPropertyName("urgentLow")]
    public int UrgentLow { get; set; } = 55;

    [JsonPropertyName("urgentHigh")]
    public int UrgentHigh { get; set; } = 250;
}

public class ActiveInsulinSettings
{
    [JsonPropertyName("duration")]
    public double Duration { get; set; } = 4.0;

    [JsonPropertyName("peak")]
    public int Peak { get; set; } = 75;
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

    [JsonPropertyName("dashboardWidgets")]
    public DashboardWidgets DashboardWidgets { get; set; } = new();

    [JsonPropertyName("plugins")]
    public Dictionary<string, PluginSettings> Plugins { get; set; } = new();

    [JsonPropertyName("battery")]
    public BatteryDisplaySettings Battery { get; set; } = new();
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

public class DashboardWidgets
{
    [JsonPropertyName("glucoseChart")]
    public bool GlucoseChart { get; set; } = true;

    [JsonPropertyName("statistics")]
    public bool Statistics { get; set; } = true;

    [JsonPropertyName("treatments")]
    public bool Treatments { get; set; } = true;

    [JsonPropertyName("predictions")]
    public bool Predictions { get; set; } = true;

    [JsonPropertyName("agp")]
    public bool Agp { get; set; }

    [JsonPropertyName("dailyStats")]
    public bool DailyStats { get; set; } = true;

    [JsonPropertyName("batteryStatus")]
    public bool BatteryStatus { get; set; } = true;
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

#endregion

#region Notification Settings

/// <summary>
/// Notification settings including alarms and notification channels.
/// This is the legacy format - new code should use UserAlarmConfiguration from AlarmProfileConfiguration.cs
/// which provides xDrip+-style alarm profiles with full customization.
/// </summary>
public class NotificationSettings
{
    [JsonPropertyName("alarmsEnabled")]
    public bool AlarmsEnabled { get; set; } = true;

    [JsonPropertyName("soundEnabled")]
    public bool SoundEnabled { get; set; } = true;

    [JsonPropertyName("vibrationEnabled")]
    public bool VibrationEnabled { get; set; } = true;

    [JsonPropertyName("volume")]
    public int Volume { get; set; } = 70;

    [JsonPropertyName("alarms")]
    public AlarmSettings Alarms { get; set; } = new();

    [JsonPropertyName("quietHours")]
    public QuietHoursSettings QuietHours { get; set; } = new();

    [JsonPropertyName("channels")]
    public NotificationChannels Channels { get; set; } = new();

    [JsonPropertyName("emergencyContacts")]
    public List<EmergencyContact> EmergencyContacts { get; set; } = new();

    /// <summary>
    /// New xDrip+-style alarm configuration stored as JSONB.
    /// When this is present, it takes precedence over the legacy AlarmSettings.
    /// </summary>
    [JsonPropertyName("alarmConfiguration")]
    public UserAlarmConfiguration? AlarmConfiguration { get; set; }
}

public class AlarmSettings
{
    [JsonPropertyName("urgentHigh")]
    public AlarmConfig UrgentHigh { get; set; } =
        new()
        {
            Enabled = true,
            Threshold = 250,
            Sound = "alarm-urgent",
            RepeatMinutes = 5,
            SnoozeOptions = new List<int> { 5, 10, 15, 30 },
        };

    [JsonPropertyName("high")]
    public AlarmConfig High { get; set; } =
        new()
        {
            Enabled = true,
            Threshold = 180,
            Sound = "alarm-high",
            RepeatMinutes = 15,
            SnoozeOptions = new List<int> { 15, 30, 60 },
        };

    [JsonPropertyName("low")]
    public AlarmConfig Low { get; set; } =
        new()
        {
            Enabled = true,
            Threshold = 70,
            Sound = "alarm-low",
            RepeatMinutes = 5,
            SnoozeOptions = new List<int> { 10, 15, 30 },
        };

    [JsonPropertyName("urgentLow")]
    public AlarmConfig UrgentLow { get; set; } =
        new()
        {
            Enabled = true,
            Threshold = 55,
            Sound = "alarm-urgent",
            RepeatMinutes = 5,
            SnoozeOptions = new List<int> { 5, 10, 15 },
        };

    [JsonPropertyName("staleData")]
    public StaleDataAlarm StaleData { get; set; } = new();
}

public class AlarmConfig
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("threshold")]
    public int Threshold { get; set; }

    [JsonPropertyName("sound")]
    public string Sound { get; set; } = "alert";

    [JsonPropertyName("repeatMinutes")]
    public int RepeatMinutes { get; set; } = 5;

    [JsonPropertyName("snoozeOptions")]
    public List<int> SnoozeOptions { get; set; } = new();
}

public class StaleDataAlarm
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("warningMinutes")]
    public int WarningMinutes { get; set; } = 15;

    [JsonPropertyName("urgentMinutes")]
    public int UrgentMinutes { get; set; } = 30;

    [JsonPropertyName("sound")]
    public string Sound { get; set; } = "alert";
}

public class QuietHoursSettings
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("startTime")]
    public string StartTime { get; set; } = "22:00";

    [JsonPropertyName("endTime")]
    public string EndTime { get; set; } = "07:00";
}

public class NotificationChannels
{
    [JsonPropertyName("push")]
    public NotificationChannel Push { get; set; } =
        new() { Enabled = true, Label = "Push Notifications" };

    [JsonPropertyName("email")]
    public NotificationChannel Email { get; set; } = new() { Enabled = false, Label = "Email" };

    [JsonPropertyName("sms")]
    public NotificationChannel Sms { get; set; } = new() { Enabled = false, Label = "SMS" };
}

public class NotificationChannel
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;
}

public class EmergencyContact
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("phone")]
    public string Phone { get; set; } = string.Empty;

    [JsonPropertyName("notifyOnUrgent")]
    public bool NotifyOnUrgent { get; set; } = true;
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
