using System.Text.Json.Serialization;

namespace Nocturne.Core.Models.Battery;

/// <summary>
/// Represents a single battery reading from a device
/// </summary>
public class BatteryReading
{
    /// <summary>
    /// Gets or sets the unique identifier for this reading
    /// </summary>
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    /// <summary>
    /// Gets or sets the device identifier/name
    /// </summary>
    [JsonPropertyName("device")]
    public string Device { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the battery percentage (0-100)
    /// </summary>
    [JsonPropertyName("battery")]
    public int? Battery { get; set; }

    /// <summary>
    /// Gets or sets the battery voltage (in volts)
    /// </summary>
    [JsonPropertyName("voltage")]
    public double? Voltage { get; set; }

    /// <summary>
    /// Gets or sets whether the device is currently charging
    /// </summary>
    [JsonPropertyName("isCharging")]
    public bool IsCharging { get; set; }

    /// <summary>
    /// Gets or sets the temperature of the device/battery (if available)
    /// </summary>
    [JsonPropertyName("temperature")]
    public double? Temperature { get; set; }

    /// <summary>
    /// Gets or sets the timestamp in milliseconds since Unix epoch
    /// </summary>
    [JsonPropertyName("mills")]
    public long Mills { get; set; }

    /// <summary>
    /// Gets or sets the ISO 8601 formatted timestamp
    /// </summary>
    [JsonPropertyName("timestamp")]
    public string? Timestamp { get; set; }

    /// <summary>
    /// Gets or sets the display value for the battery (e.g., "85%" or "3.7v")
    /// </summary>
    [JsonPropertyName("display")]
    public string Display => GetDisplayValue();

    /// <summary>
    /// Gets the battery level category (25, 50, 75, 100) for icon display
    /// </summary>
    [JsonPropertyName("level")]
    public int Level => GetBatteryLevel();

    /// <summary>
    /// Gets the notification level (none, warn, urgent) based on battery level
    /// </summary>
    [JsonPropertyName("notification")]
    public string? Notification { get; set; }

    private string GetDisplayValue()
    {
        var chargeIndicator = IsCharging ? "⚡" : "";

        if (Battery.HasValue)
        {
            return $"{Battery}%{chargeIndicator}";
        }

        if (Voltage.HasValue)
        {
            var displayVoltage = Voltage.Value > 1000 ? Voltage.Value / 1000 : Voltage.Value;
            return $"{displayVoltage:F3}v{chargeIndicator}";
        }

        return $"?%{chargeIndicator}";
    }

    private int GetBatteryLevel()
    {
        if (!Battery.HasValue)
            return 0;

        return Battery.Value switch
        {
            >= 95 => 100,
            >= 55 => 75,
            >= 30 => 50,
            _ => 25,
        };
    }
}
