using System.Text.Json.Serialization;
using Nocturne.Core.Constants;

namespace Nocturne.Core.Models;

/// <summary>
/// Device age preferences with 1:1 legacy JavaScript compatibility.
/// Configures alert thresholds in hours for when a device change is overdue.
/// </summary>
/// <seealso cref="DeviceAgeInfo"/>
/// <seealso cref="SensorAgeInfo"/>
/// <seealso cref="DeviceAgeNotification"/>
public class DeviceAgePreferences
{
    /// <summary>
    /// Age threshold for info level notifications (in hours)
    /// </summary>
    [JsonPropertyName("info")]
    public int Info { get; set; }

    /// <summary>
    /// Age threshold for warning level notifications (in hours)
    /// </summary>
    [JsonPropertyName("warn")]
    public int Warn { get; set; }

    /// <summary>
    /// Age threshold for urgent level notifications (in hours)
    /// </summary>
    [JsonPropertyName("urgent")]
    public int Urgent { get; set; }

    /// <summary>
    /// Display format ("hours" or "days")
    /// </summary>
    [JsonPropertyName("display")]
    public string Display { get; set; } = "hours";

    [JsonPropertyName("enableAlerts")]
    public bool EnableAlerts { get; set; }
}

/// <summary>
/// The Nightscout alarm level scale. A device age below its info threshold, or with no change
/// event found, is <see cref="None"/>.
/// </summary>
public static class DeviceAgeLevel
{
    public const int None = -3;
    public const int Info = 0;
    public const int Warn = 1;
    public const int Urgent = 2;
}

/// <summary>
/// Device age information with 1:1 legacy JavaScript compatibility
/// </summary>
public class DeviceAgeInfo
{
    /// <summary>
    /// Whether a device change event was found
    /// </summary>
    [JsonPropertyName("found")]
    public bool Found { get; set; }

    /// <summary>
    /// Age in hours since the last device change
    /// </summary>
    [JsonPropertyName("age")]
    public int Age { get; set; }

    /// <summary>
    /// Age in days (calculated from hours)
    /// </summary>
    [JsonPropertyName("days")]
    public int Days { get; set; }

    /// <summary>
    /// Remaining hours after full days
    /// </summary>
    [JsonPropertyName("hours")]
    public int Hours { get; set; }

    /// <summary>
    /// Timestamp of the treatment (in milliseconds since Unix epoch)
    /// </summary>
    [JsonPropertyName("treatmentDate")]
    public long? TreatmentDate { get; set; }

    [JsonPropertyName("notes")]
    public string? Notes { get; set; }

    /// <summary>
    /// Minutes fraction for precise timing
    /// </summary>
    [JsonPropertyName("minFractions")]
    public int MinFractions { get; set; }

    /// <summary>
    /// Alarm level for the age, on the <see cref="DeviceAgeLevel"/> scale
    /// </summary>
    [JsonPropertyName("level")]
    public int Level { get; set; } = DeviceAgeLevel.None;

    [JsonPropertyName("display")]
    public string Display { get; set; } = "n/a";

    /// <summary>
    /// Notification information if alerts are enabled
    /// </summary>
    [JsonPropertyName("notification")]
    public DeviceAgeNotification? Notification { get; set; }
}

/// <summary>
/// Sensor age information with support for both start and change events
/// </summary>
public class SensorAgeInfo
{
    [JsonPropertyName("Sensor Start")]
    public DeviceAgeInfo SensorStart { get; set; } = new();

    [JsonPropertyName("Sensor Change")]
    public DeviceAgeInfo SensorChange { get; set; } = new();

    /// <summary>
    /// The most recent valid event type ("Sensor Start" or "Sensor Change")
    /// </summary>
    [JsonPropertyName("min")]
    public string Min { get; set; } = TreatmentTypes.SensorStart;
}

public class DeviceAgeNotification
{
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("pushoverSound")]
    public string PushoverSound { get; set; } = "incoming";

    /// <summary>
    /// The <see cref="DeviceAgeLevel"/> of the age that raised the notification
    /// </summary>
    [JsonPropertyName("level")]
    public int Level { get; set; } = DeviceAgeLevel.None;

    [JsonPropertyName("group")]
    public string Group { get; set; } = string.Empty;
}
