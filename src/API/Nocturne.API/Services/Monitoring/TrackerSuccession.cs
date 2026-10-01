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
/// <remarks>
/// Every start goes through <see cref="StartAsync"/>, which reads the running instances, completes
/// them and starts the new run under the definition's lock
/// (<see cref="ITrackerRepository.ExecuteUnderDefinitionLockAsync{T}"/>). Two writers advancing one
/// tracker at once, a connector batch and a phone upload of the same site change say, would
/// otherwise both read the same run and both start a successor.
/// </remarks>
public static class TrackerSuccession
{
    /// <summary>
    /// Completes every running instance of a Duration <paramref name="definition"/> at
    /// <paramref name="startedAt"/> and starts the new run with <paramref name="start"/>, in one
    /// transaction. Starts nothing, completing nothing, when a running instance started at or after
    /// that moment: the start is then history rather than a replacement, and completing the newer
    /// run would rewind the tracker. Starts nothing when <paramref name="alreadyStarted"/> says a
    /// running instance already stands for this start, or when another writer completes a running
    /// instance first.
    /// </summary>
    public static async Task<TrackerSuccessionResult> StartAsync(
        ITrackerRepository repository,
        ISignalRBroadcastService broadcast,
        ILogger logger,
        TrackerDefinitionEntity definition,
        DateTime startedAt,
        string? completionNotes,
        string? completeTreatmentId,
        Func<CancellationToken, Task<TrackerInstanceEntity>> start,
        CancellationToken ct,
        Func<IReadOnlyList<TrackerInstanceEntity>, bool>? alreadyStarted = null)
    {
        var result = await repository.ExecuteUnderDefinitionLockAsync(
            definition.Id,
            async token =>
            {
                List<TrackerInstanceEntity> completed = [];
                if (definition.Mode == TrackerMode.Duration)
                {
                    var running = await repository.GetActiveInstancesForDefinitionAsync(definition.Id, token);
                    if (alreadyStarted?.Invoke(running) == true)
                        return new TrackerSuccessionResult(TrackerSuccessionOutcome.AlreadyStarted, null, completed);
                    if (running.Any(i => i.StartedAt >= startedAt))
                        return new TrackerSuccessionResult(TrackerSuccessionOutcome.NewerRunning, null, completed);

                    foreach (var instance in running)
                    {
                        var won = await repository.CompleteInstanceAsync(
                            instance.Id,
                            TrackerSchedule.ReasonForReplacement(definition.LifespanHours, instance.StartedAt, startedAt),
                            completionNotes,
                            completeTreatmentId,
                            startedAt,
                            token
                        );
                        if (won is null)
                            return new TrackerSuccessionResult(TrackerSuccessionOutcome.CompletedElsewhere, null, completed);
                        completed.Add(won);
                    }
                }

                return new TrackerSuccessionResult(TrackerSuccessionOutcome.Started, await start(token), completed);
            },
            async (attempt, token) =>
                attempt.Started is { } started
                && await repository.GetInstanceByIdAsync(started.Id, token) is not null,
            ct
        );

        foreach (var instance in result.Completed)
        {
            logger.LogInformation(
                "Completed tracker instance {InstanceId} for {DefinitionName}, replaced by a run starting {StartedAt}",
                instance.Id,
                definition.Name,
                startedAt
            );
            await broadcast.BroadcastTrackerUpdateAsync(
                "complete",
                TrackerInstanceDto.FromEntity(instance),
                definition.UserId,
                definition.Visibility
            );
        }

        return result;
    }
}

/// <summary>How a <see cref="TrackerSuccession.StartAsync"/> call ended.</summary>
public enum TrackerSuccessionOutcome
{
    Started,
    NewerRunning,
    AlreadyStarted,
    CompletedElsewhere,
}

/// <param name="Outcome">How the call ended.</param>
/// <param name="Started">The new run, when <paramref name="Outcome"/> is Started.</param>
/// <param name="Completed">The runs this call completed, which a CompletedElsewhere call keeps.</param>
public sealed record TrackerSuccessionResult(
    TrackerSuccessionOutcome Outcome,
    TrackerInstanceEntity? Started,
    IReadOnlyList<TrackerInstanceEntity> Completed);
