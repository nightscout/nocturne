using Nocturne.Core.Models;

namespace Nocturne.Core.Contracts.Notifications;

/// <summary>
/// Service interface for V1 notification operations with 1:1 legacy JavaScript compatibility
/// Handles notification acknowledgments and admin notifications
/// Based on the legacy notifications.js and adminnotifies.js implementations
/// </summary>
public interface INotificationV1Service
{
    /// <summary>
    /// Acknowledges a notification alarm to silence it
    /// Implements the legacy notifications.ack() functionality
    /// </summary>
    /// <param name="request">Acknowledgment request details</param>
    /// <returns>Acknowledgment response</returns>
    Task<NotificationAckResponse> AckNotificationAsync(
        NotificationAckRequest request,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Gets all admin notifications with their counts and timestamps
    /// Implements the legacy adminnotifies.getNotifies() functionality with admin permission check
    /// </summary>
    /// <param name="subjectId">Subject ID for authorization check (implements legacy ctx.authorization.resolveWithRequest)</param>
    Task<AdminNotifiesResponse> GetAdminNotifiesAsync(
        string? subjectId,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Adds a new admin notification or increments count if it already exists
    /// Implements the legacy adminnotifies.addNotify() functionality
    /// </summary>
    /// <returns>Success response</returns>
    Task<NotificationAckResponse> AddAdminNotificationAsync(
        AdminNotification notification,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Clears all admin notifications
    /// Implements the legacy adminnotifies.cleanAll() functionality
    /// </summary>
    Task ClearAllAdminNotificationsAsync(CancellationToken cancellationToken = default);

}
