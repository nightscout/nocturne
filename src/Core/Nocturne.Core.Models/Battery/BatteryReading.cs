using System.Globalization;
using System.Text.Json.Serialization;

namespace Nocturne.Core.Models.Battery;

/// <summary>
/// Single battery reading from a device.
/// Extracted from device status records during processing.
/// </summary>
/// <seealso cref="BatteryStatistics"/>
/// <seealso cref="ChargeCycle"/>
/// <seealso cref="CurrentBatteryStatus"/>
public class BatteryReading
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("device")]
    public string Device { get; set; } = string.Empty;

    /// <summary>
    /// Battery percentage (0-100)
    /// </summary>
    [JsonPropertyName("battery")]
    public int? Battery { get; set; }

    /// <summary>
    /// Battery voltage (in volts)
    /// </summary>
    [JsonPropertyName("voltage")]
    public double? Voltage { get; set; }

    /// <summary>
    /// Whether the device is currently charging
    /// </summary>
    [JsonPropertyName("isCharging")]
    public bool IsCharging { get; set; }

    /// <summary>
    /// Temperature of the device/battery (if available)
    /// </summary>
    [JsonPropertyName("temperature")]
    public double? Temperature { get; set; }

    /// <summary>
    /// Timestamp in milliseconds since Unix epoch
    /// </summary>
    [JsonPropertyName("mills")]
    public long Mills { get; set; }

    /// <summary>
    /// ISO 8601 formatted timestamp
    /// </summary>
    [JsonPropertyName("timestamp")]
    public string? Timestamp { get; set; }

    /// <summary>
    /// Display value for the battery (e.g., "85%" or "3.7v")
    /// </summary>
    [JsonPropertyName("display")]
    public string Display => GetDisplayValue();

    /// <summary>
    /// Battery level category (0, 25, 50, 75, 100) for icon display
    /// </summary>
    [JsonPropertyName("level")]
    public int Level => BatteryLevelBucket.Of(Battery);

    /// <summary>
    /// Notification level (none, warn, urgent) based on battery level
    /// </summary>
    [JsonPropertyName("notification")]
    public string? Notification { get; set; }

    /// <summary>
    /// Volts to three places with a "v" suffix; a value above 1000 is millivolts.
    /// </summary>
    public static string FormatVoltage(double voltage)
    {
        var volts = voltage > 1000 ? voltage / 1000 : voltage;
        return volts.ToString("F3", CultureInfo.InvariantCulture) + "v";
    }

    private string GetDisplayValue()
    {
        var chargeIndicator = IsCharging ? "⚡" : "";

        if (Battery.HasValue)
        {
            return $"{Battery}%{chargeIndicator}";
        }

        if (Voltage.HasValue)
        {
            return $"{FormatVoltage(Voltage.Value)}{chargeIndicator}";
        }

        return $"?%{chargeIndicator}";
    }
}
