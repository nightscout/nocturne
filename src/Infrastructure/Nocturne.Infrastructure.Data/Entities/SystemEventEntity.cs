using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// PostgreSQL entity for SystemEvent (alarms, warnings, info events)
/// Maps to Nocturne.Core.Models.SystemEvent
/// </summary>
[Table("system_events")]
public class SystemEventEntity
{
    /// <summary>
    /// Primary key - UUID Version 7 for time-ordered, globally unique identification
    /// </summary>
    [Key]
    public Guid Id { get; set; }

    /// <summary>
    /// Event severity/type (Alarm, Hazard, Warning, Info)
    /// </summary>
    [Column("event_type")]
    [MaxLength(50)]
    [Required]
    public string EventType { get; set; } = string.Empty;

    /// <summary>
    /// Device category (Pump, Cgm, Connectivity)
    /// </summary>
    [Column("category")]
    [MaxLength(50)]
    [Required]
    public string Category { get; set; } = string.Empty;

    /// <summary>
    /// Device-specific event code
    /// </summary>
    [Column("code")]
    [MaxLength(100)]
    public string? Code { get; set; }

    /// <summary>
    /// Human-readable description
    /// </summary>
    [Column("description")]
    [MaxLength(1000)]
    public string? Description { get; set; }

    /// <summary>
    /// When event occurred (Unix milliseconds)
    /// </summary>
    [Column("mills")]
    public long Mills { get; set; }

    /// <summary>
    /// Data source identifier
    /// </summary>
    [Column("source")]
    [MaxLength(50)]
    public string? Source { get; set; }

    /// <summary>
    /// Additional event details (stored as JSON)
    /// </summary>
    [Column("metadata", TypeName = "jsonb")]
    public string? MetadataJson { get; set; }

    /// <summary>
    /// Original ID from source system for deduplication
    /// </summary>
    [Column("original_id")]
    [MaxLength(255)]
    public string? OriginalId { get; set; }

    /// <summary>
    /// System tracking: when record was created
    /// </summary>
    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
