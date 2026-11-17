using Nocturne.Connectors.Core.Constants;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.Devices;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.API.Services.Monitoring;

/// <summary>
/// Records the device event a definition names for a manual start or completion
/// (<see cref="TrackerDefinitionEntity.StartEventType"/>, <see cref="TrackerDefinitionEntity.CompletionEventType"/>),
/// which hold Nightscout event names mapped through <see cref="TreatmentTypeMaps.DeviceEventTypeMap"/>.
/// </summary>
/// <remarks>
/// The event is written after the tracker's transaction commits, because a device event write cannot
/// join a <see cref="Infrastructure.Data.DbUnitOfWork"/>. It carries <see cref="DataSources.Tracker"/>,
/// which <see cref="TrackerTriggerService"/> passes over: the event records a move the tracker has
/// already made, and reacting to it would start or complete a run a second time.
/// </remarks>
public sealed class TrackerDeviceEventWriter(
    IDeviceEventRepository deviceEvents,
    IPatientDeviceStamper deviceStamper,
    ILogger<TrackerDeviceEventWriter> logger)
{
    /// <summary>
    /// Canonicalises a requested event name against <see cref="TreatmentTypeMaps.DeviceEventTypeMap"/>.
    /// Blank is "not supplied"; a name with no device event is an error naming <paramref name="field"/>.
    /// </summary>
    public static bool TryNormalise(string? requested, string field, out string? normalised, out string? error)
    {
        normalised = null;
        error = null;
        if (string.IsNullOrWhiteSpace(requested))
            return true;

        var name = requested.Trim();
        normalised = TreatmentTypeMaps.DeviceEventTypeMap.Keys
            .FirstOrDefault(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase));
        if (normalised is null)
            error = $"{field} '{name}' is not a device event";
        return error is null;
    }

    /// <summary>
    /// Writes the device event <paramref name="eventType"/> names at <paramref name="at"/>, keyed on
    /// <paramref name="syncIdentifier"/>. Writes nothing for no name, or for a name with no device
    /// event, which a definition saved before <see cref="TryNormalise"/> can still hold.
    /// </summary>
    /// <remarks>
    /// A failed write is logged and swallowed, as <c>DeviceEventRepository.OnLiveCreatedAsync</c> does:
    /// the start or completion it records is already committed, and failing the request would invite a
    /// retry that starts a second run.
    /// </remarks>
    public async Task WriteAsync(
        string? eventType, DateTime at, string? notes, string syncIdentifier, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(eventType))
            return;

        if (!TreatmentTypeMaps.DeviceEventTypeMap.TryGetValue(eventType.Trim(), out var type))
        {
            logger.LogDebug("Tracker event type {EventType} is not a device event; none written", eventType);
            return;
        }

        var deviceEvent = new DeviceEvent
        {
            Timestamp = at,
            EventType = type,
            Notes = string.IsNullOrWhiteSpace(notes) ? null : notes,
            DataSource = DataSources.Tracker,
            SyncIdentifier = syncIdentifier,
        };
        try
        {
            await deviceStamper.StampAsync([deviceEvent], DeviceAttributionCategories.DeviceEvent(type), batchSource: null, ct);
            await deviceEvents.CreateAsync(deviceEvent, WriteOrigin.Live, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Tracker device event {SyncIdentifier} was not written", syncIdentifier);
        }
    }
}
