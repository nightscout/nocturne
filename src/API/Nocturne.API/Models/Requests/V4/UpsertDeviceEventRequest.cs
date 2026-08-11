using Nocturne.Core.Models;

namespace Nocturne.API.Models.Requests.V4;

/// <summary>
/// Request body for upserting a device event record (site changes, sensor starts, etc.) via the V4 API.
/// </summary>
/// <seealso cref="Validators.V4.UpsertDeviceEventRequestValidator"/>
/// <seealso cref="Nocturne.API.Controllers.V4.Devices.DeviceEventController"/>
public class UpsertDeviceEventRequest
{
    /// <summary>
    /// When the device event occurred.
    /// </summary>
    public DateTimeOffset Timestamp { get; set; }

    /// <summary>
    /// UTC offset in minutes at the time of the event, for local-time display.
    /// </summary>
    public int? UtcOffset { get; set; }

    /// <summary>
    /// Identifier of the device involved in the event.
    /// </summary>
    public string? Device { get; set; }

    /// <summary>
    /// Optional reference to the registered <see cref="Nocturne.Core.Models.V4.PatientDevice"/> the event
    /// occurred on. Must resolve to one of the caller's registered devices. When omitted on create, the
    /// server attempts attribution from <see cref="Device"/> and <see cref="DataSource"/>; when omitted on
    /// update, the existing link is preserved. Send the empty GUID
    /// (<c>00000000-0000-0000-0000-000000000000</c>) to state that the event belongs to no registered
    /// device: the link is cleared and server-side attribution is skipped for this request.
    /// </summary>
    public Guid? PatientDeviceId { get; set; }

    /// <summary>
    /// Name of the application that submitted this record.
    /// </summary>
    public string? App { get; set; }

    /// <summary>
    /// Upstream data source identifier.
    /// </summary>
    public string? DataSource { get; set; }

    /// <summary>
    /// The type of device event (e.g. site change, sensor start, pump resume).
    /// </summary>
    public DeviceEventType EventType { get; set; }

    /// <summary>
    /// Free-text notes associated with the event (capped at 10,000 characters).
    /// </summary>
    public string? Notes { get; set; }

    /// <summary>
    /// Upstream sync identifier for deduplication.
    /// </summary>
    public string? SyncIdentifier { get; set; }
}
