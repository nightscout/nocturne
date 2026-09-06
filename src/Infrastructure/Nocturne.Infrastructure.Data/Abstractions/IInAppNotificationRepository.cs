using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.Infrastructure.Data.Abstractions;

/// <summary>
/// Repository port for in-app notification operations
/// </summary>
public interface IInAppNotificationRepository
{
    /// <summary>
    /// Gets all active notifications for a specific user
    /// </summary>
    Task<List<InAppNotificationEntity>> GetActiveAsync(
        string userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a notification by its unique identifier
    /// </summary>
    Task<InAppNotificationEntity?> GetByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all notifications that are pending resolution
    /// </summary>
    Task<List<InAppNotificationEntity>> GetPendingResolutionAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a new in-app notification
    /// </summary>
    Task<InAppNotificationEntity> CreateAsync(
        InAppNotificationEntity entity,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Archives a notification with a specified reason
    /// </summary>
    Task<InAppNotificationEntity?> ArchiveAsync(
        Guid id,
        NotificationArchiveReason reason,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks a single notification read. Returns the entity, or null if not found.
    /// A notification that is already read is returned unchanged.
    /// </summary>
    Task<InAppNotificationEntity?> MarkAsReadAsync(
        Guid id,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks all of a user's active, currently-unread notifications read.
    /// Returns the notifications that were updated (empty if none were unread).
    /// </summary>
    Task<List<InAppNotificationEntity>> MarkAllAsReadAsync(
        string userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a user's active (non-archived) notifications from a specific source, oldest first
    /// </summary>
    Task<List<InAppNotificationEntity>> GetActiveBySourceAsync(
        string userId,
        string source,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes archived notifications older than the specified cutoff date
    /// </summary>
    Task<int> DeleteArchivedBeforeAsync(
        DateTime cutoff,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Finds a notification by its source identifier and type for a user
    /// </summary>
    Task<InAppNotificationEntity?> FindBySourceAsync(
        string userId,
        string type,
        string sourceId,
        CancellationToken cancellationToken = default);
}
