using Nocturne.Core.Models.V4;

namespace Nocturne.Core.Contracts.Events;

/// <summary>
/// Driven port for domain reactions to newly written <see cref="DeviceEvent"/> records — currently the
/// tracker trigger, which advances tracker instances when a site or sensor change lands.
/// </summary>
/// <remarks>
/// Fired from the V4 device-event write chokepoint for live writes only: the chokepoint gates on
/// <see cref="Nocturne.Core.Contracts.V4.WriteOrigin"/>, so a migration or replay import never
/// retroactively advances trackers.
/// Sitting at the repository rather than at a controller is the point — connector ingest reaches
/// device events through <c>ITreatmentDecomposer</c> and never touches the V4 REST surface, so a
/// controller-level hook would fire for hand-entered events only.
/// Implementations run after the write has committed and must not throw; the chokepoint does not
/// roll the write back for them.
/// </remarks>
/// <seealso cref="IV4RecordBroadcaster{TModel}"/>
public interface IDeviceEventReactor
{
    /// <summary>Reacts to device events that were just created.</summary>
    /// <param name="created">The newly created events, in no particular order.</param>
    /// <param name="ct">Cancellation token.</param>
    Task OnCreatedAsync(IReadOnlyList<DeviceEvent> created, CancellationToken ct = default);
}
