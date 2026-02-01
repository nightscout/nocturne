using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.Infrastructure.Data.Repositories;

/// <summary>
/// PostgreSQL repository for in-app notification operations
/// </summary>
public class InAppNotificationRepository
{
    private readonly NocturneDbContext _context;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    /// <summary>
    /// Initializes a new instance of the <see cref="InAppNotificationRepository"/> class.
    /// </summary>
    /// <param name="context">The database context.</param>
    public InAppNotificationRepository(NocturneDbContext context)
    {
        _context = context;
    }

    /// <summary>
    /// Get all active (non-archived) notifications for a user
    /// </summary>
    /// <param name="userId">The user ID to get notifications for</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of active notifications ordered by urgency and creation time</returns>
    public virtual async Task<List<InAppNotificationEntity>> GetActiveAsync(
        string userId,
        CancellationToken cancellationToken = default
    )
    {
        return await _context
            .InAppNotifications
            .Where(n => n.UserId == userId && !n.IsArchived)
            .OrderByDescending(n => n.Urgency)
            .ThenByDescending(n => n.CreatedAt)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Get a specific notification by ID
    /// </summary>
    /// <param name="id">The notification ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The notification if found, null otherwise</returns>
    public virtual async Task<InAppNotificationEntity?> GetByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default
    )
    {
        return await _context
            .InAppNotifications
            .FirstOrDefaultAsync(n => n.Id == id, cancellationToken);
    }

    /// <summary>
    /// Get all non-archived notifications that have resolution conditions defined
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of notifications with pending resolution conditions</returns>
    public virtual async Task<List<InAppNotificationEntity>> GetPendingResolutionAsync(
        CancellationToken cancellationToken = default
    )
    {
        return await _context
            .InAppNotifications
            .Where(n => !n.IsArchived && n.ResolutionConditionsJson != null)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Create a new notification
    /// </summary>
    /// <param name="entity">The notification entity to create</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The created notification entity</returns>
    public virtual async Task<InAppNotificationEntity> CreateAsync(
        InAppNotificationEntity entity,
        CancellationToken cancellationToken = default
    )
    {
        entity.Id = Guid.CreateVersion7();
        entity.CreatedAt = DateTime.UtcNow;

        _context.InAppNotifications.Add(entity);
        await _context.SaveChangesAsync(cancellationToken);

        return entity;
    }

    /// <summary>
    /// Archive a notification with a reason
    /// </summary>
    /// <param name="id">The notification ID to archive</param>
    /// <param name="reason">The reason for archiving</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The archived notification entity if found, null otherwise</returns>
    public virtual async Task<InAppNotificationEntity?> ArchiveAsync(
        Guid id,
        NotificationArchiveReason reason,
        CancellationToken cancellationToken = default
    )
    {
        var entity = await _context
            .InAppNotifications
            .FirstOrDefaultAsync(n => n.Id == id, cancellationToken);

        if (entity == null)
            return null;

        entity.IsArchived = true;
        entity.ArchivedAt = DateTime.UtcNow;
        entity.ArchiveReason = reason;

        await _context.SaveChangesAsync(cancellationToken);

        return entity;
    }

    /// <summary>
    /// Find an active notification by source
    /// </summary>
    /// <param name="userId">The user ID</param>
    /// <param name="type">The notification type</param>
    /// <param name="sourceId">The source entity ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The notification if found, null otherwise</returns>
    public virtual async Task<InAppNotificationEntity?> FindBySourceAsync(
        string userId,
        InAppNotificationType type,
        string sourceId,
        CancellationToken cancellationToken = default
    )
    {
        return await _context
            .InAppNotifications
            .FirstOrDefaultAsync(
                n => n.UserId == userId
                     && n.Type == type
                     && n.SourceId == sourceId
                     && !n.IsArchived,
                cancellationToken
            );
    }

    /// <summary>
    /// Convert an entity to a DTO
    /// </summary>
    /// <param name="entity">The entity to convert</param>
    /// <returns>The DTO representation</returns>
    public static InAppNotificationDto ToDto(InAppNotificationEntity entity)
    {
        var actions = new List<NotificationActionDto>();

        if (!string.IsNullOrEmpty(entity.ActionsJson))
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<List<NotificationActionDto>>(
                    entity.ActionsJson,
                    JsonOptions
                );
                if (parsed != null)
                {
                    actions = parsed;
                }
            }
            catch
            {
                // Ignore deserialization errors, return empty actions list
            }
        }

        Dictionary<string, object>? metadata = null;
        if (!string.IsNullOrEmpty(entity.MetadataJson))
        {
            try
            {
                metadata = JsonSerializer.Deserialize<Dictionary<string, object>>(
                    entity.MetadataJson,
                    JsonOptions
                );
            }
            catch
            {
                // Ignore deserialization errors
            }
        }

        return new InAppNotificationDto
        {
            Id = entity.Id,
            Type = entity.Type,
            Urgency = entity.Urgency,
            Title = entity.Title,
            Subtitle = entity.Subtitle,
            CreatedAt = entity.CreatedAt,
            SourceId = entity.SourceId,
            Metadata = metadata,
            Actions = actions
        };
    }

    /// <summary>
    /// Serialize metadata to JSON
    /// </summary>
    /// <param name="metadata">The metadata to serialize</param>
    /// <returns>JSON string representation</returns>
    public static string? SerializeMetadata(Dictionary<string, object>? metadata)
    {
        if (metadata == null || metadata.Count == 0)
            return null;

        return JsonSerializer.Serialize(metadata, JsonOptions);
    }

    /// <summary>
    /// Serialize a list of actions to JSON
    /// </summary>
    /// <param name="actions">The actions to serialize</param>
    /// <returns>JSON string representation</returns>
    public static string? SerializeActions(List<NotificationActionDto>? actions)
    {
        if (actions == null || actions.Count == 0)
            return null;

        return JsonSerializer.Serialize(actions, JsonOptions);
    }

    /// <summary>
    /// Serialize resolution conditions to JSON
    /// </summary>
    /// <param name="conditions">The conditions to serialize</param>
    /// <returns>JSON string representation</returns>
    public static string? SerializeConditions(ResolutionConditions? conditions)
    {
        if (conditions == null)
            return null;

        return JsonSerializer.Serialize(conditions, JsonOptions);
    }

    /// <summary>
    /// Deserialize resolution conditions from JSON
    /// </summary>
    /// <param name="json">The JSON string to deserialize</param>
    /// <returns>The deserialized conditions, or null if invalid</returns>
    public static ResolutionConditions? DeserializeConditions(string? json)
    {
        if (string.IsNullOrEmpty(json))
            return null;

        try
        {
            return JsonSerializer.Deserialize<ResolutionConditions>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }
}
