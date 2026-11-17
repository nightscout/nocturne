using Nocturne.Core.Models.V4;

namespace Nocturne.Core.Contracts.Events;

/// <summary>
/// Driven port for domain reactions to written and deleted <see cref="DeviceEvent"/> records — currently
/// the tracker trigger, which advances tracker instances when a site or sensor change lands and undoes
/// the advance when the change is deleted.
/// </summary>
/// <remarks>
/// Fired from the V4 device-event write chokepoint for live writes only: the chokepoint gates on
/// <see cref="Nocturne.Core.Contracts.V4.WriteOrigin"/>, so a migration or replay import never
/// retroactively advances trackers.
/// Sitting at the repository rather than at a controller is the point — connector ingest reaches
/// device events through the legacy treatment write path and never touches the V4 REST surface, so a
/// controller-level hook would fire for hand-entered events only. The decomposer's own deletes of a
/// legacy treatment's rows bypass the chokepoint and call <see cref="OnDeletedAsync"/> themselves;
/// its range sweeps (a find-query bulk delete, a data source wipe) materialise no rows and run no
/// reaction.
/// Implementations run after the write has committed and must not throw; the chokepoint does not
/// roll the write back for them.
/// </remarks>
/// <seealso cref="IV4RecordBroadcaster{TModel}"/>
public interface IDeviceEventReactor
{
    /// <summary>Reacts to device events that were just created.</summary>
    /// <param name="created">The newly created events, in no particular order.</param>
    Task OnCreatedAsync(IReadOnlyList<DeviceEvent> created, CancellationToken ct = default);

    /// <summary>Reacts to device events that were just soft-deleted.</summary>
    /// <param name="deleted">The deleted events, in no particular order. A delete matching more rows
    /// than the chokepoint materialises carries none, and runs no reaction.</param>
    /// <param name="ct">Cancellation token.</param>
    Task OnDeletedAsync(IReadOnlyList<DeviceEvent> deleted, CancellationToken ct = default);

    /// <summary>
    /// Re-offers stored events after an <see cref="OnDeletedAsync"/> undo, so one passed over when it
    /// arrived can now take the undone event's place. Unlike <see cref="OnCreatedAsync"/> it starts
    /// nothing on a tracker someone ended after the event, so a run the user stopped by hand stays
    /// stopped.
    /// </summary>
    /// <param name="events">The stored events, in no particular order.</param>
    /// <param name="ct">Cancellation token.</param>
    Task OnReplayedAsync(IReadOnlyList<DeviceEvent> events, CancellationToken ct = default);

    /// <summary>
    /// Of <paramref name="eventIds"/>, the events a reaction is still following: here, those that
    /// started a tracker run still going. Deleting one of them undoes something; deleting any other
    /// undoes nothing, and needs nothing re-offered in its place.
    /// </summary>
    Task<IReadOnlySet<Guid>> GetFollowedAsync(IReadOnlyCollection<Guid> eventIds, CancellationToken ct = default);
}
