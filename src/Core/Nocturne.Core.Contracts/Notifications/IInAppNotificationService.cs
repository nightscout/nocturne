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
    Task<List<InAppNotificationDto>> GetActiveNotificationsAsync(
        string userId,
        CancellationToken cancellationToken = default
    );

    /// <param name="type">The notification type string (e.g., "tracker.unconfigured", "SuggestedMealMatch")</param>
    /// <param name="category">Optional category override (falls back to template default)</param>
    /// <param name="urgency">Optional urgency override (falls back to template default)</param>
    /// <param name="icon">Optional icon override (falls back to template default)</param>
    /// <param name="source">Optional source override (falls back to template default)</param>
    /// <param name="sourceId">Optional source entity ID for grouping and resolution</param>
    /// <param name="actions">Optional list of available actions</param>
    /// <param name="resolutionConditions">Optional automatic resolution conditions</param>
    /// <param name="metadata">Optional notification-specific metadata</param>
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
    /// Creates a notification unless the user already has an active one of
    /// <paramref name="type"/> and <paramref name="sourceId"/>; concurrent callers create one.
    /// </summary>
    /// <returns>Whether a notification was created.</returns>
    Task<bool> CreateNotificationUnlessActiveAsync(
        string userId,
        string type,
        string title,
        string sourceId,
        NotificationCategory? category = null,
        string? source = null,
        string? subtitle = null,
        Dictionary<string, object>? metadata = null,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Replaces the metadata of the user's active notification of <paramref name="type"/> and
    /// <paramref name="sourceId"/>, without announcing it again.
    /// </summary>
    /// <returns>Whether an active notification was found.</returns>
    Task<bool> ReplaceMetadataBySourceAsync(
        string userId,
        string type,
        string sourceId,
        Dictionary<string, object> metadata,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Archive a notification with a reason, confined to a notification the given user owns
    /// </summary>
    /// <param name="reason">The reason for archiving</param>
    /// <param name="userId">The user the notification must belong to</param>
    /// <returns>True if archived successfully, false if not found or owned by another user</returns>
    Task<bool> ArchiveNotificationAsync(
        Guid notificationId,
        NotificationArchiveReason reason,
        string userId,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Get the type discriminator of a notification the given user owns, for a caller that must
    /// authorize on the notification's type before executing an action on it
    /// </summary>
    /// <param name="userId">The user the notification must belong to</param>
    /// <returns>The notification type, or null if not found or owned by another user</returns>
    Task<string?> GetNotificationTypeAsync(
        Guid notificationId,
        string userId,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Runs an action on a notification the user owns. The handler registered for the notification's
    /// type gets every action first, including <c>dismiss</c> and <c>navigate</c>. Those two archive
    /// the notification themselves when the type has no handler or its handler does not handle them.
    /// </summary>
    /// <param name="userId">The user ID executing the action</param>
    /// <returns>
    /// <see cref="NotificationActionResult.Handled"/> when the action ran,
    /// <see cref="NotificationActionResult.RefusedScope"/> when the handler refused it for a scope
    /// the caller lacks, and <see cref="NotificationActionResult.NotHandled"/> when the notification
    /// is missing or not the user's, or no handler took the action.
    /// </returns>
    Task<NotificationActionResult> ExecuteActionAsync(
        Guid notificationId,
        string actionId,
        string userId,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Mark a single notification read for the given user
    /// </summary>
    /// <param name="userId">The user the notification must belong to</param>
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
    /// <returns>The number of notifications that were marked read</returns>
    Task<int> MarkAllAsReadAsync(
        string userId,
        CancellationToken cancellationToken = default
    );

    /// <param name="reason">The reason for archiving</param>
    /// <returns>True if a notification was found and archived, false otherwise</returns>
    Task<bool> ArchiveBySourceAsync(
        string userId,
        string type,
        string sourceId,
        NotificationArchiveReason reason,
        CancellationToken cancellationToken = default
    );
}
