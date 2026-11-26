using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Mappers;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Infrastructure.Data.Repositories;

/// <summary>
/// PostgreSQL repository for in-app notification operations
/// </summary>
public class InAppNotificationRepository : IInAppNotificationRepository
{
    private readonly INocturneDbContextFactory _contexts;

    /// <summary>
    /// A notification's urgency as its position in <see cref="NotificationUrgency"/>, which is
    /// declared least urgent first. The column holds the urgency's name, so ordering by it
    /// directly would sort alphabetically.
    /// </summary>
    private static readonly Expression<Func<InAppNotificationEntity, int>> UrgencyRank = BuildUrgencyRank();

    private static Expression<Func<InAppNotificationEntity, int>> BuildUrgencyRank()
    {
        var notification = Expression.Parameter(typeof(InAppNotificationEntity), "n");
        var urgency = Expression.Property(notification, nameof(InAppNotificationEntity.Urgency));
        Expression rank = Expression.Constant(-1);
        foreach (var value in Enum.GetValues<NotificationUrgency>())
        {
            rank = Expression.Condition(
                Expression.Equal(urgency, Expression.Constant(value)),
                Expression.Constant((int)value),
                rank);
        }

        return Expression.Lambda<Func<InAppNotificationEntity, int>>(rank, notification);
    }

    public InAppNotificationRepository(INocturneDbContextFactory contexts)
    {
        _contexts = contexts ?? throw new ArgumentNullException(nameof(contexts));
    }

    /// <inheritdoc />
    public virtual async Task<IReadOnlyList<InAppNotification>> GetActiveAsync(
        string userId,
        CancellationToken cancellationToken = default
    )
    {
        await using var db = await _contexts.CreateAsync(cancellationToken);
        var rows = await db
            .InAppNotifications
            .Where(n => n.UserId == userId && !n.IsArchived)
            .OrderByDescending(UrgencyRank)
            .ThenByDescending(n => n.CreatedAt)
            .ToListAsync(cancellationToken);
        return [.. rows.Select(InAppNotificationMapper.ToDomain)];
    }

    /// <inheritdoc />
    public virtual async Task<InAppNotification?> GetByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default
    )
    {
        await using var db = await _contexts.CreateAsync(cancellationToken);
        var row = await db
            .InAppNotifications
            .FirstOrDefaultAsync(n => n.Id == id, cancellationToken);
        return row is null ? null : InAppNotificationMapper.ToDomain(row);
    }

    /// <inheritdoc />
    public virtual async Task<IReadOnlyList<InAppNotification>> GetPendingResolutionAsync(
        CancellationToken cancellationToken = default
    )
    {
        await using var db = await _contexts.CreateAsync(cancellationToken);
        var rows = await db
            .InAppNotifications
            .Where(n => !n.IsArchived && n.ResolutionConditionsJson != null)
            .ToListAsync(cancellationToken);
        return [.. rows.Select(InAppNotificationMapper.ToDomain)];
    }

    /// <inheritdoc />
    public virtual async Task<InAppNotification> CreateAsync(
        InAppNotification notification,
        CancellationToken cancellationToken = default
    )
    {
        await using var db = await _contexts.CreateAsync(cancellationToken);
        var entity = InAppNotificationMapper.ToEntity(notification);
        entity.Id = Guid.CreateVersion7();
        entity.CreatedAt = DateTime.UtcNow;

        db.InAppNotifications.Add(entity);
        await db.SaveChangesAsync(cancellationToken);

        return InAppNotificationMapper.ToDomain(entity);
    }

    private const int SourceLockClass = 0x4E49_4E53;

    /// <inheritdoc />
    public virtual async Task<InAppNotification?> CreateUnlessActiveAsync(
        InAppNotification notification,
        CancellationToken cancellationToken = default
    )
    {
        await using var db = await _contexts.CreateAsync(cancellationToken);
        return await db.ExecuteInTransactionAsync<InAppNotification?>(async token =>
        {
            await db.LockForTransactionAsync(
                SourceLockClass, $"{db.TenantId}|{notification.UserId}|{notification.Type}|{notification.SourceId}", token);
            if (await db.InAppNotifications.AnyAsync(
                    n => n.UserId == notification.UserId
                         && n.Type == notification.Type
                         && n.SourceId == notification.SourceId
                         && !n.IsArchived,
                    token))
                return null;

            var entity = InAppNotificationMapper.ToEntity(notification);
            entity.Id = Guid.CreateVersion7();
            entity.CreatedAt = DateTime.UtcNow;
            db.InAppNotifications.Add(entity);
            await db.SaveChangesAsync(token);
            return InAppNotificationMapper.ToDomain(entity);
        },
        verifySucceeded: async (created, token) =>
            created is not null && await db.InAppNotifications.AnyAsync(n => n.Id == created.Id, token),
        ct: cancellationToken);
    }

    /// <inheritdoc />
    public virtual async Task ReplaceBySourceAsync(
        string userId,
        string source,
        IReadOnlyList<InAppNotification> notifications,
        CancellationToken cancellationToken = default
    )
    {
        await using var db = await _contexts.CreateAsync(cancellationToken);
        await db.ExecuteInTransactionAsync(async token =>
        {
            await db.InAppNotifications.PurgeAsync(n => n.UserId == userId && n.Source == source, token);
            db.InAppNotifications.AddRange(notifications.Select(InAppNotificationMapper.ToEntity));
            await db.SaveChangesAsync(token);
        }, ct: cancellationToken);
    }

    /// <inheritdoc />
    public virtual async Task<bool> ReplaceMetadataBySourceAsync(
        string userId,
        string type,
        string sourceId,
        Dictionary<string, object> metadata,
        CancellationToken cancellationToken = default
    )
    {
        await using var db = await _contexts.CreateAsync(cancellationToken);
        var notification = await db.InAppNotifications.FirstOrDefaultAsync(
            n => n.UserId == userId && n.Type == type && n.SourceId == sourceId && !n.IsArchived,
            cancellationToken);
        if (notification is null)
            return false;

        notification.MetadataJson = InAppNotificationMapper.SerializeMetadata(metadata);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <inheritdoc />
    public virtual async Task<InAppNotification?> ArchiveAsync(
        Guid id,
        NotificationArchiveReason reason,
        CancellationToken cancellationToken = default
    )
    {
        await using var db = await _contexts.CreateAsync(cancellationToken);
        var entity = await db
            .InAppNotifications
            .FirstOrDefaultAsync(n => n.Id == id, cancellationToken);

        if (entity == null)
            return null;

        entity.IsArchived = true;
        entity.ArchivedAt = DateTime.UtcNow;
        entity.ArchiveReason = reason;

        await db.SaveChangesAsync(cancellationToken);

        return InAppNotificationMapper.ToDomain(entity);
    }

    /// <inheritdoc />
    public virtual async Task<InAppNotification?> MarkAsReadAsync(
        Guid id,
        CancellationToken cancellationToken = default
    )
    {
        await using var db = await _contexts.CreateAsync(cancellationToken);
        var entity = await db
            .InAppNotifications
            .FirstOrDefaultAsync(n => n.Id == id, cancellationToken);

        if (entity == null)
            return null;

        if (entity.ReadAt == null)
        {
            entity.ReadAt = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
        }

        return InAppNotificationMapper.ToDomain(entity);
    }

    /// <inheritdoc />
    public virtual async Task<IReadOnlyList<InAppNotification>> MarkAllAsReadAsync(
        string userId,
        CancellationToken cancellationToken = default
    )
    {
        await using var db = await _contexts.CreateAsync(cancellationToken);
        var unread = await db
            .InAppNotifications
            .Where(n => n.UserId == userId && !n.IsArchived && n.ReadAt == null)
            .ToListAsync(cancellationToken);

        if (unread.Count == 0)
            return [];

        var now = DateTime.UtcNow;
        foreach (var entity in unread)
            entity.ReadAt = now;

        await db.SaveChangesAsync(cancellationToken);

        return [.. unread.Select(InAppNotificationMapper.ToDomain)];
    }

    /// <inheritdoc />
    public virtual async Task<IReadOnlyList<InAppNotification>> GetActiveBySourceAsync(
        string userId, string source, CancellationToken cancellationToken = default)
    {
        await using var db = await _contexts.CreateAsync(cancellationToken);
        var rows = await db.InAppNotifications
            .Where(n => n.UserId == userId && n.Source == source && !n.IsArchived)
            .OrderBy(n => n.CreatedAt)
            .ThenBy(n => n.Id)
            .ToListAsync(cancellationToken);
        return [.. rows.Select(InAppNotificationMapper.ToDomain)];
    }

    /// <inheritdoc />
    public async Task<int> DeleteArchivedBeforeAsync(
        DateTime cutoff, CancellationToken cancellationToken = default)
    {
        await using var db = await _contexts.CreateAsync(cancellationToken);
        return await db.InAppNotifications
            .Where(n => n.IsArchived && n.ArchivedAt < cutoff)
            .ExecuteDeleteAsync(cancellationToken);
    }

    /// <inheritdoc />
    public virtual async Task<bool> AnyBySourceAsync(
        string userId,
        string type,
        string sourceId,
        CancellationToken cancellationToken = default
    )
    {
        await using var db = await _contexts.CreateAsync(cancellationToken);
        return await db.InAppNotifications.AnyAsync(
            n => n.UserId == userId && n.Type == type && n.SourceId == sourceId,
            cancellationToken
        );
    }

    /// <inheritdoc />
    public virtual async Task<InAppNotification?> FindBySourceAsync(
        string userId,
        string type,
        string sourceId,
        CancellationToken cancellationToken = default
    )
    {
        await using var db = await _contexts.CreateAsync(cancellationToken);
        var row = await db
            .InAppNotifications
            .FirstOrDefaultAsync(
                n => n.UserId == userId
                     && n.Type == type
                     && n.SourceId == sourceId
                     && !n.IsArchived,
                cancellationToken
            );
        return row is null ? null : InAppNotificationMapper.ToDomain(row);
    }
}
