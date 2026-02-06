using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Nocturne.Core.Models;

namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// PostgreSQL entity for Note
/// Maps to Nocturne.Core.Models.Note
/// </summary>
[Table("notes")]
public class NoteEntity : IHasCreatedAt, IHasUpdatedAt
{
    /// <summary>
    /// Primary key - UUID Version 7 for time-ordered, globally unique identification
    /// </summary>
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    /// <summary>
    /// User identifier who owns this note
    /// </summary>
    [Column("user_id")]
    public Guid UserId { get; set; }

    /// <summary>
    /// Category of the note (stored as int in database)
    /// </summary>
    [Column("category")]
    public NoteCategory Category { get; set; }

    /// <summary>
    /// Optional title for the note (max 200 characters)
    /// </summary>
    [Column("title")]
    [MaxLength(200)]
    public string? Title { get; set; }

    /// <summary>
    /// Main content of the note (max 10000 characters)
    /// </summary>
    [Column("content")]
    [MaxLength(10000)]
    public string Content { get; set; } = string.Empty;

    /// <summary>
    /// When the note event occurred (optional contextual timestamp)
    /// </summary>
    [Column("occurred_at")]
    public DateTime OccurredAt { get; set; }

    /// <summary>
    /// Whether the note is archived
    /// </summary>
    [Column("is_archived")]
    public bool IsArchived { get; set; } = false;

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

    /// <summary>
    /// Navigation property for checklist items
    /// </summary>
    public virtual ICollection<NoteChecklistItemEntity> ChecklistItems { get; set; } = new List<NoteChecklistItemEntity>();

    /// <summary>
    /// Navigation property for attachments
    /// </summary>
    public virtual ICollection<NoteAttachmentEntity> Attachments { get; set; } = new List<NoteAttachmentEntity>();

    /// <summary>
    /// Navigation property for tracker links
    /// </summary>
    public virtual ICollection<NoteTrackerLinkEntity> TrackerLinks { get; set; } = new List<NoteTrackerLinkEntity>();

    /// <summary>
    /// Navigation property for state span links
    /// </summary>
    public virtual ICollection<NoteStateSpanLinkEntity> StateSpanLinks { get; set; } = new List<NoteStateSpanLinkEntity>();
}
