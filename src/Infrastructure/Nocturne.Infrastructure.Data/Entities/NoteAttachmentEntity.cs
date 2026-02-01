using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// PostgreSQL entity for NoteAttachment
/// Represents a file attachment within a note
/// </summary>
[Table("note_attachments")]
public class NoteAttachmentEntity
{
    /// <summary>
    /// Primary key - UUID Version 7 for time-ordered, globally unique identification
    /// </summary>
    [Key]
    public Guid Id { get; set; }

    /// <summary>
    /// Foreign key to the parent note
    /// </summary>
    [Column("note_id")]
    public Guid NoteId { get; set; }

    /// <summary>
    /// Original file name (max 255 characters)
    /// </summary>
    [Column("file_name")]
    [MaxLength(255)]
    public string FileName { get; set; } = string.Empty;

    /// <summary>
    /// MIME type of the attachment (max 100 characters)
    /// </summary>
    [Column("mime_type")]
    [MaxLength(100)]
    public string MimeType { get; set; } = string.Empty;

    /// <summary>
    /// Binary data of the attachment
    /// </summary>
    [Column("data")]
    public byte[] Data { get; set; } = Array.Empty<byte>();

    /// <summary>
    /// Size of the attachment in bytes
    /// </summary>
    [Column("size_bytes")]
    public long SizeBytes { get; set; }

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
