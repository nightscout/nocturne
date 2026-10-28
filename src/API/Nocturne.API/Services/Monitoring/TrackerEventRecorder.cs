using Nocturne.API.Services.Caching;
using Nocturne.API.Services.Devices;
using Nocturne.Connectors.Core.Constants;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.Monitoring;

/// <summary>
/// Records the device event a tracker definition names for its start or completion
/// (<c>StartEventType</c>, <c>CompletionEventType</c>), so a Nightscout CAGE or SAGE pill sees the
/// change made through the tracker.
/// </summary>
/// <remarks>
/// The caller plans the event before it writes the instance and links the instance to the planned id
/// (<c>StartTreatmentId</c>, <c>CompleteTreatmentId</c>), then records it. The write goes through the
/// device-event chokepoint, so <see cref="TrackerTriggerService"/> sees it, finds a run already linked
/// to it, and leaves that run alone rather than replace it with a duplicate.
/// </remarks>
public sealed class TrackerEventRecorder(
    IDeviceEventRepository deviceEvents,
    IPatientDeviceRepository patientDevices,
    IPatientDeviceStamper deviceStamper,
    ITenantCache cache,
    ILogger<TrackerEventRecorder> logger)
{
    /// <summary>The <see cref="DeviceEvent.App"/> of an event a tracker records.</summary>
    public const string App = "Nocturne Tracker";

    /// <summary>
    /// The event types a definition may name for its start or completion: the device events a tracker
    /// can restart or end on.
    /// </summary>
    public static readonly IReadOnlyList<string> RecordableEventTypes =
        [.. TrackerTriggerService.TriggerableEventTypes.Union(TrackerTriggerService.EndingEventTypes)];

    /// <summary>
    /// Canonicalises a requested event type against <see cref="RecordableEventTypes"/>. A blank value is
    /// null, and an unknown name is an error rather than an event that is silently never written.
    /// </summary>
    public static bool TryNormalise(string? requested, out string? normalised, out string? error)
    {
        normalised = null;
        error = null;
        if (string.IsNullOrWhiteSpace(requested))
            return true;

        normalised = RecordableEventTypes.FirstOrDefault(
            t => string.Equals(t, requested.Trim(), StringComparison.OrdinalIgnoreCase));
        if (normalised is null)
            error = $"\"{requested}\" is not a device event type. Use one of: {string.Join(", ", RecordableEventTypes)}.";
        return normalised is not null;
    }

    /// <summary>
    /// The event to record for <paramref name="eventType"/> at <paramref name="at"/>, with its id
    /// assigned, or null when the definition names none. A stored name that is not a device event
    /// type (definitions saved before the type was checked) records nothing.
    /// </summary>
    public static DeviceEvent? Plan(string? eventType, DateTime at, string? notes)
    {
        if (!TryNormalise(eventType, out var name, out _) || name is null)
            return null;

        return new DeviceEvent
        {
            Id = Guid.CreateVersion7(),
            Timestamp = at,
            EventType = TreatmentTypes.DeviceEventTypeMap[name],
            Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim(),
            App = App,
        };
    }

    /// <summary>
    /// Writes <paramref name="planned"/>. The tracker change it records has already committed, so a
    /// failure here is logged rather than thrown.
    /// </summary>
    public async Task RecordAsync(DeviceEvent planned, CancellationToken ct)
    {
        try
        {
            await PatientDeviceAttribution.ApplyAsync(
                planned,
                requested: null,
                existing: null,
                patientDevices,
                deviceStamper,
                DeviceAttributionCategories.DeviceEvent(planned.EventType),
                ct);
            await deviceEvents.CreateAsync(planned, WriteOrigin.Live, ct);
            await cache.RemoveFamilyAsync(TenantCacheFamily.Treatments, ct);
            await cache.RemoveFamilyAsync(TenantCacheFamily.Statistics, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not record the {EventType} device event for a tracker", planned.EventType);
        }
    }
}
