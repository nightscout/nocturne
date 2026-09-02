using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.Infrastructure.Data.Entities.V4;

/// <summary>
/// PostgreSQL entity for insulin bolus delivery records
/// Maps to Nocturne.Core.Models.V4.Bolus
/// </summary>
[Table("boluses")]
public class BolusEntity : V4TimeSeriesEntityBase, ISyncDedupable, IDeviceAttributedEntity
{
    /// <summary>
    /// Insulin units delivered
    /// </summary>
    [Column("insulin")]
    public double Insulin { get; set; }

    /// <summary>
    /// Original programmed dose before any interruption
    /// </summary>
    [Column("programmed")]
    public double? Programmed { get; set; }

    /// <summary>
    /// Actual insulin delivered, if different from programmed
    /// </summary>
    [Column("delivered")]
    public double? Delivered { get; set; }

    /// <summary>
    /// Type of bolus delivery (enum stored as string: Normal, Square, Dual)
    /// </summary>
    [Column("bolus_type")]
    [MaxLength(32)]
    public string? BolusType { get; set; }

    /// <summary>
    /// Whether this bolus was auto-delivered by an APS system
    /// </summary>
    [Column("automatic")]
    public bool Automatic { get; set; }

    /// <summary>
    /// Discriminator: Manual (user-initiated) or Algorithm (SMB)
    /// </summary>
    [Column("bolus_kind")]
    [MaxLength(32)]
    public string BolusKind { get; set; } = "Manual";

    /// <summary>
    /// Duration in minutes for extended/square boluses
    /// </summary>
    [Column("duration")]
    public double? Duration { get; set; }

    /// <summary>
    /// Unique identifier for synchronization across platforms and devices.
    /// </summary>
    [Column("sync_identifier")]
    [MaxLength(256)]
    public string? SyncIdentifier { get; set; }

    /// <summary>
    /// The type of insulin delivered.
    /// </summary>
    [Column("insulin_type")]
    [MaxLength(128)]
    public string? InsulinType { get; set; }

    /// <summary>
    /// Snapshot of insulin pharmacokinetic settings at delivery time (JSONB).
    /// </summary>
    [Column("insulin_context", TypeName = "jsonb")]
    public string? InsulinContextJson { get; set; }

    /// <summary>
    /// Estimated unabsorbed insulin.
    /// </summary>
    [Column("unabsorbed")]
    public double? Unabsorbed { get; set; }

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

    /// <summary>
    /// Pump-specific record identifier for deduplication.
    /// </summary>
    [Column("pump_record_id")]
    [MaxLength(256)]
    public string? PumpRecordId { get; set; }

    /// <summary>
    /// Foreign key to the BolusCalculation table.
    /// </summary>
    [Column("bolus_calculation_id")]
    public Guid? BolusCalculationId { get; set; }

    /// <summary>
    /// Foreign key to the ApsSnapshot table.
    /// </summary>
    [Column("aps_snapshot_id")]
    public Guid? ApsSnapshotId { get; set; }
}
