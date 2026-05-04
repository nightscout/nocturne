using System.Text.Json;
using Nocturne.Core.Contracts.Notifications;
using Nocturne.Core.Models;
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

        var resolvedSourceForRateLimit = source ?? template?.Source;
        if (resolvedSourceForRateLimit != null)
        {
            var activeCount = await _repository.GetActiveCountBySourceAsync(
                userId, resolvedSourceForRateLimit, cancellationToken);
            if (activeCount >= 10)
            {
                throw new InvalidOperationException(
                    $"Rate limit exceeded: source '{resolvedSourceForRateLimit}' has {activeCount} active notifications for user");
            }
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
        var dto = InAppNotificationRepository.ToDto(created);

        _logger.LogInformation(
            "Created in-app notification {NotificationId} of type {Type} for user {UserId}",
            dto.Id,
            type,
            userId
        );

        // Broadcast the notification created event
        try
        {
            await _broadcastService.BroadcastNotificationCreatedAsync(userId, dto);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to broadcast notification created event for {NotificationId}",
                dto.Id
            );
        }

        return dto;
    }

    /// <inheritdoc />
    public async Task<bool> ArchiveNotificationAsync(
        Guid notificationId,
        NotificationArchiveReason reason,
        CancellationToken cancellationToken = default
    )
    {
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

        // Broadcast the notification archived event
        var dto = InAppNotificationRepository.ToDto(archived);
        try
        {
            await _broadcastService.BroadcastNotificationArchivedAsync(archived.UserId, dto, reason);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to broadcast notification archived event for {NotificationId}",
                notificationId
            );
        }

        return true;
    }

    /// <inheritdoc />
    public async Task<bool> ExecuteActionAsync(
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
            return false;
        }

        // Verify the notification belongs to the user
        if (notification.UserId != userId)
        {
            _logger.LogWarning(
                "User {UserId} attempted to execute action on notification {NotificationId} belonging to another user",
                userId,
                notificationId
            );
            return false;
        }

        _logger.LogDebug(
            "Executing action {ActionId} on notification {NotificationId}",
            actionId,
            notificationId
        );

        // Handle built-in actions
        switch (actionId.ToLowerInvariant())
        {
            case "dismiss":
                return await ArchiveNotificationAsync(
                    notificationId,
                    NotificationArchiveReason.Dismissed,
                    cancellationToken
                );

            case "navigate":
                return await ArchiveNotificationAsync(
                    notificationId,
                    NotificationArchiveReason.Completed,
                    cancellationToken
                );

            default:
                // Dispatch to type-specific action handler if registered
                if (_actionHandlers.TryGetValue(notification.Type, out var handler))
                {
                    var metadata = DeserializeMetadata(notification.MetadataJson);
                    var result = await handler.HandleAsync(
                        notificationId,
                        actionId,
                        userId,
                        notification.SourceId,
                        metadata,
                        cancellationToken
                    );

                    if (result.Archive is { } reason)
                    {
                        await ArchiveNotificationAsync(notificationId, reason, cancellationToken);
                    }

                    return result.Handled;
                }

                // No handler registered — default to archiving as completed
                _logger.LogInformation(
                    "No action handler for type {Type}, archiving notification {NotificationId} as completed",
                    notification.Type,
                    notificationId
                );
                return await ArchiveNotificationAsync(
                    notificationId,
                    NotificationArchiveReason.Completed,
                    cancellationToken
                );
        }
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

        return await ArchiveNotificationAsync(notification.Id, reason, cancellationToken);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static Dictionary<string, object>? DeserializeMetadata(string? json)
    {
        if (string.IsNullOrEmpty(json))
            return null;

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, object>>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }
}
