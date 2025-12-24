using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Nocturne.Core.Models;

namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// PostgreSQL entity for TrackerNotificationThreshold
/// Represents a notification threshold for a tracker definition
/// Multiple thresholds can be defined per definition, supporting multiple notifications per urgency level
/// </summary>
[Table("tracker_notification_thresholds")]
public class TrackerNotificationThresholdEntity
{
    /// <summary>
    /// Primary key
    /// </summary>
    [Key]
    public Guid Id { get; set; }

    /// <summary>
    /// Foreign key to the tracker definition
    /// </summary>
    [Column("tracker_definition_id")]
    public Guid TrackerDefinitionId { get; set; }

    /// <summary>
    /// Urgency level of the notification
    /// </summary>
    [Column("urgency")]
    public NotificationUrgency Urgency { get; set; } = NotificationUrgency.Info;

    /// <summary>
    /// Hours after tracker start to trigger this notification
    /// </summary>
    [Column("hours")]
    public int Hours { get; set; }

    /// <summary>
    /// Optional description shown when this threshold is triggered
    /// </summary>
    [Column("description")]
    [MaxLength(500)]
    public string? Description { get; set; }

    /// <summary>
    /// Display order for multiple thresholds (lower = first)
    /// </summary>
    [Column("display_order")]
    public int DisplayOrder { get; set; }

    #region Alert Configuration

    /// <summary>
    /// Whether this threshold should trigger active push notifications
    /// </summary>
    [Column("push_enabled")]
    public bool PushEnabled { get; set; } = false;

    /// <summary>
    /// Whether this threshold should play an audio alert (for in-app/web)
    /// </summary>
    [Column("audio_enabled")]
    public bool AudioEnabled { get; set; } = false;

    /// <summary>
    /// Audio file/preset to play (e.g., "chime", "alarm", "urgent")
    /// </summary>
    [Column("audio_sound")]
    [MaxLength(100)]
    public string? AudioSound { get; set; }

    /// <summary>
    /// Whether to vibrate on mobile devices
    /// </summary>
    [Column("vibrate_enabled")]
    public bool VibrateEnabled { get; set; } = false;

    /// <summary>
    /// Repeat interval in minutes (0 = no repeat)
    /// </summary>
    [Column("repeat_interval_mins")]
    public int RepeatIntervalMins { get; set; } = 0;

    /// <summary>
    /// Maximum number of repeats (0 = unlimited until acknowledged)
    /// </summary>
    [Column("max_repeats")]
    public int MaxRepeats { get; set; } = 3;

    /// <summary>
    /// Whether this notification respects quiet hours
    /// </summary>
    [Column("respect_quiet_hours")]
    public bool RespectQuietHours { get; set; } = true;

    #endregion

    /// <summary>
    /// Navigation property to the parent tracker definition
    /// </summary>
    public virtual TrackerDefinitionEntity? Definition { get; set; }
}
