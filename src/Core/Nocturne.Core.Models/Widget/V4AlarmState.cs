using System.Text.Json.Serialization;

namespace Nocturne.Core.Models.Widget;

/// <summary>
/// Current alarm state for widget display.
/// Contains information about active alarms and their silencing status.
/// </summary>
public class V4AlarmState
{
    /// <summary>
    /// Alarm level (severity).
    /// Higher values indicate more severe alarms.
    /// </summary>
    [JsonPropertyName("level")]
    public int Level { get; set; }

    /// <summary>
    /// Type of alarm (e.g., "urgent_high", "urgent_low", "high", "low").
    /// </summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    /// <summary>
    /// Human-readable alarm message.
    /// </summary>
    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// Timestamp when the alarm was triggered in milliseconds since Unix epoch.
    /// </summary>
    [JsonPropertyName("triggeredMills")]
    public long TriggeredMills { get; set; }

    [JsonPropertyName("isSilenced")]
    public bool IsSilenced { get; set; }

    /// <summary>
    /// Timestamp when the silence period expires in milliseconds since Unix epoch.
    /// Null if the alarm is not silenced.
    /// </summary>
    [JsonPropertyName("silenceExpiresMills")]
    public long? SilenceExpiresMills { get; set; }
}
