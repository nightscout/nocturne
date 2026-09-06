using NJsonSchema.Annotations;

namespace Nocturne.Core.Models.V4;

/// <summary>
/// Device event record (site change, sensor start, pump battery change, etc.).
/// </summary>
/// <remarks>
/// This is the V4 equivalent of legacy <see cref="Treatment"/> records whose event type
/// represents a device lifecycle action (e.g., "Site Change", "Sensor Start", "Pump Battery Change").
/// The <see cref="EventType"/> is a strongly-typed <see cref="DeviceEventType"/> enum rather than
/// a freeform string.
/// </remarks>
/// <seealso cref="Treatment"/>
/// <seealso cref="IV4Record"/>
/// <seealso cref="DeviceEventType"/>
/// <seealso cref="Device"/>
/// <seealso cref="Note"/>
[JsonSchemaFlatten]
public class DeviceEvent : V4RecordBase, IDeviceAttributed
{
    /// <summary>
    /// Foreign key to the <see cref="Device"/> table.
    /// </summary>
    public Guid? DeviceId { get; set; }

    /// <summary>
    /// Foreign key to the <see cref="PatientDevice"/> table.
    /// </summary>
    public Guid? PatientDeviceId { get; set; }

    /// <summary>
    /// Type of device event (e.g. <see cref="DeviceEventType.SiteChange"/>,
    /// <see cref="DeviceEventType.SensorStart"/>).
    /// </summary>
    public DeviceEventType EventType { get; set; }

    /// <summary>
    /// Free-text notes about the device event
    /// </summary>
    public string? Notes { get; set; }

    /// <summary>
    /// APS system sync/deduplication identifier (used by Loop and AAPS)
    /// </summary>
    public string? SyncIdentifier { get; set; }
}
