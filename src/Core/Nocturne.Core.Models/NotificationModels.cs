using System.Text.Json.Serialization;

namespace Nocturne.Core.Models;

/// <summary>
/// V2 Notification response model for enhanced notifications system
/// Maintains 1:1 compatibility with legacy Nightscout notifications v2 API
/// </summary>
public class NotificationV2Response
{
    /// <summary>
    /// Success status of the notification operation
    /// </summary>
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    /// <summary>
    /// Message describing the result of the notification operation
    /// </summary>
    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// Optional data payload for the notification
    /// </summary>
    [JsonPropertyName("data")]
    public object? Data { get; set; }

    /// <summary>
    /// Timestamp when the notification was processed
    /// </summary>
    [JsonPropertyName("timestamp")]
    public long Timestamp { get; set; }
}

/// <summary>
/// V2 Loop notification request model for Loop app integration
/// Maintains 1:1 compatibility with legacy Nightscout Loop notifications
/// </summary>
public class LoopNotificationRequest
{
    /// <summary>
    /// Type of notification (e.g., "loop-completed", "loop-failed")
    /// </summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    /// <summary>
    /// Main message content of the notification
    /// </summary>
    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// Title of the notification
    /// </summary>
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    /// <summary>
    /// Urgency level of the notification
    /// </summary>
    [JsonPropertyName("urgency")]
    public string? Urgency { get; set; }

    /// <summary>
    /// Sound to play for the notification
    /// </summary>
    [JsonPropertyName("sound")]
    public string? Sound { get; set; }

    /// <summary>
    /// Group identifier for notification categorization
    /// </summary>
    [JsonPropertyName("group")]
    public string? Group { get; set; }

    /// <summary>
    /// Timestamp when the notification was created
    /// </summary>
    [JsonPropertyName("timestamp")]
    public long? Timestamp { get; set; }

    /// <summary>
    /// Additional arbitrary data for the notification
    /// </summary>
    [JsonPropertyName("data")]
    public Dictionary<string, object>? Data { get; set; }

    /// <summary>
    /// Indicates if this is an announcement notification
    /// </summary>
    [JsonPropertyName("isAnnouncement")]
    public bool? IsAnnouncement { get; set; }
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

    /// <summary>
    /// Title of the notification
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Message content of the notification
    /// </summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// Group identifier for notification categorization
    /// </summary>
    public string Group { get; set; } = "default";

    /// <summary>
    /// Timestamp when the notification was created
    /// </summary>
    public long Timestamp { get; set; }

    /// <summary>
    /// Plugin that generated this notification
    /// </summary>
    public string? Plugin { get; set; }

    /// <summary>
    /// Indicates if this is an announcement notification
    /// </summary>
    public bool IsAnnouncement { get; set; }

    /// <summary>
    /// Additional debug information
    /// </summary>
    public object? Debug { get; set; }

    /// <summary>
    /// Count of times this notification has been repeated
    /// </summary>
    public int Count { get; set; }

    /// <summary>
    /// Timestamp when the notification was last recorded
    /// </summary>
    public long LastRecorded { get; set; }

    /// <summary>
    /// Indicates if this notification should persist
    /// </summary>
    public bool Persistent { get; set; }

    /// <summary>
    /// Gets or sets whether this is a clear alarm notification
    /// </summary>
    public bool Clear { get; set; }

    /// <summary>
    /// Gets or sets the alert type that triggered this notification (e.g. "Low", "High", "UrgentLow").
    /// Used by clients to filter which alarm types to display.
    /// </summary>
    public string? AlertType { get; set; }
}

/// <summary>
/// V1 Notification acknowledgment request model
/// Maintains 1:1 compatibility with legacy Nightscout notification ack API
/// </summary>
public class NotificationAckRequest
{
    /// <summary>
    /// Level of the alarm to acknowledge (1=WARN, 2=URGENT)
    /// </summary>
    [JsonPropertyName("level")]
    public int Level { get; set; }

    /// <summary>
    /// Group identifier for the alarm
    /// </summary>
    [JsonPropertyName("group")]
    public string Group { get; set; } = "default";

    /// <summary>
    /// Time in milliseconds to silence the alarm (default: 30 minutes)
    /// </summary>
    [JsonPropertyName("time")]
    public int? Time { get; set; }

    /// <summary>
    /// Whether to send a clear notification
    /// </summary>
    [JsonPropertyName("sendClear")]
    public bool? SendClear { get; set; }
}

/// <summary>
/// V1 Notification acknowledgment response model
/// </summary>
public class NotificationAckResponse
{
    /// <summary>
    /// Success status of the acknowledgment operation
    /// </summary>
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    /// <summary>
    /// Message describing the result
    /// </summary>
    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// Timestamp of the operation
    /// </summary>
    [JsonPropertyName("timestamp")]
    public long Timestamp { get; set; }
}

/// <summary>
/// Pushover callback request model for handling Pushover webhook callbacks
/// Maintains 1:1 compatibility with legacy Nightscout Pushover callback API
/// </summary>
public class PushoverCallbackRequest
{
    /// <summary>
    /// Pushover receipt ID
    /// </summary>
    [JsonPropertyName("receipt")]
    public string? Receipt { get; set; }

    /// <summary>
    /// Pushover status (acknowledged, expired, etc.)
    /// </summary>
    [JsonPropertyName("status")]
    public int? Status { get; set; }

    /// <summary>
    /// Timestamp when acknowledged
    /// </summary>
    [JsonPropertyName("acknowledged_at")]
    public long? AcknowledgedAt { get; set; }

    /// <summary>
    /// User who acknowledged the notification
    /// </summary>
    [JsonPropertyName("acknowledged_by")]
    public string? AcknowledgedBy { get; set; }

    /// <summary>
    /// User key from Pushover
    /// </summary>
    [JsonPropertyName("user_key")]
    public string? UserKey { get; set; }

    /// <summary>
    /// Callback URL requested
    /// </summary>
    [JsonPropertyName("callback")]
    public string? Callback { get; set; }
}

/// <summary>
/// Admin notification model based on legacy adminnotifies.js
/// Maintains 1:1 compatibility with legacy Nightscout admin notifications
/// </summary>
public class AdminNotification
{
    /// <summary>
    /// Notification title
    /// </summary>
    [JsonPropertyName("title")]
    public string Title { get; set; } = "None";

    /// <summary>
    /// Notification message
    /// </summary>
    [JsonPropertyName("message")]
    public string Message { get; set; } = "None";

    /// <summary>
    /// Count of times this notification has occurred
    /// </summary>
    [JsonPropertyName("count")]
    public int Count { get; set; } = 1;

    /// <summary>
    /// Timestamp when last recorded
    /// </summary>
    [JsonPropertyName("lastRecorded")]
    public long LastRecorded { get; set; }

    /// <summary>
    /// Whether this notification should persist beyond 12 hours
    /// </summary>
    [JsonPropertyName("persistent")]
    public bool Persistent { get; set; }
}

/// <summary>
/// Admin notifications response model
/// </summary>
public class AdminNotifiesResponse
{
    /// <summary>
    /// Status code
    /// </summary>
    [JsonPropertyName("status")]
    public int Status { get; set; } = 200;

    /// <summary>
    /// Response message containing notification data
    /// </summary>
    [JsonPropertyName("message")]
    public AdminNotifiesMessage Message { get; set; } = new();
}

/// <summary>
/// Admin notifications message payload
/// </summary>
public class AdminNotifiesMessage
{
    /// <summary>
    /// List of admin notifications
    /// </summary>
    [JsonPropertyName("notifies")]
    public List<AdminNotification> Notifies { get; set; } = new();

    /// <summary>
    /// Total count of notifications
    /// </summary>
    [JsonPropertyName("notifyCount")]
    public int NotifyCount { get; set; }
}

/// <summary>
/// Pushover notification request model for sending notifications
/// Maintains 1:1 compatibility with legacy Nightscout Pushover integration
/// </summary>
public class PushoverNotificationRequest
{
    /// <summary>
    /// Notification title
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Notification message content
    /// </summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// Alarm level for receipt mapping (1=WARN, 2=URGENT)
    /// </summary>
    public int? Level { get; set; }

    /// <summary>
    /// Alarm group for receipt mapping
    /// </summary>
    public string? Group { get; set; }

    /// <summary>
    /// Pushover priority (-2 to 2)
    /// -2: Lowest, -1: Low, 0: Normal, 1: High, 2: Emergency
    /// </summary>
    public int? Priority { get; set; }

    /// <summary>
    /// Pushover sound name (e.g., "persistent", "siren", "default")
    /// </summary>
    public string? Sound { get; set; }

    /// <summary>
    /// Specific device to send to (optional)
    /// </summary>
    public string? Device { get; set; }

    /// <summary>
    /// URL to include in notification (optional)
    /// </summary>
    public string? Url { get; set; }

    /// <summary>
    /// Title for the URL (optional)
    /// </summary>
    public string? UrlTitle { get; set; }

    /// <summary>
    /// Retry interval in seconds for emergency priority (priority=2)
    /// </summary>
    public int? Retry { get; set; }

    /// <summary>
    /// Expiration time in seconds for emergency priority (priority=2)
    /// </summary>
    public int? Expire { get; set; }
}

/// <summary>
/// Pushover notification response model
/// Contains receipt information for emergency priority notifications
/// </summary>
public class PushoverResponse
{
    /// <summary>
    /// Whether the notification was sent successfully
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Pushover receipt ID for emergency priority notifications
    /// Used for acknowledgment callbacks
    /// </summary>
    public string? Receipt { get; set; }

    /// <summary>
    /// Pushover request ID
    /// </summary>
    public string? Request { get; set; }

    /// <summary>
    /// Error message if sending failed
    /// </summary>
    public string? Error { get; set; }
}

/// <summary>
/// Alert event model for internal alert processing
/// </summary>
public class AlertEvent
{
    /// <summary>
    /// Unique identifier for this alert event
    /// </summary>
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>
    /// User identifier this alert belongs to
    /// </summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>
    /// Alert rule that triggered this event (optional)
    /// </summary>
    public Guid? AlertRuleId { get; set; }

    /// <summary>
    /// Type of alert that was triggered
    /// </summary>
    public AlertType AlertType { get; set; }

    /// <summary>
    /// Glucose value that triggered the alert (mg/dL)
    /// </summary>
    public decimal GlucoseValue { get; set; }

    /// <summary>
    /// Threshold value that was crossed
    /// </summary>
    public decimal Threshold { get; set; }

    /// <summary>
    /// When the alert was triggered
    /// </summary>
    public DateTime TriggerTime { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Alert rule entity (for processing context)
    /// </summary>
    public object? Rule { get; set; }

    /// <summary>
    /// Additional context data for the alert
    /// </summary>
    public Dictionary<string, object>? Context { get; set; }
}

/// <summary>
/// Alert type enumeration
/// </summary>
public enum AlertType
{
    /// <summary>
    /// Low glucose alert
    /// </summary>
    Low,

    /// <summary>
    /// High glucose alert
    /// </summary>
    High,

    /// <summary>
    /// Urgent low glucose alert
    /// </summary>
    UrgentLow,

    /// <summary>
    /// Urgent high glucose alert
    /// </summary>
    UrgentHigh,

    /// <summary>
    /// Device warning or error
    /// </summary>
    DeviceWarning,

    /// <summary>
    /// Forecast low glucose alert
    /// </summary>
    ForecastLow,
}

/// <summary>
/// Alert monitoring configuration options
/// </summary>
public class AlertMonitoringOptions
{
    /// <summary>
    /// Section name for configuration binding
    /// </summary>
    public const string SectionName = "AlertMonitoring";

    /// <summary>
    /// Cooldown period in minutes to prevent alert spam
    /// </summary>
    public int AlertCooldownMinutes { get; set; } = 15;

    /// <summary>
    /// Maximum number of active alerts per user
    /// </summary>
    public int MaxActiveAlertsPerUser { get; set; } = 10;

    /// <summary>
    /// Hysteresis percentage to prevent oscillating alerts (0.1 = 10%)
    /// </summary>
    public double HysteresisPercentage { get; set; } = 0.1;

    /// <summary>
    /// Number of alerts to process in a single batch
    /// </summary>
    public int AlertProcessingBatchSize { get; set; } = 100;

    /// <summary>
    /// Maximum time in seconds to process glucose data before generating alerts
    /// </summary>
    public int MaxProcessingTimeSeconds { get; set; } = 5;

    /// <summary>
    /// Enable debug logging for alert processing
    /// </summary>
    public bool EnableDebugLogging { get; set; } = false;

    /// <summary>
    /// Default low glucose threshold in mg/dL if no rule specified
    /// </summary>
    public decimal DefaultLowThreshold { get; set; } = 70;

    /// <summary>
    /// Default high glucose threshold in mg/dL if no rule specified
    /// </summary>
    public decimal DefaultHighThreshold { get; set; } = 180;

    /// <summary>
    /// Default urgent low glucose threshold in mg/dL if no rule specified
    /// </summary>
    public decimal DefaultUrgentLowThreshold { get; set; } = 55;

    /// <summary>
    /// Default urgent high glucose threshold in mg/dL if no rule specified
    /// </summary>
    public decimal DefaultUrgentHighThreshold { get; set; } = 300;
}
