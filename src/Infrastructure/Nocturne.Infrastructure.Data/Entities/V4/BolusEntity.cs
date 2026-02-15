using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Nocturne.Infrastructure.Data.Entities.V4;

/// <summary>
/// PostgreSQL entity for insulin bolus delivery records
/// Maps to Nocturne.Core.Models.V4.Bolus
/// </summary>
[Table("boluses")]
public class BolusEntity
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
    /// Device identifier that delivered this bolus
    /// </summary>
    [Column("device")]
    [MaxLength(256)]
    public string? Device { get; set; }

    /// <summary>
    /// Application that uploaded this bolus
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
    /// Duration in minutes for extended/square boluses
    /// </summary>
    [Column("duration")]
    public double? Duration { get; set; }
}
