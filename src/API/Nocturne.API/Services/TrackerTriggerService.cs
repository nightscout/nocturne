using System.Text.Json;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Repositories;

namespace Nocturne.API.Services;

/// <summary>
/// Service to automatically start tracker instances when matching treatments are created
/// </summary>
public interface ITrackerTriggerService
{
    /// <summary>
    /// Check if a treatment should trigger any tracker instances and start them
    /// </summary>
    Task ProcessTreatmentAsync(
        Treatment treatment,
        string? userId,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Process multiple treatments for tracker triggers
    /// </summary>
    Task ProcessTreatmentsAsync(
        IEnumerable<Treatment> treatments,
        string? userId,
        CancellationToken cancellationToken = default
    );
}

public class TrackerTriggerService : ITrackerTriggerService
{
    private readonly TrackerRepository _trackerRepository;
    private readonly ISignalRBroadcastService _broadcast;
    private readonly ILogger<TrackerTriggerService> _logger;

    public TrackerTriggerService(
        TrackerRepository trackerRepository,
        ISignalRBroadcastService broadcast,
        ILogger<TrackerTriggerService> logger
    )
    {
        _trackerRepository = trackerRepository;
        _broadcast = broadcast;
        _logger = logger;
    }

    public async Task ProcessTreatmentAsync(
        Treatment treatment,
        string? userId,
        CancellationToken cancellationToken = default
    )
    {
        if (string.IsNullOrEmpty(treatment.EventType) || string.IsNullOrEmpty(userId))
            return;

        await ProcessTreatmentInternalAsync(treatment, userId, cancellationToken);
    }

    public async Task ProcessTreatmentsAsync(
        IEnumerable<Treatment> treatments,
        string? userId,
        CancellationToken cancellationToken = default
    )
    {
        if (string.IsNullOrEmpty(userId))
            return;

        foreach (var treatment in treatments)
        {
            if (!string.IsNullOrEmpty(treatment.EventType))
            {
                await ProcessTreatmentInternalAsync(treatment, userId, cancellationToken);
            }
        }
    }

    private async Task ProcessTreatmentInternalAsync(
        Treatment treatment,
        string userId,
        CancellationToken cancellationToken
    )
    {
        // Get all definitions for this user that might be triggered
        var definitions = await _trackerRepository.GetDefinitionsForUserAsync(
            userId,
            cancellationToken
        );

        foreach (var definition in definitions)
        {
            var triggerEventTypes = ParseTriggerEventTypes(definition.TriggerEventTypes);
            if (triggerEventTypes.Count == 0)
                continue;

            // Check if this treatment's event type matches any trigger
            if (!triggerEventTypes.Contains(treatment.EventType, StringComparer.OrdinalIgnoreCase))
                continue;

            // Check if there's already an active instance for this definition
            var activeInstances = await _trackerRepository.GetActiveInstancesForDefinitionAsync(
                definition.Id,
                cancellationToken
            );

            if (activeInstances.Count > 0)
            {
                // Complete the existing active instance(s) before starting a new one
                foreach (var activeInstance in activeInstances)
                {
                    var completedAt = DateTimeOffset
                        .FromUnixTimeMilliseconds(treatment.Mills)
                        .UtcDateTime;

                    await _trackerRepository.CompleteInstanceAsync(
                        activeInstance.Id,
                        CompletionReason.Completed,
                        completionNotes: $"Auto-completed by new {treatment.EventType}",
                        completeTreatmentId: treatment.Id,
                        completedAt: completedAt,
                        cancellationToken: cancellationToken
                    );

                    _logger.LogInformation(
                        "Auto-completed tracker instance {InstanceId} for definition {DefinitionName} due to new treatment",
                        activeInstance.Id,
                        definition.Name
                    );

                    // Broadcast the completion
                    await _broadcast.BroadcastTrackerUpdateAsync(
                        "update",
                        Nocturne.API.Controllers.V4.TrackerInstanceDto.FromEntity(activeInstance)
                    );
                }
            }

            // Start a new instance
            var startedAt = DateTimeOffset.FromUnixTimeMilliseconds(treatment.Mills).UtcDateTime;

            var newInstance = await _trackerRepository.StartInstanceAsync(
                definition.Id,
                userId,
                startNotes: null,
                startTreatmentId: treatment.Id,
                startedAt: startedAt,
                cancellationToken: cancellationToken
            );

            _logger.LogInformation(
                "Auto-started tracker instance {InstanceId} for definition {DefinitionName} from treatment {TreatmentEventType}",
                newInstance.Id,
                definition.Name,
                treatment.EventType
            );

            // Broadcast the new instance
            await _broadcast.BroadcastTrackerUpdateAsync(
                "create",
                Nocturne.API.Controllers.V4.TrackerInstanceDto.FromEntity(newInstance)
            );
        }
    }

    private static List<string> ParseTriggerEventTypes(string? json)
    {
        if (string.IsNullOrEmpty(json))
            return [];

        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch
        {
            return [];
        }
    }
}
