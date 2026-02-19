using System.Text.Json.Serialization;

namespace Nocturne.Core.Models.Configuration;

/// <summary>
/// xDrip+-inspired alarm profile configuration.
/// Stored as JSONB in the database for flexibility and easy extensibility.
/// Each user can have multiple alarm profiles with complex, customizable behavior.
/// </summary>
public class AlarmProfileConfiguration
{
    /// <summary>
    /// Unique identifier for this alarm profile
    /// </summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// User-defined name for this alarm (e.g., "Urgent Low", "Nighttime High")
    /// </summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Description of what this alarm is for
    /// </summary>
    [JsonPropertyName("description")]
    public string? Description { get; set; }

    /// <summary>
    /// Whether this alarm is currently enabled
    /// </summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// The type of alarm condition to check
    /// </summary>
    [JsonPropertyName("alarmType")]
    public AlarmTriggerType AlarmType { get; set; } = AlarmTriggerType.High;

    /// <summary>
    /// Glucose threshold that triggers this alarm (in mg/dL)
    /// </summary>
    [JsonPropertyName("threshold")]
    public int Threshold { get; set; }

    /// <summary>
    /// For range alarms, the upper bound (threshold is lower bound)
    /// </summary>
    [JsonPropertyName("thresholdHigh")]
    public double? ThresholdHigh { get; set; }

    /// <summary>
    /// Lead time in minutes for forecast alerts.
    /// </summary>
    [JsonPropertyName("forecastLeadTimeMinutes")]
    public int? ForecastLeadTimeMinutes { get; set; }

    /// <summary>
    /// Delayed raise - only trigger if above/below threshold for this many minutes
    /// Prevents alarms for brief spikes/dips. Set to 0 for immediate triggering.
    /// </summary>
    [JsonPropertyName("persistenceMinutes")]
    public int PersistenceMinutes { get; set; } = 0;

    /// <summary>
    /// Audio configuration for this alarm
    /// </summary>
    [JsonPropertyName("audio")]
    public AlarmAudioSettings Audio { get; set; } = new();

    /// <summary>
    /// Vibration settings for this alarm
    /// </summary>
    [JsonPropertyName("vibration")]
    public AlarmVibrationSettings Vibration { get; set; } = new();

    /// <summary>
    /// Visual settings (screen flash, colors)
    /// </summary>
    [JsonPropertyName("visual")]
    public AlarmVisualSettings Visual { get; set; } = new();

    /// <summary>
    /// Snooze behavior configuration
    /// </summary>
    [JsonPropertyName("snooze")]
    public AlarmSnoozeSettings Snooze { get; set; } = new();

    /// <summary>
    /// Re-raise/repeat configuration for unacknowledged alarms
    /// </summary>
    [JsonPropertyName("reraise")]
    public AlarmReraiseSettings Reraise { get; set; } = new();

    /// <summary>
    /// Smart snooze - auto-extend snooze if glucose is trending in the right direction
    /// </summary>
    [JsonPropertyName("smartSnooze")]
    public SmartSnoozeSettings SmartSnooze { get; set; } = new();

    /// <summary>
    /// Time-of-day schedule - when this alarm is active
    /// </summary>
    [JsonPropertyName("schedule")]
    public AlarmScheduleSettings Schedule { get; set; } = new();

    /// <summary>
    /// Priority level for this alarm (affects notification behavior)
    /// </summary>
    [JsonPropertyName("priority")]
    public AlarmPriority Priority { get; set; } = AlarmPriority.Normal;

    /// <summary>
    /// Override quiet hours for this alarm
    /// </summary>
    [JsonPropertyName("overrideQuietHours")]
    public bool OverrideQuietHours { get; set; }

    /// <summary>
    /// Order for display in UI (lower numbers first)
    /// </summary>
    [JsonPropertyName("displayOrder")]
    public int DisplayOrder { get; set; }

    /// <summary>
    /// Timestamp when this profile was created
    /// </summary>
    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Timestamp when this profile was last modified
    /// </summary>
    [JsonPropertyName("updatedAt")]
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Types of alarm triggers
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AlarmTriggerType
{
    /// <summary>Above threshold</summary>
    High,

    /// <summary>Below threshold</summary>
    Low,

    /// <summary>Above urgent high threshold</summary>
    UrgentHigh,

    /// <summary>Below urgent low threshold</summary>
    UrgentLow,

    /// <summary>Rising too fast (delta-based)</summary>
    RisingFast,

    /// <summary>Falling too fast (delta-based)</summary>
    FallingFast,

    /// <summary>No data received for a period</summary>
    StaleData,

    /// <summary>Forecasted low glucose</summary>
    ForecastLow,

    /// <summary>Custom - uses thresholdHigh for range</summary>
    Custom,
}

/// <summary>
/// Priority levels for alarms
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AlarmPriority
{
    /// <summary>Low priority - can be silenced easily</summary>
    Low,

    /// <summary>Normal priority</summary>
    Normal,

    /// <summary>High priority - louder, more persistent</summary>
    High,

    /// <summary>Critical - overrides all quiet settings, maximum volume</summary>
    Critical,
}

/// <summary>
/// Audio settings for an alarm
/// </summary>
public class AlarmAudioSettings
{
    /// <summary>
    /// Whether sound is enabled for this alarm
    /// </summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Sound identifier - can be built-in or custom audio file reference
    /// </summary>
    [JsonPropertyName("soundId")]
    public string SoundId { get; set; } = "alarm-default";

    /// <summary>
    /// Path to custom audio file (if using custom sound)
    /// </summary>
    [JsonPropertyName("customSoundUrl")]
    public string? CustomSoundUrl { get; set; }

    /// <summary>
    /// Whether to use ascending volume (starts quiet, gets louder)
    /// </summary>
    [JsonPropertyName("ascendingVolume")]
    public bool AscendingVolume { get; set; }

    /// <summary>
    /// Starting volume percentage (0-100) when ascending volume is enabled
    /// </summary>
    [JsonPropertyName("startVolume")]
    public int StartVolume { get; set; } = 20;

    /// <summary>
    /// Maximum volume percentage (0-100)
    /// </summary>
    [JsonPropertyName("maxVolume")]
    public int MaxVolume { get; set; } = 100;

    /// <summary>
    /// Seconds to reach max volume when ascending
    /// </summary>
    [JsonPropertyName("ascendDurationSeconds")]
    public int AscendDurationSeconds { get; set; } = 30;

    /// <summary>
    /// How many times to repeat the sound (0 = until acknowledged)
    /// </summary>
    [JsonPropertyName("repeatCount")]
    public int RepeatCount { get; set; } = 0;
}

/// <summary>
/// Vibration settings for an alarm
/// </summary>
public class AlarmVibrationSettings
{
    /// <summary>
    /// Whether vibration is enabled for this alarm
    /// </summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Vibration pattern: "short", "long", "sos", "continuous", or custom pattern
    /// </summary>
    [JsonPropertyName("pattern")]
    public string Pattern { get; set; } = "short";

    /// <summary>
    /// Custom vibration pattern as array of milliseconds [vibrate, pause, vibrate, pause, ...]
    /// </summary>
    [JsonPropertyName("customPattern")]
    public List<int>? CustomPattern { get; set; }
}

/// <summary>
/// Visual settings for an alarm (screen flash, colors)
/// </summary>
public class AlarmVisualSettings
{
    /// <summary>
    /// Whether to flash the screen when alarm triggers
    /// </summary>
    [JsonPropertyName("screenFlash")]
    public bool ScreenFlash { get; set; }

    /// <summary>
    /// Color to use for screen flash (hex format or CSS variable)
    /// </summary>
    [JsonPropertyName("flashColor")]
    public string FlashColor { get; set; } = "";

    /// <summary>
    /// Flash frequency in milliseconds
    /// </summary>
    [JsonPropertyName("flashIntervalMs")]
    public int FlashIntervalMs { get; set; } = 500;

    /// <summary>
    /// Whether to show a persistent banner until acknowledged
    /// </summary>
    [JsonPropertyName("persistentBanner")]
    public bool PersistentBanner { get; set; } = true;

    /// <summary>
    /// Whether to turn on the screen when alarm triggers
    /// </summary>
    [JsonPropertyName("wakeScreen")]
    public bool WakeScreen { get; set; } = true;
}

/// <summary>
/// Snooze settings for an alarm
/// </summary>
public class AlarmSnoozeSettings
{
    /// <summary>
    /// Default snooze duration in minutes
    /// </summary>
    [JsonPropertyName("defaultMinutes")]
    public int DefaultMinutes { get; set; } = 15;

    /// <summary>
    /// Available snooze duration options in minutes
    /// </summary>
    [JsonPropertyName("options")]
    public List<int> Options { get; set; } = new() { 5, 10, 15, 30, 60 };

    /// <summary>
    /// Maximum snooze duration allowed in minutes
    /// </summary>
    [JsonPropertyName("maxMinutes")]
    public int MaxMinutes { get; set; } = 120;

    /// <summary>
    /// Maximum number of times alarm can be snoozed
    /// </summary>
    [JsonPropertyName("maxSnoozeCount")]
    public int? MaxSnoozeCount { get; set; }
}

/// <summary>
/// Settings for re-raising unacknowledged alarms
/// </summary>
public class AlarmReraiseSettings
{
    /// <summary>
    /// Whether to automatically re-raise if not acknowledged
    /// </summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Re-raise interval in minutes
    /// </summary>
    [JsonPropertyName("intervalMinutes")]
    public int IntervalMinutes { get; set; } = 5;

    /// <summary>
    /// Whether to escalate (get louder) on each re-raise
    /// </summary>
    [JsonPropertyName("escalate")]
    public bool Escalate { get; set; }

    /// <summary>
    /// Volume increase per escalation (percentage points)
    /// </summary>
    [JsonPropertyName("escalationVolumeStep")]
    public int EscalationVolumeStep { get; set; } = 20;
}

/// <summary>
/// Smart snooze settings - auto-extend snooze when trending in right direction
/// </summary>
public class SmartSnoozeSettings
{
    /// <summary>
    /// Whether smart snooze is enabled
    /// </summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    /// <summary>
    /// For high alarms: auto-extend snooze if glucose is falling
    /// For low alarms: auto-extend snooze if glucose is rising
    /// </summary>
    [JsonPropertyName("extendWhenTrendingCorrect")]
    public bool ExtendWhenTrendingCorrect { get; set; } = true;

    /// <summary>
    /// Minimum delta (mg/dL per 5 min) to consider "trending correct"
    /// </summary>
    [JsonPropertyName("minDeltaThreshold")]
    public int MinDeltaThreshold { get; set; } = 5;

    /// <summary>
    /// Extension duration in minutes when trending correct
    /// </summary>
    [JsonPropertyName("extensionMinutes")]
    public int ExtensionMinutes { get; set; } = 15;

    /// <summary>
    /// Maximum total snooze time with extensions
    /// </summary>
    [JsonPropertyName("maxTotalMinutes")]
    public int MaxTotalMinutes { get; set; } = 60;
}

/// <summary>
/// Time-of-day schedule for when alarm is active
/// </summary>
public class AlarmScheduleSettings
{
    /// <summary>
    /// Whether scheduling is enabled (if false, alarm is always active)
    /// </summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    /// <summary>
    /// Time ranges when this alarm is active
    /// </summary>
    [JsonPropertyName("activeRanges")]
    public List<TimeRange> ActiveRanges { get; set; } = new();

    /// <summary>
    /// Days of week when alarm is active (0=Sunday, 6=Saturday)
    /// If empty, active every day
    /// </summary>
    [JsonPropertyName("activeDays")]
    public List<int>? ActiveDays { get; set; }

    /// <summary>
    /// Timezone for schedule interpretation
    /// </summary>
    [JsonPropertyName("timezone")]
    public string? Timezone { get; set; }
}

/// <summary>
/// A time range for scheduling
/// </summary>
public class TimeRange
{
    /// <summary>
    /// Start time in HH:mm format (24-hour)
    /// </summary>
    [JsonPropertyName("startTime")]
    public string StartTime { get; set; } = "00:00";

    /// <summary>
    /// End time in HH:mm format (24-hour)
    /// </summary>
    [JsonPropertyName("endTime")]
    public string EndTime { get; set; } = "23:59";
}

/// <summary>
/// Complete user alarm configuration containing all profiles and global settings.
/// This is the root object stored in JSONB.
/// </summary>
public class UserAlarmConfiguration
{
    /// <summary>
    /// Version for migration purposes
    /// </summary>
    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    /// <summary>
    /// Global master switch for all alarms
    /// </summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Global sound enabled setting
    /// </summary>
    [JsonPropertyName("soundEnabled")]
    public bool SoundEnabled { get; set; } = true;

    /// <summary>
    /// Global vibration enabled setting
    /// </summary>
    [JsonPropertyName("vibrationEnabled")]
    public bool VibrationEnabled { get; set; } = true;

    /// <summary>
    /// Global volume level (0-100)
    /// </summary>
    [JsonPropertyName("globalVolume")]
    public int GlobalVolume { get; set; } = 80;

    /// <summary>
    /// List of all configured alarm profiles
    /// </summary>
    [JsonPropertyName("profiles")]
    public List<AlarmProfileConfiguration> Profiles { get; set; } = new();

    /// <summary>
    /// Quiet hours configuration
    /// </summary>
    [JsonPropertyName("quietHours")]
    public QuietHoursConfiguration QuietHours { get; set; } = new();

    /// <summary>
    /// Custom sounds uploaded by the user
    /// </summary>
    [JsonPropertyName("customSounds")]
    public List<CustomSoundReference> CustomSounds { get; set; } = new();

    /// <summary>
    /// Emergency contacts to notify for urgent alarms
    /// </summary>
    [JsonPropertyName("emergencyContacts")]
    public List<EmergencyContactConfig> EmergencyContacts { get; set; } = new();

    /// <summary>
    /// Notification channels configuration
    /// </summary>
    [JsonPropertyName("channels")]
    public NotificationChannelsConfig Channels { get; set; } = new();
}

/// <summary>
/// Enhanced quiet hours configuration
/// </summary>
public class QuietHoursConfiguration
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("startTime")]
    public string StartTime { get; set; } = "22:00";

    [JsonPropertyName("endTime")]
    public string EndTime { get; set; } = "07:00";

    /// <summary>
    /// Allow critical alarms during quiet hours
    /// </summary>
    [JsonPropertyName("allowCritical")]
    public bool AllowCritical { get; set; } = true;

    /// <summary>
    /// Reduce volume during quiet hours instead of silencing
    /// </summary>
    [JsonPropertyName("reduceVolume")]
    public bool ReduceVolume { get; set; } = true;

    /// <summary>
    /// Volume level during quiet hours (percentage)
    /// </summary>
    [JsonPropertyName("quietVolume")]
    public int QuietVolume { get; set; } = 30;
}

/// <summary>
/// Reference to a custom uploaded sound
/// </summary>
public class CustomSoundReference
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("durationSeconds")]
    public int? DurationSeconds { get; set; }

    [JsonPropertyName("uploadedAt")]
    public DateTimeOffset UploadedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Enhanced emergency contact with more notification options
/// </summary>
public class EmergencyContactConfig
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("phone")]
    public string? Phone { get; set; }

    [JsonPropertyName("email")]
    public string? Email { get; set; }

    /// <summary>
    /// Only notify for critical priority alarms
    /// </summary>
    [JsonPropertyName("criticalOnly")]
    public bool CriticalOnly { get; set; } = true;

    /// <summary>
    /// Delay before notifying (allows user to acknowledge first)
    /// </summary>
    [JsonPropertyName("delayMinutes")]
    public int DelayMinutes { get; set; } = 5;

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;
}

/// <summary>
/// Notification channels configuration
/// </summary>
public class NotificationChannelsConfig
{
    [JsonPropertyName("push")]
    public ChannelConfig Push { get; set; } = new() { Enabled = true };

    [JsonPropertyName("email")]
    public ChannelConfig Email { get; set; } = new();

    [JsonPropertyName("sms")]
    public ChannelConfig Sms { get; set; } = new();

    [JsonPropertyName("pushover")]
    public PushoverChannelConfig? Pushover { get; set; }
}

/// <summary>
/// Base channel configuration
/// </summary>
public class ChannelConfig
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    /// <summary>
    /// Minimum priority level to send through this channel
    /// </summary>
    [JsonPropertyName("minPriority")]
    public AlarmPriority MinPriority { get; set; } = AlarmPriority.Normal;
}

/// <summary>
/// Pushover-specific channel configuration
/// </summary>
public class PushoverChannelConfig : ChannelConfig
{
    [JsonPropertyName("userKey")]
    public string? UserKey { get; set; }

    [JsonPropertyName("apiToken")]
    public string? ApiToken { get; set; }
}

/// <summary>
/// Built-in sound presets
/// </summary>
public static class BuiltInSounds
{
    public const string Default = "alarm-default";
    public const string Urgent = "alarm-urgent";
    public const string High = "alarm-high";
    public const string Low = "alarm-low";
    public const string Alert = "alert";
    public const string Chime = "chime";
    public const string Bell = "bell";
    public const string Siren = "siren";
    public const string Beep = "beep";
    public const string Soft = "soft";

    public static readonly List<SoundPreset> All = new()
    {
        new(Default, "Default Alarm", "Standard alarm sound"),
        new(Urgent, "Urgent Alarm", "Loud, attention-grabbing alarm"),
        new(High, "High Alert", "Warning tone for high glucose"),
        new(Low, "Low Alert", "Warning tone for low glucose"),
        new(Alert, "Alert", "General alert sound"),
        new(Chime, "Chime", "Pleasant chime"),
        new(Bell, "Bell", "Bell ring"),
        new(Siren, "Siren", "Emergency siren"),
        new(Beep, "Beep", "Simple beep"),
        new(Soft, "Soft", "Gentle, quiet notification"),
    };
}

/// <summary>
/// Sound preset metadata
/// </summary>
public record SoundPreset(string Id, string Name, string Description);
