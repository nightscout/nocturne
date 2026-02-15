using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Nocturne.Infrastructure.Data.Entities.V4;

/// <summary>
/// PostgreSQL entity for pump status snapshot records
/// Maps to Nocturne.Core.Models.V4.PumpSnapshot
/// </summary>
[Table("pump_snapshots")]
public class PumpSnapshotEntity
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
    /// Pump manufacturer name
    /// </summary>
    [Column("manufacturer")]
    [MaxLength(128)]
    public string? Manufacturer { get; set; }

    /// <summary>
    /// Pump model name
    /// </summary>
    [Column("model")]
    [MaxLength(128)]
    public string? Model { get; set; }

    /// <summary>
    /// Reservoir level in units
    /// </summary>
    [Column("reservoir")]
    public double? Reservoir { get; set; }

    /// <summary>
    /// Human-readable reservoir display string
    /// </summary>
    [Column("reservoir_display")]
    [MaxLength(64)]
    public string? ReservoirDisplay { get; set; }

    /// <summary>
    /// Battery percentage (0-100)
    /// </summary>
    [Column("battery_percent")]
    public int? BatteryPercent { get; set; }

    /// <summary>
    /// Battery voltage
    /// </summary>
    [Column("battery_voltage")]
    public double? BatteryVoltage { get; set; }

    /// <summary>
    /// Whether the pump is currently delivering a bolus
    /// </summary>
    [Column("bolusing")]
    public bool? Bolusing { get; set; }

    /// <summary>
    /// Whether the pump is suspended
    /// </summary>
    [Column("suspended")]
    public bool? Suspended { get; set; }

    /// <summary>
    /// Pump status string
    /// </summary>
    [Column("pump_status")]
    [MaxLength(64)]
    public string? PumpStatus { get; set; }

    /// <summary>
    /// Pump clock time
    /// </summary>
    [Column("clock")]
    [MaxLength(64)]
    public string? Clock { get; set; }
}
