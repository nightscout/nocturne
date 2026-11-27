using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Nocturne.Core.Constants;

namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// PostgreSQL entity for SystemEvent (alarms, warnings, info events)
/// Maps to Nocturne.Core.Models.SystemEvent
/// </summary>
[Table("system_events")]
public class SystemEventEntity : ITenantScoped
{
    /// <summary>Row id, unique within its tenant: the primary key is <c>(TenantId, Id)</c>.</summary>
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
    [MaxLength(ColumnLengths.SystemEvent.Code)]
    public string? Code { get; set; }

    /// <summary>
    /// Human-readable description
    /// </summary>
    [Column("description")]
    [MaxLength(ColumnLengths.SystemEvent.Description)]
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
    [MaxLength(ColumnLengths.SystemEvent.Source)]
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
    [MaxLength(ColumnLengths.SystemEvent.OriginalId)]
    public string? OriginalId { get; set; }

    /// <summary>
    /// System tracking: when record was created
    /// </summary>
    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Tenant this system event belongs to
    /// </summary>
    [Column("tenant_id")]
    public Guid TenantId { get; set; }
}
