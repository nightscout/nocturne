namespace Nocturne.Core.Models;

/// <summary>
/// Defines the defaults for a notification type, used by the template registry
/// to merge with caller-provided values when creating notifications.
/// </summary>
/// <seealso cref="InAppNotificationDto"/>
/// <seealso cref="CreateNotificationRequest"/>
/// <seealso cref="NotificationCategory"/>
/// <seealso cref="NotificationUrgency"/>
public class NotificationTemplate
{
    /// <summary>
    /// Dot-delimited type string (e.g., "tracker.unconfigured", "glucose.predicted_low")
    /// </summary>
    public required string Type { get; init; }

    /// <summary>
    /// Broad rendering category that determines visual treatment
    /// </summary>
    public required NotificationCategory Category { get; init; }

    /// <summary>
    /// Default urgency level when the caller does not specify one
    /// </summary>
    public NotificationUrgency DefaultUrgency { get; init; } = NotificationUrgency.Info;

    /// <summary>
    /// Lucide icon name for the notification
    /// </summary>
    public string? Icon { get; init; }

    /// <summary>
    /// Subsystem or service that produces this notification type
    /// </summary>
    public string? Source { get; init; }

    /// <summary>
    /// Whether a notice of this type puts a person's name or own words in front of someone else. True
    /// exactly for the types <see cref="NotificationSubjectNaming.Pointers"/> declares.
    /// </summary>
    public required bool NamesSubject { get; init; }

    /// <summary>
    /// Default actions available on notifications of this type
    /// </summary>
    public List<NotificationActionDto>? DefaultActions { get; init; }

    /// <summary>
    /// Default resolution conditions for automatic archival
    /// </summary>
    public ResolutionConditions? DefaultResolutionConditions { get; init; }
}
