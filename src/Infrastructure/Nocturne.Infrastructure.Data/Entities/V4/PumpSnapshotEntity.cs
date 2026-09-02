using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Nocturne.Infrastructure.Data.Entities.V4;

/// <summary>
/// PostgreSQL entity for pump status snapshot records
/// Maps to Nocturne.Core.Models.V4.PumpSnapshot
/// </summary>
[Table("pump_snapshots")]
public class PumpSnapshotEntity : V4TimeSeriesEntityBase
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
    /// Canonical closed-loop operating mode (a PumpModeState name, e.g. "Automatic"/"Manual")
    /// </summary>
    [Column("pump_mode")]
    [MaxLength(64)]
    public string? PumpMode { get; set; }

    /// <summary>
    /// Pump clock time
    /// </summary>
    [Column("clock")]
    [MaxLength(64)]
    public string? Clock { get; set; }

    /// <summary>
    /// Foreign key to the Device table
    /// </summary>
    [Column("device_id")]
    public Guid? DeviceId { get; set; }

    /// <summary>
    /// Foreign key to the PatientDevice table.
    /// </summary>
    [Column("patient_device_id")]
    public Guid? PatientDeviceId { get; set; }

    /// <summary>
    /// Pump-reported total IOB (when no APS algorithm is running)
    /// </summary>
    [Column("iob")]
    public double? Iob { get; set; }

    /// <summary>
    /// Pump-reported bolus IOB
    /// </summary>
    [Column("bolus_iob")]
    public double? BolusIob { get; set; }
}
