using System.Text.Json.Serialization;

namespace Nocturne.Core.Models.Battery;

/// <summary>
/// Current battery status for all tracked devices.
/// Provides an aggregated view with the worst-case level across all devices.
/// </summary>
/// <seealso cref="DeviceBatteryStatus"/>
/// <seealso cref="BatteryReading"/>
/// <seealso cref="BatteryStatistics"/>
public class CurrentBatteryStatus
{
    /// <summary>
    /// Overall/minimum battery level across all devices
    /// </summary>
    [JsonPropertyName("level")]
    public int? Level { get; set; }

    [JsonPropertyName("display")]
    public string Display { get; set; } = "?%";

    /// <summary>
    /// Overall status (ok, warn, urgent)
    /// </summary>
    [JsonPropertyName("status")]
    public string Status { get; set; } = "ok";

    /// <summary>
    /// Minimum battery reading across all devices
    /// </summary>
    [JsonPropertyName("min")]
    public BatteryReading? Min { get; set; }

    [JsonPropertyName("devices")]
    public Dictionary<string, DeviceBatteryStatus> Devices { get; set; } = new();
}

/// <seealso cref="CurrentBatteryStatus"/>
/// <seealso cref="BatteryReading"/>
public class DeviceBatteryStatus
{
    [JsonPropertyName("uri")]
    public string Uri { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Most recent battery readings for this device
    /// </summary>
    [JsonPropertyName("statuses")]
    public List<BatteryReading> Statuses { get; set; } = new();

    /// <summary>
    /// Minimum reading from recent statuses
    /// </summary>
    [JsonPropertyName("min")]
    public BatteryReading? Min { get; set; }
}
