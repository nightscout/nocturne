using Nocturne.API.Controllers.V4.Monitoring;
using Nocturne.API.Services.Realtime;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Abstractions;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.API.Services.Monitoring;

/// <summary>
/// A new run of a Duration tracker replaces the one before it, whether a person started it or a
/// device event did: two runs of one sensor tracker would each fire the same threshold ladder.
/// Event trackers are exempt, because two booked appointments of one kind are both real.
/// </summary>
public static class TrackerSuccession
{
    /// <summary>
    /// Completes every running instance of <paramref name="definition"/> at
    /// <paramref name="startedAt"/>, ready for a new run to start there. Returns false, changing
    /// nothing, when a running instance started at or after that moment: the start is then history
    /// rather than a replacement, and completing the newer run would rewind the tracker.
    /// <paramref name="running"/> is the definition's running instances when the caller has already
    /// read them.
    /// </summary>
    public static async Task<bool> ReplaceRunningAsync(
        ITrackerRepository repository,
        ISignalRBroadcastService broadcast,
        ILogger logger,
        IReadOnlyList<TrackerInstanceEntity>? running,
        TrackerDefinitionEntity definition,
        DateTime startedAt,
        string? completionNotes,
        string? completeTreatmentId,
        CancellationToken ct)
    {
        if (definition.Mode != TrackerMode.Duration)
            return true;

        running ??= await repository.GetActiveInstancesForDefinitionAsync(definition.Id, ct);
        if (running.Any(i => i.StartedAt >= startedAt))
            return false;

        foreach (var instance in running)
        {
            var completed = await repository.CompleteInstanceAsync(
                instance.Id,
                TrackerSchedule.ReasonForReplacement(definition.LifespanHours, instance.StartedAt, startedAt),
                completionNotes,
                completeTreatmentId,
                startedAt,
                ct
            );

            if (completed is not null)
            {
                logger.LogInformation(
                    "Completed tracker instance {InstanceId} for {DefinitionName}, replaced by a run starting {StartedAt}",
                    completed.Id,
                    definition.Name,
                    startedAt
                );
                await broadcast.BroadcastTrackerUpdateAsync(
                    "complete",
                    TrackerInstanceDto.FromEntity(completed),
                    definition.UserId,
                    definition.Visibility
                );
            }
        }

        return true;
    }
}
