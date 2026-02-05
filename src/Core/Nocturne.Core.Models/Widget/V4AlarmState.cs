using System.Text.Json.Serialization;

namespace Nocturne.Core.Models.Widget;

/// <summary>
/// Represents the current alarm state for widget display.
/// Contains information about active alarms and their silencing status.
/// </summary>
public class V4AlarmState
{
    /// <summary>
    /// Gets or sets the alarm level (severity).
    /// Higher values indicate more severe alarms.
    /// </summary>
    [JsonPropertyName("level")]
    public int Level { get; set; }

    /// <summary>
    /// Gets or sets the type of alarm (e.g., "urgent_high", "urgent_low", "high", "low").
    /// </summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the human-readable alarm message.
    /// </summary>
    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the timestamp when the alarm was triggered in milliseconds since Unix epoch.
    /// </summary>
    [JsonPropertyName("triggeredMills")]
    public long TriggeredMills { get; set; }

    /// <summary>
    /// Gets or sets whether the alarm is currently silenced.
    /// </summary>
    [JsonPropertyName("isSilenced")]
    public bool IsSilenced { get; set; }

    /// <summary>
    /// Gets or sets the timestamp when the silence period expires in milliseconds since Unix epoch.
    /// Null if the alarm is not silenced.
    /// </summary>
    [JsonPropertyName("silenceExpiresMills")]
    public long? SilenceExpiresMills { get; set; }
}
