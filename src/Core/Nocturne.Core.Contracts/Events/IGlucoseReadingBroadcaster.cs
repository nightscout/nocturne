using Nocturne.Core.Models.V4;

namespace Nocturne.Core.Contracts.Events;

/// <summary>
/// Driven port for relaying live sensor glucose readings to subscribers that follow a single CGM
/// stream (the Home Assistant hub's <c>glucose_reading</c>).
/// </summary>
/// <remarks>
/// Fired from the sensor glucose repository for <see cref="V4.WriteOrigin.Live"/> creates only, with the
/// readings that survive canonical stream selection and are newer than the tenant's last reading.
/// </remarks>
public interface IGlucoseReadingBroadcaster
{
    /// <summary>Relays new readings a live write created.</summary>
    Task BroadcastAsync(IReadOnlyList<SensorGlucose> readings, CancellationToken ct = default);
}
