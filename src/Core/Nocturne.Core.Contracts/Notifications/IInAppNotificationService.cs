using Nocturne.Core.Models;

namespace Nocturne.Core.Contracts.Notifications;

/// <summary>
/// Service for managing in-app notifications displayed to users
/// </summary>
public interface IInAppNotificationService
{
    /// <summary>
    /// Get all active (non-archived) notifications for a user
    /// </summary>
    /// <param name="userId">The user ID to get notifications for</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of active notifications</returns>
    Task<List<InAppNotificationDto>> GetActiveNotificationsAsync(
        string userId,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Create a new notification for a user
    /// </summary>
    /// <param name="userId">The user ID to create the notification for</param>
    /// <param name="type">The notification type string (e.g., "tracker.unconfigured", "SuggestedMealMatch")</param>
    /// <param name="title">The notification title</param>
    /// <param name="category">Optional category override (falls back to template default)</param>
    /// <param name="urgency">Optional urgency override (falls back to template default)</param>
    /// <param name="icon">Optional icon override (falls back to template default)</param>
    /// <param name="source">Optional source override (falls back to template default)</param>
    /// <param name="subtitle">Optional subtitle</param>
    /// <param name="sourceId">Optional source entity ID for grouping and resolution</param>
    /// <param name="actions">Optional list of available actions</param>
    /// <param name="resolutionConditions">Optional automatic resolution conditions</param>
    /// <param name="metadata">Optional notification-specific metadata</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The created notification</returns>
    Task<InAppNotificationDto> CreateNotificationAsync(
        string userId,
        string type,
        string title,
        NotificationCategory? category = null,
        NotificationUrgency? urgency = null,
        string? icon = null,
        string? source = null,
        string? subtitle = null,
        string? sourceId = null,
        List<NotificationActionDto>? actions = null,
        ResolutionConditions? resolutionConditions = null,
        Dictionary<string, object>? metadata = null,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Archive a notification with a reason
    /// </summary>
    /// <param name="notificationId">The notification ID to archive</param>
    /// <param name="reason">The reason for archiving</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if archived successfully, false if not found</returns>
    Task<bool> ArchiveNotificationAsync(
        Guid notificationId,
        NotificationArchiveReason reason,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Execute an action on a notification
    /// </summary>
    /// <param name="notificationId">The notification ID</param>
    /// <param name="actionId">The action ID to execute</param>
    /// <param name="userId">The user ID executing the action</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if action executed successfully, false otherwise</returns>
    Task<bool> ExecuteActionAsync(
        Guid notificationId,
        string actionId,
        string userId,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Mark a single notification read for the given user
    /// </summary>
    /// <param name="notificationId">The notification ID to mark read</param>
    /// <param name="userId">The user the notification must belong to</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if the notification was found and belongs to the user, false otherwise</returns>
    Task<bool> MarkAsReadAsync(
        Guid notificationId,
        string userId,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Mark all of a user's active, unread notifications read
    /// </summary>
    /// <param name="userId">The user whose notifications to mark read</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The number of notifications that were marked read</returns>
    Task<int> MarkAllAsReadAsync(
        string userId,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Archive a notification by its source
    /// </summary>
    /// <param name="userId">The user ID</param>
    /// <param name="type">The notification type string</param>
    /// <param name="sourceId">The source entity ID</param>
    /// <param name="reason">The reason for archiving</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if a notification was found and archived, false otherwise</returns>
    Task<bool> ArchiveBySourceAsync(
        string userId,
        string type,
        string sourceId,
        NotificationArchiveReason reason,
        CancellationToken cancellationToken = default
    );
}
