using System.ComponentModel.DataAnnotations;

namespace Nocturne.Core.Models;

/// <summary>
/// Data transfer object for in-app notifications displayed to users in the Nocturne frontend.
/// </summary>
/// <seealso cref="NotificationCategory"/>
/// <seealso cref="NotificationUrgency"/>
/// <seealso cref="NotificationActionDto"/>
/// <seealso cref="NotificationTemplate"/>
public class InAppNotificationDto
{
    /// <summary>
    /// Unique identifier for the notification
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Dotted string type for categorization and handling (e.g., "tracker.alert", "glucose.predicted_low")
    /// </summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>
    /// Broad rendering category that determines visual treatment
    /// </summary>
    public NotificationCategory Category { get; set; }

    /// <summary>
    /// Urgency level for prioritization and visual styling
    /// </summary>
    public NotificationUrgency Urgency { get; set; }

    /// <summary>
    /// Primary notification title displayed to the user
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Optional secondary text providing additional context
    /// </summary>
    public string? Subtitle { get; set; }

    /// <summary>
    /// When the notification was created
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// When the notification was marked read by the user, or null while unread.
    /// </summary>
    public DateTime? ReadAt { get; set; }

    /// <summary>
    /// Lucide icon name for the notification (e.g., "alert-triangle", "bell")
    /// </summary>
    public string? Icon { get; set; }

    /// <summary>
    /// Subsystem or service that created the notification (e.g., "TrackerService", "ConnectorSync")
    /// </summary>
    public string? Source { get; set; }

    /// <summary>
    /// Optional source identifier for the notification (e.g., food entry ID for meal match)
    /// </summary>
    public string? SourceId { get; set; }

    /// <summary>
    /// Optional metadata containing notification-specific data
    /// </summary>
    public Dictionary<string, object>? Metadata { get; set; }

    /// <summary>
    /// Available actions the user can take on this notification
    /// </summary>
    public List<NotificationActionDto> Actions { get; set; } = new();
}

/// <summary>
/// Represents an action button available on a notification
/// </summary>
public class NotificationActionDto
{
    /// <summary>
    /// Unique identifier for the action within the notification
    /// </summary>
    public string ActionId { get; set; } = string.Empty;

    /// <summary>
    /// Display label for the action button
    /// </summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>
    /// Optional icon identifier for the action button
    /// </summary>
    public string? Icon { get; set; }

    /// <summary>
    /// Visual variant for the action button (e.g., "default", "destructive", "outline")
    /// </summary>
    public string? Variant { get; set; }
}

/// <summary>
/// Conditions that automatically resolve/archive a notification
/// </summary>
public class ResolutionConditions
{
    /// <summary>
    /// When the notification should automatically expire and be archived
    /// </summary>
    public DateTime? ExpiresAt { get; set; }

    /// <summary>
    /// If set, notification is archived when the source entity of this type is deleted
    /// </summary>
    public string? SourceDeletedType { get; set; }

    /// <summary>
    /// Glucose-based resolution condition (e.g., archive when glucose returns to range)
    /// </summary>
    public GlucoseCondition? Glucose { get; set; }
}

/// <summary>
/// Glucose-based condition for automatic notification resolution
/// </summary>
public class GlucoseCondition
{
    /// <summary>
    /// Archive when glucose rises above this value (mg/dL)
    /// </summary>
    public int? AboveMgDl { get; set; }

    /// <summary>
    /// Archive when glucose falls below this value (mg/dL)
    /// </summary>
    public int? BelowMgDl { get; set; }

    /// <summary>
    /// Number of minutes the condition must be sustained before archiving
    /// </summary>
    public int? SustainedMinutes { get; set; }
}

/// <summary>
/// Real-time notification event for SignalR broadcasting
/// </summary>
public class NotificationEvent
{
    /// <summary>
    /// Type of event: "created", "archived", or "updated"
    /// </summary>
    public string EventType { get; set; } = string.Empty;

    /// <summary>
    /// The notification data (for created/updated events)
    /// </summary>
    public InAppNotificationDto? Notification { get; set; }

    /// <summary>
    /// Why the notification was archived (for archived events)
    /// </summary>
    public NotificationArchiveReason? ArchiveReason { get; set; }
}

/// <summary>
/// Request to create a notification programmatically (for third-party integrations).
/// If a <see cref="NotificationTemplate"/> is registered for the given <see cref="Type"/>,
/// its defaults are merged with the values provided here.
/// </summary>
/// <seealso cref="InAppNotificationDto"/>
/// <seealso cref="NotificationTemplate"/>
public class CreateNotificationRequest
{
    /// <summary>
    /// Structured type string (e.g., "dexcom.connection_lost")
    /// </summary>
    [Required]
    public string Type { get; set; } = string.Empty;

    /// <summary>
    /// Notification title
    /// </summary>
    [Required]
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Rendering category. Required if no template is registered for the type.
    /// </summary>
    public NotificationCategory? Category { get; set; }

    public NotificationUrgency? Urgency { get; set; }
    public string? Subtitle { get; set; }
    public string? SourceId { get; set; }
    public string? Icon { get; set; }
    public string? Source { get; set; }
    public List<NotificationActionDto>? Actions { get; set; }
    public ResolutionConditions? ResolutionConditions { get; set; }
    public Dictionary<string, object>? Metadata { get; set; }
}
