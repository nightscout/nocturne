using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.Infrastructure.Data.Entities.V4;

/// <summary>
/// PostgreSQL entity for temporary basal rate change records
/// Maps to Nocturne.Core.Models.V4.TempBasal
/// </summary>
[Table("temp_basals")]
public class TempBasalEntity : ITenantScoped, IAuditable
{
    /// <summary>
    /// The unique identifier of the tenant this record belongs to.
    /// </summary>
    [Column("tenant_id")]
    public Guid TenantId { get; set; }

    /// <summary>
    /// Primary key - UUID Version 7 for time-ordered, globally unique identification
    /// </summary>
    [Key]
    public Guid Id { get; set; }

    /// <summary>
    /// Start timestamp as UTC DateTime (timestamptz)
    /// </summary>
    [Column("start_timestamp")]
    public DateTime StartTimestamp { get; set; }

    /// <summary>
    /// End timestamp as UTC DateTime (timestamptz, null if still active)
    /// </summary>
    [Column("end_timestamp")]
    public DateTime? EndTimestamp { get; set; }

    /// <summary>
    /// UTC offset in minutes
    /// </summary>
    [Column("utc_offset")]
    public int? UtcOffset { get; set; }

    /// <summary>
    /// Device identifier that set this temp basal
    /// </summary>
    [Column("device")]
    [MaxLength(256)]
    public string? Device { get; set; }

    /// <summary>
    /// Application that uploaded this record
    /// </summary>
    [Column("app")]
    [MaxLength(256)]
    public string? App { get; set; }

    /// <summary>
    /// Origin data source identifier
    /// </summary>
    [Column("data_source")]
    [MaxLength(256)]
    public string? DataSource { get; set; }

    /// <summary>
    /// Links records that were split from the same legacy Treatment
    /// </summary>
    [Column("correlation_id")]
    public Guid? CorrelationId { get; set; }

    /// <summary>
    /// Original v1/v3 record ID for migration traceability
    /// </summary>
    [Column("legacy_id")]
    [MaxLength(64)]
    public string? LegacyId { get; set; }

    /// <summary>
    /// System tracking: when record was inserted
    /// </summary>
    [AuditIgnored]
    [Column("sys_created_at")]
    public DateTime SysCreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// System tracking: when record was last updated
    /// </summary>
    [AuditIgnored]
    [Column("sys_updated_at")]
    public DateTime SysUpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Temporary basal rate in units per hour
    /// </summary>
    [Column("rate")]
    public double Rate { get; set; }

    /// <summary>
    /// Scheduled basal rate that this temp basal overrides
    /// </summary>
    [Column("scheduled_rate")]
    public double? ScheduledRate { get; set; }

    /// <summary>
    /// Origin of this temp basal (stored as string: Algorithm, Scheduled, Manual, Suspended, Inferred)
    /// </summary>
    [Column("origin")]
    [MaxLength(32)]
    public string Origin { get; set; } = null!;

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
    /// Pump-specific record identifier for deduplication
    /// </summary>
    [Column("pump_record_id")]
    [MaxLength(256)]
    public string? PumpRecordId { get; set; }

    /// <summary>
    /// Foreign key to the ApsSnapshot table if this temp basal was enacted by an algorithm
    /// </summary>
    [Column("aps_snapshot_id")]
    public Guid? ApsSnapshotId { get; set; }

    /// <summary>
    /// Catch-all JSONB column for fields not mapped to dedicated columns
    /// </summary>
    [Column("additional_properties", TypeName = "jsonb")]
    public string? AdditionalPropertiesJson { get; set; }
}
