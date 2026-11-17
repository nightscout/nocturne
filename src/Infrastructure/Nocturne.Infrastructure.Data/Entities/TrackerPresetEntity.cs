using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// PostgreSQL entity for TrackerPreset (saved quick-apply configuration)
/// Links to a single definition for easy activation
/// </summary>
[Table("tracker_presets")]
public class TrackerPresetEntity : ITenantScoped
{
    /// <summary>
    /// Identifier of the tenant this tracker preset belongs to.
    /// </summary>
    [Column("tenant_id")]
    public Guid TenantId { get; set; }

    /// <summary>
    /// Primary key - UUID for preset
    /// </summary>
    [Key]
    public Guid Id { get; set; }

    /// <summary>
    /// The subject who created this row. It grants nothing: trackers are the tenant's, and any
    /// member may read or change them.
    /// </summary>
    [Column("created_by")]
    [MaxLength(255)]
    public string CreatedBy { get; set; } = string.Empty;

    /// <summary>
    /// Human-readable name (e.g., "G7", "Steel Cannula")
    /// </summary>
    [Column("name")]
    [MaxLength(255)]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Foreign key to TrackerDefinition
    /// </summary>
    [Column("definition_id")]
    public Guid DefinitionId { get; set; }

    /// <summary>
    /// Default start notes to pre-fill when applying
    /// </summary>
    [Column("default_start_notes")]
    [MaxLength(1000)]
    public string? DefaultStartNotes { get; set; }

    /// <summary>
    /// When this preset was created
    /// </summary>
    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Navigation property to definition
    /// </summary>
    [ForeignKey(nameof(DefinitionId))]
    public virtual TrackerDefinitionEntity Definition { get; set; } = null!;
}
