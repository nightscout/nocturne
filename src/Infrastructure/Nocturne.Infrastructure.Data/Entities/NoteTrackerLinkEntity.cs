using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// PostgreSQL entity for NoteTrackerLink
/// Represents a link between a note and a tracker definition
/// </summary>
[Table("note_tracker_links")]
public class NoteTrackerLinkEntity
{
    /// <summary>
    /// Primary key - UUID Version 7 for time-ordered, globally unique identification
    /// </summary>
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    /// <summary>
    /// Foreign key to the parent note
    /// </summary>
    [Column("note_id")]
    public Guid NoteId { get; set; }

    /// <summary>
    /// Foreign key to the tracker definition
    /// </summary>
    [Column("tracker_definition_id")]
    public Guid TrackerDefinitionId { get; set; }

    /// <summary>
    /// System tracking: when record was created
    /// </summary>
    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Navigation property to the parent note
    /// </summary>
    [ForeignKey(nameof(NoteId))]
    public virtual NoteEntity Note { get; set; } = null!;

    /// <summary>
    /// Navigation property to the tracker definition
    /// </summary>
    [ForeignKey(nameof(TrackerDefinitionId))]
    public virtual TrackerDefinitionEntity TrackerDefinition { get; set; } = null!;

    /// <summary>
    /// Navigation property for notification thresholds
    /// </summary>
    public virtual ICollection<NoteTrackerThresholdEntity> Thresholds { get; set; } = new List<NoteTrackerThresholdEntity>();
}
