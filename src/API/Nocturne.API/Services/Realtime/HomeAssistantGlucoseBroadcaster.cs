using Nocturne.Core.Contracts.Events;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.Realtime;

/// <summary>
/// SignalR adapter for <see cref="IGlucoseReadingBroadcaster"/>: sends the new readings as one
/// <see cref="GlucoseReadingEvent"/>, oldest first, to the tenant's Home Assistant instances.
/// </summary>
public sealed class HomeAssistantGlucoseBroadcaster(ISignalRBroadcastService broadcast) : IGlucoseReadingBroadcaster
{
    /// <inheritdoc />
    public Task BroadcastAsync(IReadOnlyList<SensorGlucose> readings, CancellationToken ct = default)
    {
        if (readings.Count == 0)
            return Task.CompletedTask;

        return broadcast.BroadcastHomeAssistantGlucoseAsync(new GlucoseReadingEvent
        {
            Readings = readings.OrderBy(r => r.Timestamp).ToList(),
        });
    }
}
