using Nocturne.Core.Contracts.Notifications;
using Nocturne.Core.Models;
using Nocturne.Core.Models.ClientDevices;
using Nocturne.Infrastructure.Data.Abstractions;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.API.Services.Realtime;

namespace Nocturne.API.Services.Notifications;

/// <summary>
/// Service implementation for managing in-app notifications. Persists notifications via
/// <see cref="IInAppNotificationRepository"/>, broadcasts changes over SignalR via
/// <see cref="ISignalRBroadcastService"/>, applies type-specific defaults from
/// <see cref="INotificationTemplateRegistry"/>, and delegates action handling to registered
/// <see cref="INotificationActionHandler"/> implementations.
/// </summary>
/// <seealso cref="IInAppNotificationService"/>
public class InAppNotificationService : IInAppNotificationService
{
    private readonly IInAppNotificationRepository _repository;
    private readonly ISignalRBroadcastService _broadcastService;
    private readonly INotificationTemplateRegistry _templateRegistry;
    private readonly Dictionary<string, INotificationActionHandler> _actionHandlers;
    private readonly ILogger<InAppNotificationService> _logger;

    /// <summary>
    /// Most active notifications one source may hold for a user at once. A create that would
    /// exceed it archives the source's oldest active notifications as
    /// <see cref="NotificationArchiveReason.Superseded"/>.
    /// </summary>
    public const int MaxActiveNotificationsPerSource = 10;

    /// <summary>
    /// Initializes a new instance of the <see cref="InAppNotificationService"/> class.
    /// </summary>
    /// <param name="repository">The notification repository</param>
    /// <param name="broadcastService">The SignalR broadcast service</param>
    /// <param name="templateRegistry">The notification template registry for type defaults</param>
    /// <param name="actionHandlers">Registered notification action handlers</param>
    /// <param name="logger">The logger</param>
    public InAppNotificationService(
        IInAppNotificationRepository repository,
        ISignalRBroadcastService broadcastService,
        INotificationTemplateRegistry templateRegistry,
        IEnumerable<INotificationActionHandler> actionHandlers,
        ILogger<InAppNotificationService> logger
    )
    {
        _repository = repository;
        _broadcastService = broadcastService;
        _templateRegistry = templateRegistry;
        _actionHandlers = actionHandlers.ToDictionary(h => h.NotificationType, h => h);
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<List<InAppNotificationDto>> GetActiveNotificationsAsync(
        string userId,
        CancellationToken cancellationToken = default
    )
    {
        var entities = await _repository.GetActiveAsync(userId, cancellationToken);
        return entities.Select(InAppNotificationRepository.ToDto).ToList();
    }

    /// <inheritdoc />
    public async Task<InAppNotificationDto> CreateNotificationAsync(
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
    )
    {
        var template = _templateRegistry.GetTemplate(type);

        // Merge: caller values override template defaults
        var resolvedCategory = category
            ?? template?.Category
            ?? throw new ArgumentException($"No category provided and no template registered for type '{type}'", nameof(type));
        var resolvedUrgency = urgency ?? template?.DefaultUrgency ?? NotificationUrgency.Info;
        var resolvedIcon = icon ?? template?.Icon;
        var resolvedSource = source ?? template?.Source;
        var resolvedActions = actions ?? template?.DefaultActions;
        var resolvedConditions = resolutionConditions ?? template?.DefaultResolutionConditions;

        if (resolvedSource != null)
        {
            await SupersedeOldestOverCapAsync(userId, resolvedSource, cancellationToken);
        }

        var entity = new InAppNotificationEntity
        {
            UserId = userId,
            Type = type,
            Category = resolvedCategory,
            Urgency = resolvedUrgency,
            Icon = resolvedIcon,
            Source = resolvedSource,
            Title = title,
            Subtitle = subtitle,
            SourceId = sourceId,
            ActionsJson = InAppNotificationRepository.SerializeActions(resolvedActions),
            ResolutionConditionsJson = InAppNotificationRepository.SerializeConditions(resolvedConditions),
            MetadataJson = InAppNotificationRepository.SerializeMetadata(metadata)
        };

        var created = await _repository.CreateAsync(entity, cancellationToken);
        return await AnnounceAsync(userId, type, created);
    }

    /// <inheritdoc />
    public async Task<bool> CreateNotificationUnlessActiveAsync(
        string userId,
        string type,
        string title,
        string sourceId,
        NotificationCategory? category = null,
        string? source = null,
        string? subtitle = null,
        Dictionary<string, object>? metadata = null,
        CancellationToken cancellationToken = default
    )
    {
        if (await _repository.FindBySourceAsync(userId, type, sourceId, cancellationToken) is not null)
            return false;

        var template = _templateRegistry.GetTemplate(type);
        var resolvedCategory = category
            ?? template?.Category
            ?? throw new ArgumentException($"No category provided and no template registered for type '{type}'", nameof(type));
        var resolvedSource = source ?? template?.Source;
        if (resolvedSource != null)
        {
            await SupersedeOldestOverCapAsync(userId, resolvedSource, cancellationToken);
        }

        var created = await _repository.CreateUnlessActiveAsync(new InAppNotificationEntity
        {
            UserId = userId,
            Type = type,
            Category = resolvedCategory,
            Urgency = template?.DefaultUrgency ?? NotificationUrgency.Info,
            Icon = template?.Icon,
            Source = resolvedSource,
            Title = title,
            Subtitle = subtitle,
            SourceId = sourceId,
            ActionsJson = InAppNotificationRepository.SerializeActions(template?.DefaultActions),
            ResolutionConditionsJson = InAppNotificationRepository.SerializeConditions(template?.DefaultResolutionConditions),
            MetadataJson = InAppNotificationRepository.SerializeMetadata(metadata)
        }, cancellationToken);
        if (created is null)
            return false;

        await AnnounceAsync(userId, type, created);
        return true;
    }

    /// <inheritdoc />
    public Task<bool> ReplaceMetadataBySourceAsync(
        string userId,
        string type,
        string sourceId,
        Dictionary<string, object> metadata,
        CancellationToken cancellationToken = default
    ) =>
        _repository.ReplaceMetadataBySourceAsync(
            userId, type, sourceId, InAppNotificationRepository.SerializeMetadata(metadata), cancellationToken);

    private async Task<InAppNotificationDto> AnnounceAsync(string userId, string type, InAppNotificationEntity created)
    {
        var dto = InAppNotificationRepository.ToDto(created);

        _logger.LogInformation(
            "Created in-app notification {NotificationId} of type {Type} for user {UserId}",
            dto.Id,
            type,
            userId
        );

        await _broadcastService.BroadcastNotificationCreatedAsync(userId, dto);

        // Mirror qualifying non-alert notifications to the user's registered devices. Alerts reach
        // devices via device_action channels, so alert.firing is excluded by Qualifies.
        if (DeviceNotificationMirror.Qualifies(dto))
        {
            await _broadcastService.BroadcastDeviceNotificationAsync(
                new DeviceNotificationMirror { UserId = userId, Notification = dto });
        }

        return dto;
    }

    /// <summary>
    /// Archives a source's oldest active notifications so that one more create leaves the source
    /// at <see cref="MaxActiveNotificationsPerSource"/>.
    /// </summary>
    private async Task SupersedeOldestOverCapAsync(
        string userId,
        string source,
        CancellationToken cancellationToken
    )
    {
        var active = await _repository.GetActiveBySourceAsync(userId, source, cancellationToken);
        var overflow = active.Count - (MaxActiveNotificationsPerSource - 1);

        for (var i = 0; i < overflow; i++)
        {
            await ArchiveNotificationAsync(
                active[i].Id,
                NotificationArchiveReason.Superseded,
                userId,
                cancellationToken
            );
        }
    }

    /// <inheritdoc />
    public async Task<bool> ArchiveNotificationAsync(
        Guid notificationId,
        NotificationArchiveReason reason,
        string userId,
        CancellationToken cancellationToken = default
    )
    {
        var notification = await _repository.GetByIdAsync(notificationId, cancellationToken);

        if (notification == null)
        {
            _logger.LogWarning(
                "Attempted to archive non-existent notification {NotificationId}",
                notificationId
            );
            return false;
        }

        if (notification.UserId != userId)
        {
            _logger.LogWarning(
                "User {UserId} attempted to archive notification {NotificationId} belonging to another user",
                userId,
                notificationId
            );
            return false;
        }

        var archived = await _repository.ArchiveAsync(notificationId, reason, cancellationToken);

        if (archived == null)
        {
            _logger.LogWarning(
                "Attempted to archive non-existent notification {NotificationId}",
                notificationId
            );
            return false;
        }

        _logger.LogInformation(
            "Archived notification {NotificationId} with reason {Reason}",
            notificationId,
            reason
        );

        await _broadcastService.BroadcastNotificationArchivedAsync(
            archived.UserId, InAppNotificationRepository.ToDto(archived), reason);

        return true;
    }

    /// <inheritdoc />
    public async Task<bool> MarkAsReadAsync(
        Guid notificationId,
        string userId,
        CancellationToken cancellationToken = default
    )
    {
        var notification = await _repository.GetByIdAsync(notificationId, cancellationToken);

        if (notification == null || notification.UserId != userId)
        {
            return false;
        }

        // Already read — nothing to update or broadcast.
        if (notification.ReadAt != null)
        {
            return true;
        }

        var updated = await _repository.MarkAsReadAsync(notificationId, cancellationToken);
        if (updated == null)
        {
            return false;
        }

        await BroadcastUpdatedAsync(updated);
        return true;
    }

    /// <inheritdoc />
    public async Task<int> MarkAllAsReadAsync(
        string userId,
        CancellationToken cancellationToken = default
    )
    {
        var updated = await _repository.MarkAllAsReadAsync(userId, cancellationToken);

        foreach (var entity in updated)
        {
            await BroadcastUpdatedAsync(entity);
        }

        _logger.LogInformation(
            "Marked {Count} notifications read for user {UserId}",
            updated.Count,
            userId
        );

        return updated.Count;
    }

    private Task BroadcastUpdatedAsync(InAppNotificationEntity entity) =>
        _broadcastService.BroadcastNotificationUpdatedAsync(
            entity.UserId, InAppNotificationRepository.ToDto(entity));

    /// <inheritdoc />
    public async Task<string?> GetNotificationTypeAsync(
        Guid notificationId,
        string userId,
        CancellationToken cancellationToken = default
    )
    {
        var notification = await _repository.GetByIdAsync(notificationId, cancellationToken);

        if (notification == null || notification.UserId != userId)
        {
            return null;
        }

        return notification.Type;
    }

    /// <inheritdoc />
    public async Task<NotificationActionResult> ExecuteActionAsync(
        Guid notificationId,
        string actionId,
        string userId,
        CancellationToken cancellationToken = default
    )
    {
        var notification = await _repository.GetByIdAsync(notificationId, cancellationToken);

        if (notification == null)
        {
            _logger.LogWarning(
                "Attempted to execute action on non-existent notification {NotificationId}",
                notificationId
            );
            return NotificationActionResult.NotHandled;
        }

        // Verify the notification belongs to the user
        if (notification.UserId != userId)
        {
            _logger.LogWarning(
                "User {UserId} attempted to execute action on notification {NotificationId} belonging to another user",
                userId,
                notificationId
            );
            return NotificationActionResult.NotHandled;
        }

        _logger.LogDebug(
            "Executing action {ActionId} on notification {NotificationId}",
            actionId,
            notificationId
        );

        var builtInArchive = actionId.ToLowerInvariant() switch
        {
            "dismiss" => NotificationArchiveReason.Dismissed,
            "navigate" => NotificationArchiveReason.Completed,
            _ => (NotificationArchiveReason?)null,
        };

        if (_actionHandlers.TryGetValue(notification.Type, out var handler))
        {
            var metadata = InAppNotificationRepository.DeserializeMetadata(notification.MetadataJson);
            var result = await handler.HandleAsync(
                notificationId,
                actionId,
                userId,
                notification.SourceId,
                metadata,
                cancellationToken
            );

            if (result.Handled || result.RefusedScope is not null || builtInArchive is null)
            {
                if (result.RefusedScope is null && result.Archive is { } reason)
                {
                    await ArchiveNotificationAsync(notificationId, reason, userId, cancellationToken);
                }

                return result;
            }

            _logger.LogInformation(
                "Handler for type {Type} declined {ActionId}, archiving notification {NotificationId}",
                notification.Type,
                actionId,
                notificationId
            );
        }

        var archiveReason = builtInArchive ?? NotificationArchiveReason.Completed;
        var archived = await ArchiveNotificationAsync(notificationId, archiveReason, userId, cancellationToken);
        return new NotificationActionResult(archived, archived ? archiveReason : null);
    }

    /// <inheritdoc />
    public async Task<bool> ArchiveBySourceAsync(
        string userId,
        string type,
        string sourceId,
        NotificationArchiveReason reason,
        CancellationToken cancellationToken = default
    )
    {
        var notification = await _repository.FindBySourceAsync(
            userId,
            type,
            sourceId,
            cancellationToken
        );

        if (notification == null)
        {
            _logger.LogDebug(
                "No active notification found for user {UserId}, type {Type}, source {SourceId}",
                userId,
                type,
                sourceId
            );
            return false;
        }

        return await ArchiveNotificationAsync(notification.Id, reason, userId, cancellationToken);
    }
}
