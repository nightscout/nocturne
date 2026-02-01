using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Nocturne.Core.Models;

namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// PostgreSQL entity for unified in-app notifications
/// Supports various notification types with flexible actions and resolution conditions
/// </summary>
[Table("in_app_notifications")]
public class InAppNotificationEntity
{
    /// <summary>
    /// Primary key - UUID Version 7 for time-ordered, globally unique identification
    /// </summary>
    [Key]
    public Guid Id { get; set; }

    /// <summary>
    /// User identifier this notification belongs to
    /// </summary>
    [Required]
    [Column("user_id")]
    [MaxLength(255)]
    public string UserId { get; set; } = string.Empty;

    /// <summary>
    /// Type of notification for categorization and handling
    /// </summary>
    [Required]
    [Column("type")]
    [MaxLength(50)]
    public InAppNotificationType Type { get; set; }

    /// <summary>
    /// Urgency level for prioritization and visual styling
    /// </summary>
    [Required]
    [Column("urgency")]
    [MaxLength(20)]
    public NotificationUrgency Urgency { get; set; }

    /// <summary>
    /// Primary notification title displayed to the user
    /// </summary>
    [Required]
    [Column("title")]
    [MaxLength(255)]
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Optional secondary text providing additional context
    /// </summary>
    [Column("subtitle")]
    [MaxLength(500)]
    public string? Subtitle { get; set; }

    /// <summary>
    /// When the notification was created
    /// </summary>
    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Optional identifier linking to the source entity (e.g., tracker ID, user ID)
    /// Used for grouping and automatic resolution
    /// </summary>
    [Column("source_id")]
    [MaxLength(255)]
    public string? SourceId { get; set; }

    /// <summary>
    /// JSON array of available actions for this notification
    /// Stored as JSON for flexibility across notification types
    /// </summary>
    [Column("actions_json", TypeName = "jsonb")]
    public string? ActionsJson { get; set; }

    /// <summary>
    /// JSON object defining conditions that automatically archive the notification
    /// Supports expiration, source deletion, and glucose-based conditions
    /// </summary>
    [Column("resolution_conditions_json", TypeName = "jsonb")]
    public string? ResolutionConditionsJson { get; set; }

    /// <summary>
    /// Optional JSON object containing additional notification-specific metadata
    /// </summary>
    [Column("metadata_json", TypeName = "jsonb")]
    public string? MetadataJson { get; set; }

    /// <summary>
    /// Whether this notification has been archived (completed, dismissed, or auto-resolved)
    /// </summary>
    [Column("is_archived")]
    public bool IsArchived { get; set; } = false;

    /// <summary>
    /// When the notification was archived
    /// </summary>
    [Column("archived_at")]
    public DateTime? ArchivedAt { get; set; }

    /// <summary>
    /// Why the notification was archived
    /// </summary>
    [Column("archive_reason")]
    [MaxLength(20)]
    public NotificationArchiveReason? ArchiveReason { get; set; }
}
