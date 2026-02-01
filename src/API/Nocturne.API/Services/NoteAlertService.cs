using Nocturne.Core.Contracts;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Mappers;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Infrastructure.Data.Repositories.Interfaces;

namespace Nocturne.API.Services;

/// <summary>
/// Service for evaluating and processing note alerts based on tracker thresholds
/// </summary>
public class NoteAlertService : INoteAlertService
{
    private readonly INoteRepository _noteRepository;
    private readonly TrackerRepository _trackerRepository;
    private readonly IInAppNotificationService _notificationService;
    private readonly ILogger<NoteAlertService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="NoteAlertService"/> class.
    /// </summary>
    /// <param name="noteRepository">The note repository</param>
    /// <param name="trackerRepository">The tracker repository</param>
    /// <param name="notificationService">The in-app notification service</param>
    /// <param name="logger">The logger</param>
    public NoteAlertService(
        INoteRepository noteRepository,
        TrackerRepository trackerRepository,
        IInAppNotificationService notificationService,
        ILogger<NoteAlertService> logger)
    {
        _noteRepository = noteRepository;
        _trackerRepository = trackerRepository;
        _notificationService = notificationService;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IEnumerable<NoteAlert>> GetPendingNoteAlertsAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var alerts = new List<NoteAlert>();

        // Get all active tracker instances for the user
        var activeInstances = await _trackerRepository.GetActiveInstancesAsync(
            userId.ToString(),
            cancellationToken);

        if (activeInstances.Length == 0)
        {
            _logger.LogDebug("No active tracker instances found for user {UserId}", userId);
            return alerts;
        }

        // Get distinct tracker definition IDs from active instances
        var definitionIds = activeInstances
            .Select(i => i.DefinitionId)
            .Distinct()
            .ToList();

        // For each tracker definition, get notes linked to it
        foreach (var definitionId in definitionIds)
        {
            var notesForTracker = await _noteRepository.GetNotesLinkedToTrackerDefinitionAsync(
                definitionId,
                cancellationToken);

            // Get active instances for this specific definition
            var instancesForDefinition = activeInstances
                .Where(i => i.DefinitionId == definitionId)
                .ToList();

            foreach (var note in notesForTracker)
            {
                // Find the tracker link for this specific definition
                var trackerLink = note.TrackerLinks
                    .FirstOrDefault(tl => tl.TrackerDefinitionId == definitionId);

                if (trackerLink == null || trackerLink.Thresholds.Count == 0)
                {
                    continue;
                }

                // Check each instance against each threshold
                foreach (var instance in instancesForDefinition)
                {
                    foreach (var threshold in trackerLink.Thresholds)
                    {
                        var shouldFire = EvaluateThreshold(instance, threshold);

                        if (shouldFire)
                        {
                            var noteModel = NoteMapper.ToModel(note);
                            alerts.Add(new NoteAlert
                            {
                                NoteId = note.Id,
                                Note = noteModel,
                                TrackerInstanceId = instance.Id,
                                TrackerName = instance.Definition?.Name ?? "Unknown Tracker",
                                Urgency = threshold.Urgency,
                                ThresholdDescription = threshold.Description
                            });
                        }
                    }
                }
            }
        }

        return alerts;
    }

    /// <inheritdoc />
    public async Task ProcessNoteAlertsAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Starting note alert processing");

        // Get all users with active tracker instances by getting all active instances
        // and extracting unique user IDs
        var allActiveInstances = await _trackerRepository.GetActiveInstancesAsync(
            null, // null returns all users' instances where definition is public
            cancellationToken);

        // Also need to get instances for users with private trackers
        // Since GetActiveInstancesAsync with null only returns public trackers,
        // we need to query by unique user IDs from those results
        var userIds = allActiveInstances
            .Select(i => i.UserId)
            .Where(id => !string.IsNullOrEmpty(id))
            .Distinct()
            .ToList();

        _logger.LogDebug("Found {UserCount} users with active tracker instances", userIds.Count);

        foreach (var userId in userIds)
        {
            if (!Guid.TryParse(userId, out var userGuid))
            {
                _logger.LogWarning("Invalid user ID format: {UserId}", userId);
                continue;
            }

            try
            {
                var pendingAlerts = await GetPendingNoteAlertsAsync(userGuid, cancellationToken);

                foreach (var alert in pendingAlerts)
                {
                    await CreateNotificationForAlertAsync(userId, alert, cancellationToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Error processing note alerts for user {UserId}",
                    userId);
            }
        }

        _logger.LogDebug("Completed note alert processing");
    }

    /// <summary>
    /// Evaluate if a threshold should fire for a given tracker instance
    /// </summary>
    /// <param name="instance">The active tracker instance</param>
    /// <param name="threshold">The note tracker threshold to evaluate</param>
    /// <returns>True if the threshold condition is met</returns>
    private bool EvaluateThreshold(
        TrackerInstanceEntity instance,
        NoteTrackerThresholdEntity threshold)
    {
        var definition = instance.Definition;
        if (definition == null)
        {
            _logger.LogWarning(
                "Tracker instance {InstanceId} has no definition loaded",
                instance.Id);
            return false;
        }

        var hoursOffset = (double)threshold.HoursOffset;

        if (definition.Mode == TrackerMode.Duration)
        {
            // Duration mode: time since tracker started
            var hoursSinceStart = instance.AgeHours;

            if (hoursOffset >= 0)
            {
                // Positive offset: X hours after start
                // Fire if current time has passed the threshold
                return hoursSinceStart >= hoursOffset;
            }
            else
            {
                // Negative offset: X hours before expected end
                // Need lifespan to calculate expected end
                if (!definition.LifespanHours.HasValue)
                {
                    _logger.LogWarning(
                        "Negative threshold on tracker definition {DefinitionId} without lifespan",
                        definition.Id);
                    return false;
                }

                var expectedEndAt = instance.StartedAt.AddHours(definition.LifespanHours.Value);
                var hoursUntilEnd = (expectedEndAt - DateTime.UtcNow).TotalHours;

                // Fire if we are within the threshold hours before end
                // e.g., if hoursOffset is -2, fire when hoursUntilEnd <= 2
                return hoursUntilEnd <= Math.Abs(hoursOffset);
            }
        }
        else // Event mode
        {
            // Event mode: time relative to scheduled event
            if (!instance.ScheduledAt.HasValue)
            {
                _logger.LogWarning(
                    "Event mode tracker instance {InstanceId} has no ScheduledAt",
                    instance.Id);
                return false;
            }

            var hoursFromScheduled = (DateTime.UtcNow - instance.ScheduledAt.Value).TotalHours;

            // For event mode, positive means after event, negative means before
            // Threshold offset works the same way
            return hoursFromScheduled >= hoursOffset;
        }
    }

    /// <summary>
    /// Create an in-app notification for a note alert
    /// </summary>
    /// <param name="userId">The user ID to create the notification for</param>
    /// <param name="alert">The note alert to create a notification for</param>
    /// <param name="cancellationToken">Cancellation token</param>
    private async Task CreateNotificationForAlertAsync(
        string userId,
        NoteAlert alert,
        CancellationToken cancellationToken)
    {
        // Create a source ID that combines note ID and tracker instance ID for deduplication
        // This ensures we don't create duplicate notifications for the same note/instance/threshold combination
        var sourceId = $"note:{alert.NoteId}:instance:{alert.TrackerInstanceId}";

        // Check if a notification already exists for this source
        var existingNotifications = await _notificationService.GetActiveNotificationsAsync(
            userId,
            cancellationToken);

        var alreadyExists = existingNotifications.Any(n =>
            n.Type == InAppNotificationType.NoteReminder &&
            n.SourceId == sourceId);

        if (alreadyExists)
        {
            _logger.LogDebug(
                "Notification already exists for note {NoteId} and instance {InstanceId}",
                alert.NoteId,
                alert.TrackerInstanceId);
            return;
        }

        // Generate title from note title or first 50 chars of content
        var title = !string.IsNullOrWhiteSpace(alert.Note.Title)
            ? alert.Note.Title
            : alert.Note.Content.Length > 50
                ? alert.Note.Content[..50] + "..."
                : alert.Note.Content;

        // Generate subtitle with tracker context
        var subtitle = !string.IsNullOrWhiteSpace(alert.ThresholdDescription)
            ? $"{alert.TrackerName}: {alert.ThresholdDescription}"
            : $"Reminder for {alert.TrackerName}";

        // Define actions: View, Snooze, Dismiss
        var actions = new List<NotificationActionDto>
        {
            new()
            {
                ActionId = "view",
                Label = "View",
                Icon = "eye",
                Variant = "default"
            },
            new()
            {
                ActionId = "snooze",
                Label = "Snooze",
                Icon = "clock",
                Variant = "outline"
            },
            new()
            {
                ActionId = "dismiss",
                Label = "Dismiss",
                Icon = "x",
                Variant = "outline"
            }
        };

        // Add metadata for frontend handling
        var metadata = new Dictionary<string, object>
        {
            { "noteId", alert.NoteId.ToString() },
            { "trackerInstanceId", alert.TrackerInstanceId.ToString() },
            { "trackerName", alert.TrackerName }
        };

        try
        {
            await _notificationService.CreateNotificationAsync(
                userId,
                InAppNotificationType.NoteReminder,
                alert.Urgency,
                title,
                subtitle,
                sourceId,
                actions,
                null, // No automatic resolution conditions for note reminders
                metadata,
                cancellationToken);

            _logger.LogInformation(
                "Created note reminder notification for note {NoteId}, instance {InstanceId}, user {UserId}",
                alert.NoteId,
                alert.TrackerInstanceId,
                userId);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to create notification for note {NoteId}, instance {InstanceId}",
                alert.NoteId,
                alert.TrackerInstanceId);
        }
    }
}
