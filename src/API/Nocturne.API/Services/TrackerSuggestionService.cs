using System.Text.Json;
using Nocturne.API.Controllers.V4;
using Nocturne.Core.Contracts;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Repositories;

namespace Nocturne.API.Services;

/// <summary>
/// Service for creating tracker reset suggestions based on detected events
/// </summary>
public class TrackerSuggestionService : ITrackerSuggestionService
{
    private readonly TrackerRepository _trackerRepository;
    private readonly InAppNotificationRepository _notificationRepository;
    private readonly ISignalRBroadcastService _broadcastService;
    private readonly ILogger<TrackerSuggestionService> _logger;

    /// <summary>
    /// Hours before expected end to consider a sensor "near end-of-life" for warmup detection
    /// </summary>
    private const int SensorEndOfLifeWindowHours = 8;

    /// <summary>
    /// Minimum gap duration in minutes to consider as potential sensor warmup
    /// </summary>
    private const int MinimumGapMinutesForWarmup = 60;

    /// <summary>
    /// Cooldown period in hours to prevent duplicate suggestions for the same tracker
    /// </summary>
    private const int SuggestionCooldownHours = 1;

    public TrackerSuggestionService(
        TrackerRepository trackerRepository,
        InAppNotificationRepository notificationRepository,
        ISignalRBroadcastService broadcastService,
        ILogger<TrackerSuggestionService> logger
    )
    {
        _trackerRepository = trackerRepository;
        _notificationRepository = notificationRepository;
        _broadcastService = broadcastService;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task EvaluateTreatmentForTrackerSuggestionAsync(
        Treatment treatment,
        string userId,
        CancellationToken cancellationToken = default
    )
    {
        // Only process Site Change treatments
        if (!IsSiteChangeTreatment(treatment.EventType))
            return;

        _logger.LogDebug(
            "Evaluating Site Change treatment {TreatmentId} for tracker suggestions",
            treatment.Id
        );

        // Get all Cannula-category tracker definitions for this user
        var cannulaDefinitions = await _trackerRepository.GetDefinitionsByCategoryAsync(
            userId,
            TrackerCategory.Cannula,
            cancellationToken
        );

        if (cannulaDefinitions.Count == 0)
        {
            _logger.LogDebug("No Cannula-category trackers found for user {UserId}", userId);
            return;
        }

        foreach (var definition in cannulaDefinitions)
        {
            // Check if we already have a recent suggestion for this tracker
            if (await HasRecentSuggestionAsync(userId, definition.Id, cancellationToken))
            {
                _logger.LogDebug(
                    "Skipping suggestion for tracker {TrackerName} - recent suggestion exists",
                    definition.Name
                );
                continue;
            }

            // Create the suggestion notification
            await CreateTrackerSuggestionNotificationAsync(
                userId,
                definition.Id,
                definition.Name,
                TrackerSuggestionReason.SiteChangeTreatment,
                treatment.Id,
                null,
                null,
                cancellationToken
            );

            _logger.LogInformation(
                "Created tracker suggestion for {TrackerName} based on Site Change treatment {TreatmentId}",
                definition.Name,
                treatment.Id
            );
        }
    }

    /// <inheritdoc />
    public async Task EvaluateDataGapForTrackerSuggestionAsync(
        string userId,
        DateTime gapStart,
        DateTime gapEnd,
        CancellationToken cancellationToken = default
    )
    {
        var gapMinutes = (gapEnd - gapStart).TotalMinutes;

        // Only consider gaps of 60+ minutes
        if (gapMinutes < MinimumGapMinutesForWarmup)
        {
            _logger.LogDebug(
                "Data gap of {GapMinutes} minutes is below threshold for sensor warmup detection",
                gapMinutes
            );
            return;
        }

        _logger.LogDebug(
            "Evaluating {GapMinutes} minute data gap for sensor tracker suggestions",
            gapMinutes
        );

        // Get all Sensor-category tracker definitions for this user
        var sensorDefinitions = await _trackerRepository.GetDefinitionsByCategoryAsync(
            userId,
            TrackerCategory.Sensor,
            cancellationToken
        );

        if (sensorDefinitions.Count == 0)
        {
            _logger.LogDebug("No Sensor-category trackers found for user {UserId}", userId);
            return;
        }

        foreach (var definition in sensorDefinitions)
        {
            // Get active instances for this definition
            var activeInstances = await _trackerRepository.GetActiveInstancesForDefinitionAsync(
                definition.Id,
                cancellationToken
            );

            foreach (var instance in activeInstances)
            {
                // Check if the tracker is near end-of-life or past expiration
                if (!IsTrackerNearEndOfLife(instance.StartedAt, definition.LifespanHours, gapStart))
                {
                    _logger.LogDebug(
                        "Tracker instance {InstanceId} is not near end-of-life, skipping suggestion",
                        instance.Id
                    );
                    continue;
                }

                // Check if we already have a recent suggestion for this tracker
                if (await HasRecentSuggestionAsync(userId, definition.Id, cancellationToken))
                {
                    _logger.LogDebug(
                        "Skipping suggestion for tracker {TrackerName} - recent suggestion exists",
                        definition.Name
                    );
                    continue;
                }

                // Create the suggestion notification
                await CreateTrackerSuggestionNotificationAsync(
                    userId,
                    definition.Id,
                    definition.Name,
                    TrackerSuggestionReason.SensorWarmupDetected,
                    null,
                    gapStart,
                    gapEnd,
                    cancellationToken
                );

                _logger.LogInformation(
                    "Created tracker suggestion for {TrackerName} based on {GapMinutes} minute data gap (likely sensor warmup)",
                    definition.Name,
                    gapMinutes
                );
            }
        }
    }

    /// <inheritdoc />
    public async Task<bool> AcceptSuggestionAsync(
        Guid notificationId,
        string userId,
        CancellationToken cancellationToken = default
    )
    {
        var notification = await _notificationRepository.GetByIdAsync(notificationId, cancellationToken);

        if (notification == null)
        {
            _logger.LogWarning("Notification {NotificationId} not found", notificationId);
            return false;
        }

        if (notification.UserId != userId)
        {
            _logger.LogWarning(
                "User {UserId} attempted to accept notification {NotificationId} belonging to another user",
                userId,
                notificationId
            );
            return false;
        }

        if (notification.Type != InAppNotificationType.SuggestedTrackerMatch)
        {
            _logger.LogWarning(
                "Notification {NotificationId} is not a SuggestedTrackerMatch type",
                notificationId
            );
            return false;
        }

        // Parse the metadata to get the tracker definition ID
        var metadata = ParseMetadata(notification.MetadataJson);
        if (metadata == null || !metadata.TryGetValue("trackerDefinitionId", out var definitionIdObj))
        {
            _logger.LogWarning(
                "Notification {NotificationId} metadata missing trackerDefinitionId",
                notificationId
            );
            return false;
        }

        if (!Guid.TryParse(definitionIdObj?.ToString(), out var definitionId))
        {
            _logger.LogWarning(
                "Notification {NotificationId} has invalid trackerDefinitionId",
                notificationId
            );
            return false;
        }

        // Get the tracker definition
        var definition = await _trackerRepository.GetDefinitionByIdAsync(definitionId, cancellationToken);
        if (definition == null)
        {
            _logger.LogWarning("Tracker definition {DefinitionId} not found", definitionId);
            // Still archive the notification since the tracker no longer exists
            await ArchiveNotificationDirectlyAsync(
                notification,
                NotificationArchiveReason.Completed,
                cancellationToken
            );
            return false;
        }

        // Get active instances for this definition
        var activeInstances = await _trackerRepository.GetActiveInstancesForDefinitionAsync(
            definitionId,
            cancellationToken
        );

        // Complete any active instances
        foreach (var instance in activeInstances)
        {
            // Determine completion reason based on lifespan
            var completionReason = IsTrackerPastExpiration(instance.StartedAt, definition.LifespanHours)
                ? CompletionReason.Expired
                : CompletionReason.ReplacedEarly;

            var completedInstance = await _trackerRepository.CompleteInstanceAsync(
                instance.Id,
                completionReason,
                completionNotes: "Completed via tracker suggestion",
                cancellationToken: cancellationToken
            );

            if (completedInstance != null)
            {
                _logger.LogInformation(
                    "Completed tracker instance {InstanceId} with reason {Reason}",
                    instance.Id,
                    completionReason
                );

                // Broadcast the completion
                await _broadcastService.BroadcastTrackerUpdateAsync(
                    "update",
                    TrackerInstanceDto.FromEntity(completedInstance)
                );
            }
        }

        // Start a new instance
        var newInstance = await _trackerRepository.StartInstanceAsync(
            definitionId,
            userId,
            startNotes: "Started via tracker suggestion",
            cancellationToken: cancellationToken
        );

        _logger.LogInformation(
            "Started new tracker instance {InstanceId} for definition {DefinitionName}",
            newInstance.Id,
            definition.Name
        );

        // Broadcast the new instance
        await _broadcastService.BroadcastTrackerUpdateAsync(
            "create",
            TrackerInstanceDto.FromEntity(newInstance)
        );

        // Archive the notification
        await ArchiveNotificationDirectlyAsync(
            notification,
            NotificationArchiveReason.Completed,
            cancellationToken
        );

        return true;
    }

    /// <inheritdoc />
    public async Task<bool> DismissSuggestionAsync(
        Guid notificationId,
        string userId,
        CancellationToken cancellationToken = default
    )
    {
        var notification = await _notificationRepository.GetByIdAsync(notificationId, cancellationToken);

        if (notification == null)
        {
            _logger.LogWarning("Notification {NotificationId} not found", notificationId);
            return false;
        }

        if (notification.UserId != userId)
        {
            _logger.LogWarning(
                "User {UserId} attempted to dismiss notification {NotificationId} belonging to another user",
                userId,
                notificationId
            );
            return false;
        }

        return await ArchiveNotificationDirectlyAsync(
            notification,
            NotificationArchiveReason.Dismissed,
            cancellationToken
        );
    }

    /// <inheritdoc />
    public async Task EvaluateDataResumedForTrackerSuggestionAsync(
        string userId,
        DateTime? previousDataTime,
        DateTime newDataTime,
        CancellationToken cancellationToken = default
    )
    {
        // If we don't know when previous data was, we can't detect a gap
        if (!previousDataTime.HasValue)
            return;

        var gapMinutes = (newDataTime - previousDataTime.Value).TotalMinutes;

        // Only consider gaps of 60+ minutes as potential sensor warmup
        if (gapMinutes < MinimumGapMinutesForWarmup)
            return;

        _logger.LogDebug(
            "Data resumed after {GapMinutes} minute gap for user {UserId}, evaluating for sensor warmup",
            gapMinutes,
            userId
        );

        // Use the existing data gap evaluation method
        await EvaluateDataGapForTrackerSuggestionAsync(
            userId,
            previousDataTime.Value,
            newDataTime,
            cancellationToken
        );
    }

    #region Private Helpers

    private static bool IsSiteChangeTreatment(string? eventType)
    {
        if (string.IsNullOrEmpty(eventType))
            return false;

        return eventType.Equals("Site Change", StringComparison.OrdinalIgnoreCase) ||
               eventType.Equals("Cannula Change", StringComparison.OrdinalIgnoreCase);
    }

    private bool IsTrackerNearEndOfLife(DateTime startedAt, int? lifespanHours, DateTime referenceTime)
    {
        if (!lifespanHours.HasValue)
        {
            // No lifespan defined, can't determine end-of-life
            return false;
        }

        var expectedEnd = startedAt.AddHours(lifespanHours.Value);
        var hoursUntilEnd = (expectedEnd - referenceTime).TotalHours;

        // Near end-of-life if within window or already past expiration
        return hoursUntilEnd <= SensorEndOfLifeWindowHours;
    }

    private static bool IsTrackerPastExpiration(DateTime startedAt, int? lifespanHours)
    {
        if (!lifespanHours.HasValue)
            return false;

        var expectedEnd = startedAt.AddHours(lifespanHours.Value);
        return DateTime.UtcNow > expectedEnd;
    }

    private async Task<bool> HasRecentSuggestionAsync(
        string userId,
        Guid definitionId,
        CancellationToken cancellationToken
    )
    {
        // Check for existing active notification with the same tracker definition
        var existingNotification = await _notificationRepository.FindBySourceAsync(
            userId,
            InAppNotificationType.SuggestedTrackerMatch,
            definitionId.ToString(),
            cancellationToken
        );

        if (existingNotification != null)
        {
            // Check if within cooldown period
            var hoursSinceCreated = (DateTime.UtcNow - existingNotification.CreatedAt).TotalHours;
            return hoursSinceCreated < SuggestionCooldownHours;
        }

        return false;
    }

    private async Task CreateTrackerSuggestionNotificationAsync(
        string userId,
        Guid definitionId,
        string definitionName,
        TrackerSuggestionReason reason,
        string? treatmentId,
        DateTime? gapStart,
        DateTime? gapEnd,
        CancellationToken cancellationToken
    )
    {
        var title = reason == TrackerSuggestionReason.SiteChangeTreatment
            ? $"Reset {definitionName} tracker?"
            : $"Reset {definitionName} tracker?";

        var subtitle = reason == TrackerSuggestionReason.SiteChangeTreatment
            ? "You logged a Site Change"
            : "Sensor warmup detected";

        var metadata = new Dictionary<string, object>
        {
            ["trackerDefinitionId"] = definitionId.ToString(),
            ["trackerDefinitionName"] = definitionName,
            ["triggerReason"] = reason.ToString()
        };

        if (!string.IsNullOrEmpty(treatmentId))
        {
            metadata["treatmentId"] = treatmentId;
        }

        if (gapStart.HasValue)
        {
            metadata["gapStartTime"] = gapStart.Value.ToString("O");
        }

        if (gapEnd.HasValue)
        {
            metadata["gapEndTime"] = gapEnd.Value.ToString("O");
        }

        var actions = new List<NotificationActionDto>
        {
            new()
            {
                ActionId = "accept",
                Label = "Reset Tracker",
                Icon = "refresh-cw",
                Variant = "default"
            },
            new()
            {
                ActionId = "dismiss",
                Label = "Dismiss",
                Icon = "x",
                Variant = "outline"
            }
        };

        var entity = new InAppNotificationEntity
        {
            UserId = userId,
            Type = InAppNotificationType.SuggestedTrackerMatch,
            Urgency = NotificationUrgency.Info,
            Title = title,
            Subtitle = subtitle,
            SourceId = definitionId.ToString(), // Use definition ID as source for deduplication
            ActionsJson = InAppNotificationRepository.SerializeActions(actions),
            ResolutionConditionsJson = null,
            MetadataJson = InAppNotificationRepository.SerializeMetadata(metadata)
        };

        var created = await _notificationRepository.CreateAsync(entity, cancellationToken);
        var dto = InAppNotificationRepository.ToDto(created);

        _logger.LogInformation(
            "Created tracker suggestion notification {NotificationId} for user {UserId}",
            dto.Id,
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
    }

    /// <summary>
    /// Archives a notification directly using the repository and broadcasts the event.
    /// This avoids circular dependency with IInAppNotificationService.
    /// </summary>
    private async Task<bool> ArchiveNotificationDirectlyAsync(
        InAppNotificationEntity notification,
        NotificationArchiveReason reason,
        CancellationToken cancellationToken
    )
    {
        var archived = await _notificationRepository.ArchiveAsync(notification.Id, reason, cancellationToken);

        if (archived == null)
        {
            _logger.LogWarning(
                "Failed to archive notification {NotificationId}",
                notification.Id
            );
            return false;
        }

        _logger.LogInformation(
            "Archived notification {NotificationId} with reason {Reason}",
            notification.Id,
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
                notification.Id
            );
        }

        return true;
    }

    private static Dictionary<string, object>? ParseMetadata(string? json)
    {
        if (string.IsNullOrEmpty(json))
            return null;

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, object>>(json);
        }
        catch
        {
            return null;
        }
    }

    #endregion
}

/// <summary>
/// Reason why a tracker suggestion was created
/// </summary>
public enum TrackerSuggestionReason
{
    /// <summary>
    /// Suggestion triggered by a Site Change treatment
    /// </summary>
    SiteChangeTreatment,

    /// <summary>
    /// Suggestion triggered by detecting a sensor warmup (data gap near end-of-life)
    /// </summary>
    SensorWarmupDetected
}
