using System.Text.Json.Serialization;

namespace Nocturne.Core.Models;

/// <summary>
/// V2 Notification response model for enhanced notifications system.
/// Maintains 1:1 compatibility with legacy Nightscout notifications v2 API.
/// </summary>
/// <seealso cref="NotificationAckRequest"/>
public class NotificationV2Response
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("data")]
    public object? Data { get; set; }

    [JsonPropertyName("timestamp")]
    public long Timestamp { get; set; }
}

/// <summary>
/// Base notification model used internally for processing
/// </summary>
public class NotificationBase
{
    /// <summary>
    /// Notification level (INFO, WARN, URGENT)
    /// </summary>
    public int Level { get; set; }

    public string Title { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;

    public string Group { get; set; } = "default";

    public long Timestamp { get; set; }

    public string? Plugin { get; set; }

    public string? EventName { get; set; }

    /// <summary>
    /// Indicates if this is an announcement notification
    /// </summary>
    public bool IsAnnouncement { get; set; }

    public object? Debug { get; set; }

    /// <summary>
    /// Count of times this notification has been repeated
    /// </summary>
    public int Count { get; set; }

    public long LastRecorded { get; set; }

    /// <summary>
    /// Indicates if this notification should persist
    /// </summary>
    public bool Persistent { get; set; }

    /// <summary>Marks an all-clear notification in the Nightscout payload.</summary>
    public bool Clear { get; set; }
}

/// <summary>
/// V1 Notification acknowledgment request model
/// Maintains 1:1 compatibility with legacy Nightscout notification ack API
/// </summary>
public class NotificationAckRequest
{
    /// <summary>Nightscout's silence time when an ack gives none.</summary>
    public const int DefaultSilenceTimeMs = 30 * 60 * 1000;

    /// <summary>
    /// Level of the alarm to acknowledge (1=WARN, 2=URGENT)
    /// </summary>
    [JsonPropertyName("level")]
    public int Level { get; set; }

    [JsonPropertyName("group")]
    public string Group { get; set; } = "default";

    /// <summary>
    /// Time in milliseconds to silence the alarm (default: 30 minutes)
    /// </summary>
    [JsonPropertyName("time")]
    public int? Time { get; set; }

    [JsonPropertyName("sendClear")]
    public bool? SendClear { get; set; }
}

/// <summary>
/// V1 Notification acknowledgment response model
/// </summary>
public class NotificationAckResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("timestamp")]
    public long Timestamp { get; set; }
}

/// <summary>
/// Admin notification model based on legacy adminnotifies.js
/// Maintains 1:1 compatibility with legacy Nightscout admin notifications
/// </summary>
public class AdminNotification
{
    [JsonPropertyName("title")]
    public string Title { get; set; } = "None";

    [JsonPropertyName("message")]
    public string Message { get; set; } = "None";

    /// <summary>
    /// Count of times this notification has occurred
    /// </summary>
    [JsonPropertyName("count")]
    public int Count { get; set; } = 1;

    /// <summary>When the notification was last recorded, in Unix milliseconds.</summary>
    [JsonPropertyName("lastRecorded")]
    public long LastRecorded { get; set; }

    /// <summary>
    /// Whether this notification should persist beyond 12 hours
    /// </summary>
    [JsonPropertyName("persistent")]
    public bool Persistent { get; set; }
}

public class AdminNotifiesResponse
{
    [JsonPropertyName("status")]
    public int Status { get; set; } = 200;

    [JsonPropertyName("message")]
    public AdminNotifiesMessage Message { get; set; } = new();
}

/// <summary>
/// Admin notifications message payload
/// </summary>
public class AdminNotifiesMessage
{
    [JsonPropertyName("notifies")]
    public List<AdminNotification> Notifies { get; set; } = new();

    /// <summary>
    /// Total count of notifications
    /// </summary>
    [JsonPropertyName("notifyCount")]
    public int NotifyCount { get; set; }
}
