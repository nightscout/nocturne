using Nocturne.Core.Models.V4;

namespace Nocturne.Core.Contracts.Devices;

/// <summary>
/// Stamping shapes that more than one ingest path needs.
/// </summary>
public static class PatientDeviceStamperExtensions
{
    /// <summary>
    /// Stamps a mixed batch of device events, split by the categories that can own each event type:
    /// sensor-lifecycle events attribute to the CGM, every other event to the pump
    /// (<see cref="DeviceAttributionCategories.DeviceEvent"/>).
    /// </summary>
    public static async Task StampDeviceEventsAsync(
        this IPatientDeviceStamper stamper,
        IReadOnlyList<DeviceEvent> events,
        string? batchSource,
        CancellationToken ct = default)
    {
        var sensorEvents = events.Where(e => DeviceAttributionCategories.IsSensorEvent(e.EventType)).ToList();
        if (sensorEvents.Count > 0)
            await stamper.StampAsync(sensorEvents, DeviceAttributionCategories.SensorDeviceEvent, batchSource, ct);

        var pumpEvents = events.Where(e => !DeviceAttributionCategories.IsSensorEvent(e.EventType)).ToList();
        if (pumpEvents.Count > 0)
            await stamper.StampAsync(pumpEvents, DeviceAttributionCategories.PumpDeviceEvent, batchSource, ct);
    }
}
