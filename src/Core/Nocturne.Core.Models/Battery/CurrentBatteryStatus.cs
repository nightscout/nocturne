using System.Text.Json.Serialization;

namespace Nocturne.Core.Models.Battery;

/// <summary>
/// Represents the current battery status for all tracked devices
/// </summary>
public class CurrentBatteryStatus
{
    /// <summary>
    /// Gets or sets the overall/minimum battery level across all devices
    /// </summary>
    [JsonPropertyName("level")]
    public int? Level { get; set; }

    /// <summary>
    /// Gets or sets the display string for the overall battery status
    /// </summary>
    [JsonPropertyName("display")]
    public string Display { get; set; } = "?%";

    /// <summary>
    /// Gets or sets the overall status (ok, warn, urgent)
    /// </summary>
    [JsonPropertyName("status")]
    public string Status { get; set; } = "ok";

    /// <summary>
    /// Gets or sets the minimum battery reading across all devices
    /// </summary>
    [JsonPropertyName("min")]
    public BatteryReading? Min { get; set; }

    /// <summary>
    /// Gets or sets battery status for each device
    /// </summary>
    [JsonPropertyName("devices")]
    public Dictionary<string, DeviceBatteryStatus> Devices { get; set; } = new();
}

/// <summary>
/// Represents the current battery status for a single device
/// </summary>
public class DeviceBatteryStatus
{
    /// <summary>
    /// Gets or sets the device URI/identifier
    /// </summary>
    [JsonPropertyName("uri")]
    public string Uri { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the device display name
    /// </summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the most recent battery readings for this device
    /// </summary>
    [JsonPropertyName("statuses")]
    public List<BatteryReading> Statuses { get; set; } = new();

    /// <summary>
    /// Gets or sets the minimum reading from recent statuses
    /// </summary>
    [JsonPropertyName("min")]
    public BatteryReading? Min { get; set; }
}
