using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// PostgreSQL entity for Activity records
/// Maps to Nocturne.Core.Models.Activity
/// </summary>
[Table("activities")]
public class ActivityEntity : IHasSysCreatedAt, IHasSysUpdatedAt
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
    /// Date and time as ISO 8601 string
    /// </summary>
    [Column("dateString")]
    [MaxLength(50)]
    public string? DateString { get; set; }

    /// <summary>
    /// Activity type or category
    /// </summary>
    [Column("type")]
    [MaxLength(100)]
    public string? Type { get; set; }

    /// <summary>
    /// Activity description or notes
    /// </summary>
    [Column("description")]
    public string? Description { get; set; }

    /// <summary>
    /// Duration of the activity in minutes
    /// </summary>
    [Column("duration")]
    public double? Duration { get; set; }

    /// <summary>
    /// Intensity level of the activity
    /// </summary>
    [Column("intensity")]
    [MaxLength(50)]
    public string? Intensity { get; set; }

    /// <summary>
    /// Additional notes about the activity
    /// </summary>
    [Column("notes")]
    public string? Notes { get; set; }

    /// <summary>
    /// Who entered this activity record
    /// </summary>
    [Column("enteredBy")]
    [MaxLength(255)]
    public string? EnteredBy { get; set; }

    /// <summary>
    /// UTC offset in minutes
    /// </summary>
    [Column("utcOffset")]
    public int? UtcOffset { get; set; }

    /// <summary>
    /// Optional timestamp in milliseconds since Unix epoch
    /// </summary>
    [Column("timestamp")]
    public long? Timestamp { get; set; }

    /// <summary>
    /// When this activity was created
    /// </summary>
    [Column("created_at")]
    [MaxLength(50)]
    public string? CreatedAt { get; set; }

    /// <summary>
    /// Additional properties (stored as JSON)
    /// </summary>
    [Column("additional_properties", TypeName = "jsonb")]
    public string? AdditionalPropertiesJson { get; set; }

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
