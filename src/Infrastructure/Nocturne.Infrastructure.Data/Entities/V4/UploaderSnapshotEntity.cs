using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Nocturne.Infrastructure.Data.Entities.V4;

/// <summary>
/// PostgreSQL entity for uploader/phone status snapshot records
/// Maps to Nocturne.Core.Models.V4.UploaderSnapshot
/// </summary>
[Table("uploader_snapshots")]
public class UploaderSnapshotEntity
{
    /// <summary>
    /// Primary key - UUID Version 7 for time-ordered, globally unique identification
    /// </summary>
    [Key]
    public Guid Id { get; set; }

    /// <summary>
    /// Canonical timestamp in Unix milliseconds
    /// </summary>
    [Column("mills")]
    public long Mills { get; set; }

    /// <summary>
    /// UTC offset in minutes
    /// </summary>
    [Column("utc_offset")]
    public int? UtcOffset { get; set; }

    /// <summary>
    /// Device identifier that produced this snapshot
    /// </summary>
    [Column("device")]
    [MaxLength(256)]
    public string? Device { get; set; }

    /// <summary>
    /// Original v1/v3 record ID for migration traceability
    /// </summary>
    [Column("legacy_id")]
    [MaxLength(64)]
    public string? LegacyId { get; set; }

    /// <summary>
    /// System tracking: when record was inserted
    /// </summary>
    [Column("sys_created_at")]
    public DateTime SysCreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// System tracking: when record was last updated
    /// </summary>
    [Column("sys_updated_at")]
    public DateTime SysUpdatedAt { get; set; } = DateTime.UtcNow;

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
}
