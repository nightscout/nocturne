using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// PostgreSQL entity for StateSpan (pump modes, connectivity, overrides, profiles)
/// Maps to Nocturne.Core.Models.StateSpan
/// </summary>
[Table("state_spans")]
public class StateSpanEntity
{
    /// <summary>
    /// Primary key - UUID Version 7 for time-ordered, globally unique identification
    /// </summary>
    [Key]
    public Guid Id { get; set; }

    /// <summary>
    /// State category (PumpMode, PumpConnectivity, Override, Profile)
    /// </summary>
    [Column("category")]
    [MaxLength(50)]
    [Required]
    public string Category { get; set; } = string.Empty;

    /// <summary>
    /// State within category (e.g., "Automatic", "Manual", "Connected")
    /// </summary>
    [Column("state")]
    [MaxLength(100)]
    [Required]
    public string State { get; set; } = string.Empty;

    /// <summary>
    /// When this state began (Unix milliseconds)
    /// </summary>
    [Column("start_mills")]
    public long StartMills { get; set; }

    /// <summary>
    /// When this state ended (Unix milliseconds, null = active)
    /// </summary>
    [Column("end_mills")]
    public long? EndMills { get; set; }

    /// <summary>
    /// Data source identifier (e.g., "glooko", "nightscout")
    /// </summary>
    [Column("source")]
    [MaxLength(50)]
    public string? Source { get; set; }

    /// <summary>
    /// Category-specific metadata (stored as JSON)
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

    /// <summary>
    /// System tracking: when record was last updated
    /// </summary>
    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
