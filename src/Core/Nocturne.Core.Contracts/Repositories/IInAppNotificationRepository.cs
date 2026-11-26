using Nocturne.Core.Models;

namespace Nocturne.Core.Contracts.Repositories;

/// <summary>
/// Stores in-app notifications. A notification is a user's, but a read here returns the rows the
/// tenant filter, row level security and the history clamp leave, whoever they are for.
/// </summary>
public interface IInAppNotificationRepository
{
    /// <summary>The user's active notifications, most urgent first, then newest first.</summary>
    Task<IReadOnlyList<InAppNotification>> GetActiveAsync(
        string userId,
        CancellationToken cancellationToken = default);

    Task<InAppNotification?> GetByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default);

    /// <summary>The active notifications that carry resolution conditions.</summary>
    Task<IReadOnlyList<InAppNotification>> GetPendingResolutionAsync(
        CancellationToken cancellationToken = default);

    /// <summary>Stores <paramref name="notification"/> under a new id, created now.</summary>
    /// <returns>The notification as stored.</returns>
    Task<InAppNotification> CreateAsync(
        InAppNotification notification,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores <paramref name="notification"/> as <see cref="CreateAsync"/> does unless its user already
    /// has an active notification of its type and source id, deciding under a lock on that key so two
    /// concurrent callers create one.
    /// </summary>
    /// <returns>The created notification, or null when one already stood.</returns>
    Task<InAppNotification?> CreateUnlessActiveAsync(
        InAppNotification notification,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes every notification of <paramref name="userId"/> from <paramref name="source"/>, archived
    /// or not, and stores <paramref name="notifications"/> as they are, ids and timestamps kept, in one
    /// transaction. An unaudited hard delete, for the sample-data seeder's idempotent re-seed only.
    /// </summary>
    Task ReplaceBySourceAsync(
        string userId,
        string source,
        IReadOnlyList<InAppNotification> notifications,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the metadata of the user's active notification of <paramref name="type"/> and
    /// <paramref name="sourceId"/>.
    /// </summary>
    /// <returns>Whether an active notification was found.</returns>
    Task<bool> ReplaceMetadataBySourceAsync(
        string userId,
        string type,
        string sourceId,
        Dictionary<string, object> metadata,
        CancellationToken cancellationToken = default);

    /// <summary>Archives a notification with <paramref name="reason"/>, stamped archived now.</summary>
    /// <returns>The notification as stored, or null when there is none.</returns>
    Task<InAppNotification?> ArchiveAsync(
        Guid id,
        NotificationArchiveReason reason,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks a notification read. One that is already read is returned unchanged.
    /// </summary>
    /// <returns>The notification as stored, or null when there is none.</returns>
    Task<InAppNotification?> MarkAsReadAsync(
        Guid id,
        CancellationToken cancellationToken = default);

    /// <summary>Marks all of a user's active, unread notifications read.</summary>
    /// <returns>The notifications that were updated; empty when none was unread.</returns>
    Task<IReadOnlyList<InAppNotification>> MarkAllAsReadAsync(
        string userId,
        CancellationToken cancellationToken = default);

    /// <summary>A user's active notifications from <paramref name="source"/>, oldest first.</summary>
    Task<IReadOnlyList<InAppNotification>> GetActiveBySourceAsync(
        string userId,
        string source,
        CancellationToken cancellationToken = default);

    /// <summary>Deletes the archived notifications archived before <paramref name="cutoff"/>.</summary>
    /// <returns>The number deleted.</returns>
    Task<int> DeleteArchivedBeforeAsync(
        DateTime cutoff,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether a user has ever been sent a notification of this type and source identifier, read or
    /// archived included.
    /// </summary>
    Task<bool> AnyBySourceAsync(
        string userId,
        string type,
        string sourceId,
        CancellationToken cancellationToken = default);

    /// <summary>The user's active notification of this type and source identifier.</summary>
    Task<InAppNotification?> FindBySourceAsync(
        string userId,
        string type,
        string sourceId,
        CancellationToken cancellationToken = default);
}
