namespace Nocturne.Widget.Contracts;

/// <summary>
/// Urgency level for system notifications
/// </summary>
public enum NotificationUrgency
{
    /// <summary>
    /// Informational notification
    /// </summary>
    Info,

    /// <summary>
    /// Warning level notification
    /// </summary>
    Warn,

    /// <summary>
    /// Hazard level notification (more urgent than warning)
    /// </summary>
    Hazard,

    /// <summary>
    /// Urgent notification (highest priority)
    /// </summary>
    Urgent,
}

/// <summary>
/// Interface for displaying system-level notifications (toast notifications)
/// </summary>
public interface ISystemNotificationService
{
    /// <summary>
    /// Shows an alarm notification with the specified parameters
    /// </summary>
    /// <param name="title">The notification title</param>
    /// <param name="message">The notification message</param>
    /// <param name="level">The alarm level (0-4, higher is more severe)</param>
    /// <param name="urgent">Whether the notification requires immediate attention</param>
    Task ShowAlarmNotificationAsync(string title, string message, int level, bool urgent);

    /// <summary>
    /// Shows a tracker alert notification
    /// </summary>
    /// <param name="trackerName">The name of the tracker that triggered the alert</param>
    /// <param name="message">The alert message</param>
    /// <param name="urgency">The urgency level of the notification</param>
    Task ShowTrackerAlertAsync(string trackerName, string message, NotificationUrgency urgency);

    /// <summary>
    /// Clears all pending notifications from this application
    /// </summary>
    Task ClearNotificationsAsync();
}
