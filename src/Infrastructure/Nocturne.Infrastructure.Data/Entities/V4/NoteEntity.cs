using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.Infrastructure.Data.Entities.V4;

/// <summary>
/// PostgreSQL entity for user note or annotation records
/// Maps to Nocturne.Core.Models.V4.Note
/// </summary>
[Table("notes")]
public class NoteEntity : V4TimeSeriesEntityBase, ISyncDedupable
{
    /// <summary>
    /// Note text content
    /// </summary>
    [Column("text")]
    [MaxLength(4096)]
    public string Text { get; set; } = string.Empty;

    /// <summary>
    /// Original event type (user-specified freeform)
    /// </summary>
    [Column("event_type")]
    [MaxLength(256)]
    public string? EventType { get; set; }

    /// <summary>
    /// Whether this note is an announcement
    /// </summary>
    [Column("is_announcement")]
    public bool IsAnnouncement { get; set; }

    /// <summary>
    /// Unique identifier for synchronization across platforms and devices.
    /// </summary>
    [Column("sync_identifier")]
    [MaxLength(256)]
    public string? SyncIdentifier { get; set; }
}
