using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// PostgreSQL entity for NoteStateSpanLink
/// Represents a link between a note and a state span
/// </summary>
[Table("note_state_span_links")]
public class NoteStateSpanLinkEntity
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
    /// Foreign key to the state span
    /// </summary>
    [Column("state_span_id")]
    public Guid StateSpanId { get; set; }

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
    /// Navigation property to the state span
    /// </summary>
    [ForeignKey(nameof(StateSpanId))]
    public virtual StateSpanEntity StateSpan { get; set; } = null!;
}
