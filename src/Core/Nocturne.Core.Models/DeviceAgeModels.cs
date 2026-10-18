using System.Text.Json.Serialization;

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
    /// Notification level based on age thresholds
    /// </summary>
    [JsonPropertyName("level")]
    public int Level { get; set; }

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
    public string Min { get; set; } = "Sensor Start";
}

public class DeviceAgeNotification
{
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("pushoverSound")]
    public string PushoverSound { get; set; } = "incoming";

    [JsonPropertyName("level")]
    public int Level { get; set; }

    [JsonPropertyName("group")]
    public string Group { get; set; } = string.Empty;
}
