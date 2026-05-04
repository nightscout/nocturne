using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.Infrastructure.Data.Entities.V4;

/// <summary>
/// PostgreSQL entity for continuous glucose monitor (CGM) readings
/// Maps to Nocturne.Core.Models.V4.SensorGlucose
/// </summary>
[Table("sensor_glucose")]
public class SensorGlucoseEntity : ITenantScoped, IAuditable
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
    /// Canonical timestamp as UTC DateTime (timestamptz)
    /// </summary>
    [Column("timestamp")]
    public DateTime Timestamp { get; set; }

    /// <summary>
    /// UTC offset in minutes
    /// </summary>
    [Column("utc_offset")]
    public int? UtcOffset { get; set; }

    /// <summary>
    /// Device identifier that produced this reading
    /// </summary>
    [Column("device")]
    [MaxLength(256)]
    public string? Device { get; set; }

    /// <summary>
    /// Application that uploaded this reading
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
    /// FK to the patient's registered device record (resolved at ingest time)
    /// </summary>
    [Column("patient_device_id")]
    public Guid? PatientDeviceId { get; set; }

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
    /// Glucose value in mg/dL
    /// </summary>
    [Column("mgdl")]
    public double Mgdl { get; set; }

    /// <summary>
    /// CGM trend arrow direction (enum stored as string)
    /// </summary>
    [Column("direction")]
    [MaxLength(32)]
    public string? Direction { get; set; }

    /// <summary>
    /// Rate of glucose change in mg/dL per minute
    /// </summary>
    [Column("trend_rate")]
    public double? TrendRate { get; set; }

    /// <summary>
    /// Signal noise level (0-4)
    /// </summary>
    [Column("noise")]
    public int? Noise { get; set; }

    /// <summary>
    /// Raw filtered sensor value (scaled ADC)
    /// </summary>
    [Column("filtered")]
    public double? Filtered { get; set; }

    /// <summary>
    /// Raw unfiltered sensor value (scaled ADC)
    /// </summary>
    [Column("unfiltered")]
    public double? Unfiltered { get; set; }

    /// <summary>
    /// Glucose delta in mg/dL over the last 5 minutes
    /// </summary>
    [Column("delta")]
    public double? Delta { get; set; }

    /// <summary>
    /// Whether this reading is smoothed or unsmoothed (enum stored as string). Null when unknown.
    /// </summary>
    [Column("glucose_processing")]
    [MaxLength(16)]
    public string? GlucoseProcessing { get; set; }

    /// <summary>
    /// Smoothed glucose value in mg/dL
    /// </summary>
    [Column("smoothed_mgdl")]
    public double? SmoothedMgdl { get; set; }

    /// <summary>
    /// Unsmoothed (raw) glucose value in mg/dL
    /// </summary>
    [Column("unsmoothed_mgdl")]
    public double? UnsmoothedMgdl { get; set; }

    /// <summary>
    /// Catch-all JSONB column for fields not mapped to dedicated columns
    /// </summary>
    [Column("additional_properties", TypeName = "jsonb")]
    public string? AdditionalPropertiesJson { get; set; }
}
