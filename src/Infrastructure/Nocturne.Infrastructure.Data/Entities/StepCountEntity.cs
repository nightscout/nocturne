using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// PostgreSQL entity for StepCount records
/// Maps to Nocturne.Core.Models.StepCount
/// </summary>
[Table("step_counts")]
public class StepCountEntity
{
    /// <summary>
    /// Primary key - UUID Version 7 for time-ordered, globally unique identification
    /// </summary>
    [Key]
    public Guid Id { get; set; }

    /// <summary>
    /// Original MongoDB ObjectId as string for reference/migration tracking
    /// </summary>
    [Column("original_id")]
    [MaxLength(24)]
    public string? OriginalId { get; set; }

    /// <summary>
    /// Time in milliseconds since Unix epoch
    /// </summary>
    [Column("mills")]
    public long Mills { get; set; }

    /// <summary>
    /// Step count or movement metric value (absolute total or delta, depending on source bitmask)
    /// </summary>
    [Column("metric")]
    public int Metric { get; set; }

    /// <summary>
    /// Source bitmask. Bit 0 set indicates an absolute step count total; otherwise a delta.
    /// </summary>
    [Column("source")]
    public int Source { get; set; }

    /// <summary>
    /// Device identifier that recorded this reading
    /// </summary>
    [Column("device")]
    [MaxLength(255)]
    public string? Device { get; set; }

    /// <summary>
    /// Who entered this record
    /// </summary>
    [Column("entered_by")]
    [MaxLength(255)]
    public string? EnteredBy { get; set; }

    /// <summary>
    /// When this record was created (ISO 8601)
    /// </summary>
    [Column("created_at")]
    [MaxLength(50)]
    public string? CreatedAt { get; set; }

    /// <summary>
    /// UTC offset in minutes
    /// </summary>
    [Column("utc_offset")]
    public int? UtcOffset { get; set; }

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
}
