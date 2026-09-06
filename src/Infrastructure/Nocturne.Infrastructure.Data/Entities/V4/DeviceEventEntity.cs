using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.Infrastructure.Data.Entities.V4;

/// <summary>
/// PostgreSQL entity for device event records (site change, sensor start, etc.)
/// Maps to Nocturne.Core.Models.V4.DeviceEvent
/// </summary>
[Table("device_events")]
public class DeviceEventEntity : V4TimeSeriesEntityBase, ISyncDedupable, IDeviceAttributedEntity
{
    /// <summary>
    /// Type of device event stored as string (e.g. "SiteChange", "SensorStart")
    /// </summary>
    [Column("event_type")]
    [MaxLength(64)]
    public string EventType { get; set; } = string.Empty;

    /// <summary>
    /// Free-text notes about the device event
    /// </summary>
    [Column("notes")]
    [MaxLength(4096)]
    public string? Notes { get; set; }

    /// <summary>
    /// Unique identifier for synchronization across platforms and devices.
    /// </summary>
    [Column("sync_identifier")]
    [MaxLength(256)]
    public string? SyncIdentifier { get; set; }

    /// <summary>
    /// Foreign key to the Device table.
    /// </summary>
    [Column("device_id")]
    public Guid? DeviceId { get; set; }

    /// <summary>
    /// Foreign key to the PatientDevice table.
    /// </summary>
    [Column("patient_device_id")]
    public Guid? PatientDeviceId { get; set; }
}
