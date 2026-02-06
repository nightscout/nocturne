using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// PostgreSQL entity for NoteChecklistItem
/// Represents a checklist item within a note
/// </summary>
[Table("note_checklist_items")]
public class NoteChecklistItemEntity : IHasCreatedAt
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
    /// Text content of the checklist item (max 500 characters)
    /// </summary>
    [Column("text")]
    [MaxLength(500)]
    public string Text { get; set; } = string.Empty;

    /// <summary>
    /// Whether the checklist item is completed
    /// </summary>
    [Column("is_completed")]
    public bool IsCompleted { get; set; } = false;

    /// <summary>
    /// When the checklist item was completed (null if not completed)
    /// </summary>
    [Column("completed_at")]
    public DateTime? CompletedAt { get; set; }

    /// <summary>
    /// Sort order for display within the note
    /// </summary>
    [Column("sort_order")]
    public int SortOrder { get; set; }

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
}
