using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Nocturne.Core.Models;

namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// PostgreSQL entity for NoteTrackerThreshold
/// Represents a notification threshold for a note tracker link
/// </summary>
[Table("note_tracker_thresholds")]
public class NoteTrackerThresholdEntity
{
    /// <summary>
    /// Primary key - UUID Version 7 for time-ordered, globally unique identification
    /// </summary>
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    /// <summary>
    /// Foreign key to the parent note tracker link
    /// </summary>
    [Column("note_tracker_link_id")]
    public Guid NoteTrackerLinkId { get; set; }

    /// <summary>
    /// Hours offset from tracker start (positive) or end (negative)
    /// </summary>
    [Column("hours_offset")]
    public decimal HoursOffset { get; set; }

    /// <summary>
    /// Urgency level of the notification
    /// </summary>
    [Column("urgency")]
    public NotificationUrgency Urgency { get; set; }

    /// <summary>
    /// Optional description for this threshold (max 500 characters)
    /// </summary>
    [Column("description")]
    [MaxLength(500)]
    public string? Description { get; set; }

    /// <summary>
    /// Navigation property to the parent note tracker link
    /// </summary>
    [ForeignKey(nameof(NoteTrackerLinkId))]
    public virtual NoteTrackerLinkEntity NoteTrackerLink { get; set; } = null!;
}
