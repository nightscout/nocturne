using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Nocturne.Infrastructure.Data.Entities.V4;

/// <summary>
/// PostgreSQL entity for uploader/phone status snapshot records
/// Maps to Nocturne.Core.Models.V4.UploaderSnapshot
/// </summary>
[Table("uploader_snapshots")]
public class UploaderSnapshotEntity : V4TimeSeriesEntityBase
{
    /// <summary>
    /// Stable per-source identifier for synchronization. Unlike <see cref="V4TimeSeriesEntityBase.LegacyId"/> (insert-only),
    /// a record matched by (DataSource, SyncIdentifier) is updated in place on re-upload — required so
    /// uploader retries of the same loop cycle don't duplicate the snapshot.
    /// </summary>
    [Column("sync_identifier")]
    [MaxLength(256)]
    public string? SyncIdentifier { get; set; }

    /// <summary>
    /// Uploader/phone name
    /// </summary>
    [Column("name")]
    [MaxLength(256)]
    public string? Name { get; set; }

    /// <summary>
    /// Battery percentage (0-100)
    /// </summary>
    [Column("battery")]
    public int? Battery { get; set; }

    /// <summary>
    /// Battery voltage
    /// </summary>
    [Column("battery_voltage")]
    public double? BatteryVoltage { get; set; }

    /// <summary>
    /// Whether the device is currently charging
    /// </summary>
    [Column("is_charging")]
    public bool? IsCharging { get; set; }

    /// <summary>
    /// Device temperature
    /// </summary>
    [Column("temperature")]
    public double? Temperature { get; set; }

    /// <summary>
    /// Uploader type identifier
    /// </summary>
    [Column("type")]
    [MaxLength(128)]
    public string? Type { get; set; }

    /// <summary>
    /// Foreign key to the Device table
    /// </summary>
    [Column("device_id")]
    public Guid? DeviceId { get; set; }
}
