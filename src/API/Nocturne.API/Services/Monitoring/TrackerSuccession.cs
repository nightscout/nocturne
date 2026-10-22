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
    /// instance first. With <paramref name="replay"/> it also starts nothing when nothing is running
    /// and a run of the definition ended after <paramref name="startedAt"/>: someone has already
    /// moved the tracker past the start being re-offered. Each run it completes is broadcast as
    /// <see cref="TrackerRealtimeAction.Complete"/>, then the new run as
    /// <see cref="TrackerRealtimeAction.Create"/>.
    /// </summary>
    public static async Task<TrackerSuccessionResult> StartAsync(
        ITrackerRepository repository,
        ISignalRBroadcastService broadcast,
        ILogger logger,
        TrackerDefinitionEntity definition,
        DateTime startedAt,
        string? completionNotes,
        string? completeTreatmentId,
        Func<ITrackerRepository, CancellationToken, Task<TrackerInstanceEntity>> start,
        CancellationToken ct,
        Func<IReadOnlyList<TrackerInstanceEntity>, bool>? alreadyStarted = null,
        bool replay = false)
    {
        var result = await repository.ExecuteUnderDefinitionLockAsync(
            definition.Id,
            async (tx, token) =>
            {
                List<TrackerInstanceEntity> completed = [];
                if (definition.Mode == TrackerMode.Duration)
                {
                    var running = await tx.GetActiveInstancesForDefinitionAsync(definition.Id, token);
                    if (alreadyStarted?.Invoke(running) == true)
                        return new TrackerSuccessionResult(TrackerSuccessionOutcome.AlreadyStarted, null, completed);
                    if (replay && running.Count == 0
                        && await tx.HasRunEndedAfterAsync(definition.Id, startedAt, token))
                        return new TrackerSuccessionResult(TrackerSuccessionOutcome.EndedSince, null, completed);
                    if (running.Any(i => i.StartedAt >= startedAt))
                        return new TrackerSuccessionResult(TrackerSuccessionOutcome.NewerRunning, null, completed);

                    foreach (var instance in running)
                    {
                        var won = await tx.CompleteInstanceAsync(
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

                return new TrackerSuccessionResult(TrackerSuccessionOutcome.Started, await start(tx, token), completed);
            },
            async (tx, attempt, token) =>
                attempt.Started is { } started
                && await tx.GetInstanceByIdAsync(started.Id, token) is not null,
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
                TrackerRealtimeAction.Complete,
                TrackerInstanceDto.FromEntity(instance),
                definition.UserId,
                definition.Visibility
            );
        }

        if (result.Started is { } started)
            await broadcast.BroadcastTrackerUpdateAsync(
                TrackerRealtimeAction.Create,
                TrackerInstanceDto.FromEntity(started),
                definition.UserId,
                definition.Visibility
            );

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
    EndedSince,
}

/// <param name="Outcome">How the call ended.</param>
/// <param name="Started">The new run, when <paramref name="Outcome"/> is Started.</param>
/// <param name="Completed">The runs this call completed, which a CompletedElsewhere call keeps.</param>
public sealed record TrackerSuccessionResult(
    TrackerSuccessionOutcome Outcome,
    TrackerInstanceEntity? Started,
    IReadOnlyList<TrackerInstanceEntity> Completed);
