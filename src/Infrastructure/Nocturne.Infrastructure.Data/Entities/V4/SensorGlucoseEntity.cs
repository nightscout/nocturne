using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Nocturne.Infrastructure.Data.Entities.V4;

/// <summary>
/// PostgreSQL entity for continuous glucose monitor (CGM) readings
/// Maps to Nocturne.Core.Models.V4.SensorGlucose
/// </summary>
[Table("sensor_glucose")]
public class SensorGlucoseEntity
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
    /// Glucose value in mg/dL
    /// </summary>
    [Column("mgdl")]
    public double Mgdl { get; set; }

    /// <summary>
    /// Glucose value in mmol/L (computed from Mgdl)
    /// </summary>
    [Column("mmol")]
    public double? Mmol { get; set; }

    /// <summary>
    /// CGM trend arrow direction (enum stored as string)
    /// </summary>
    [Column("direction")]
    [MaxLength(32)]
    public string? Direction { get; set; }

    /// <summary>
    /// Numeric trend value corresponding to CGM trend arrows
    /// </summary>
    [Column("trend")]
    public int? Trend { get; set; }

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
}
