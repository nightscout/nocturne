using Microsoft.Extensions.DependencyInjection;
using Nocturne.Core.Contracts;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Repositories;

namespace Nocturne.API.Services;

/// <summary>
/// Service implementation for managing in-app notifications
/// </summary>
public class InAppNotificationService : IInAppNotificationService
{
    private readonly InAppNotificationRepository _repository;
    private readonly ISignalRBroadcastService _broadcastService;
    private readonly IConnectorFoodEntryRepository _foodEntryRepository;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<InAppNotificationService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="InAppNotificationService"/> class.
    /// </summary>
    /// <param name="repository">The notification repository</param>
    /// <param name="broadcastService">The SignalR broadcast service</param>
    /// <param name="foodEntryRepository">The food entry repository for meal matching dismiss</param>
    /// <param name="serviceProvider">Service provider for lazy resolution of domain services (avoids circular dependency)</param>
    /// <param name="logger">The logger</param>
    public InAppNotificationService(
        InAppNotificationRepository repository,
        ISignalRBroadcastService broadcastService,
        IConnectorFoodEntryRepository foodEntryRepository,
        IServiceProvider serviceProvider,
        ILogger<InAppNotificationService> logger
    )
    {
        _repository = repository;
        _broadcastService = broadcastService;
        _foodEntryRepository = foodEntryRepository;
        _serviceProvider = serviceProvider;
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
        InAppNotificationType type,
        NotificationUrgency urgency,
        string title,
        string? subtitle = null,
        string? sourceId = null,
        List<NotificationActionDto>? actions = null,
        ResolutionConditions? resolutionConditions = null,
        Dictionary<string, object>? metadata = null,
        CancellationToken cancellationToken = default
    )
    {
        var entity = new InAppNotificationEntity
        {
            UserId = userId,
            Type = type,
            Urgency = urgency,
            Title = title,
            Subtitle = subtitle,
            SourceId = sourceId,
            ActionsJson = InAppNotificationRepository.SerializeActions(actions),
            ResolutionConditionsJson = InAppNotificationRepository.SerializeConditions(resolutionConditions),
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
                // Archive the notification as dismissed
                return await ArchiveNotificationAsync(
                    notificationId,
                    NotificationArchiveReason.Dismissed,
                    cancellationToken
                );

            case "navigate":
                // Navigation is handled client-side, just mark as completed
                return await ArchiveNotificationAsync(
                    notificationId,
                    NotificationArchiveReason.Completed,
                    cancellationToken
                );

            default:
                // Check for domain-specific action handling
                if (notification.Type == InAppNotificationType.SuggestedMealMatch)
                {
                    switch (actionId.ToLowerInvariant())
                    {
                        case "accept":
                            // Accept action is handled via MealMatchingController
                            // Just archive the notification here
                            return await ArchiveNotificationAsync(
                                notificationId,
                                NotificationArchiveReason.Completed,
                                cancellationToken);

                        case "dismiss":
                            if (notification.SourceId != null && Guid.TryParse(notification.SourceId, out var foodEntryId))
                            {
                                // Mark the food entry as standalone directly to avoid circular dependency
                                await _foodEntryRepository.UpdateStatusAsync(
                                    foodEntryId,
                                    ConnectorFoodEntryStatus.Standalone,
                                    null,
                                    cancellationToken);
                            }
                            return await ArchiveNotificationAsync(
                                notificationId,
                                NotificationArchiveReason.Dismissed,
                                cancellationToken);

                        case "review":
                            // Review opens a dialog client-side, just return true
                            return true;
                    }
                }

                // Handle tracker suggestion actions
                if (notification.Type == InAppNotificationType.SuggestedTrackerMatch)
                {
                    // Lazy resolution to avoid circular dependency
                    var trackerSuggestionService = _serviceProvider.GetRequiredService<ITrackerSuggestionService>();

                    switch (actionId.ToLowerInvariant())
                    {
                        case "accept":
                            // Accept resets the tracker (completes current instance, starts new one)
                            return await trackerSuggestionService.AcceptSuggestionAsync(
                                notificationId,
                                userId,
                                cancellationToken);

                        case "dismiss":
                            // Dismiss just archives the notification
                            return await trackerSuggestionService.DismissSuggestionAsync(
                                notificationId,
                                userId,
                                cancellationToken);
                    }
                }

                // For other custom actions, log and mark as completed
                _logger.LogInformation(
                    "Executed custom action {ActionId} on notification {NotificationId}",
                    actionId,
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
        InAppNotificationType type,
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
}
