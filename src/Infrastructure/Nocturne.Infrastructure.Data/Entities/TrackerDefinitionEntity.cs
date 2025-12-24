using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Nocturne.Core.Models;

namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// PostgreSQL entity for TrackerDefinition (reusable tracker template)
/// Defines a type of tracker with notification thresholds and event matching
/// </summary>
[Table("tracker_definitions")]
public class TrackerDefinitionEntity
{
    /// <summary>
    /// Primary key - UUID for tracker definition
    /// </summary>
    [Key]
    public Guid Id { get; set; }

    /// <summary>
    /// User identifier this definition belongs to
    /// </summary>
    [Column("user_id")]
    [MaxLength(255)]
    public string UserId { get; set; } = string.Empty;

    /// <summary>
    /// Human-readable name (e.g., "G7 Sensor", "Steel Cannula")
    /// </summary>
    [Column("name")]
    [MaxLength(255)]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Optional description
    /// </summary>
    [Column("description")]
    [MaxLength(1000)]
    public string? Description { get; set; }

    /// <summary>
    /// Category for grouping and filtering
    /// </summary>
    [Column("category")]
    public TrackerCategory Category { get; set; } = TrackerCategory.Consumable;

    /// <summary>
    /// Lucide icon name for display
    /// </summary>
    [Column("icon")]
    [MaxLength(100)]
    public string Icon { get; set; } = "activity";

    /// <summary>
    /// Treatment event types that trigger/reset this tracker (JSON array)
    /// e.g., ["Site Change", "Cannula Change"]
    /// </summary>
    [Column("trigger_event_types", TypeName = "jsonb")]
    public string TriggerEventTypes { get; set; } = "[]";

    /// <summary>
    /// Optional: match treatment notes containing this string
    /// </summary>
    [Column("trigger_notes_contains")]
    [MaxLength(255)]
    public string? TriggerNotesContains { get; set; }

    /// <summary>
    /// Expected lifespan in hours (for forecasting and comparison)
    /// </summary>
    [Column("lifespan_hours")]
    public int? LifespanHours { get; set; }

    /// <summary>
    /// Show in quick-add favorites
    /// </summary>
    [Column("is_favorite")]
    public bool IsFavorite { get; set; } = false;

    /// <summary>
    /// Dashboard visibility: Off, Always, Info, Warn, Hazard, Urgent
    /// "Off" = never show, "Always" = always show, others = show when age reaches that notification level
    /// </summary>
    [Column("dashboard_visibility")]
    public DashboardVisibility DashboardVisibility { get; set; } = DashboardVisibility.Always;

    /// <summary>
    /// When this definition was created
    /// </summary>
    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When this definition was last updated
    /// </summary>
    [Column("updated_at")]
    public DateTime? UpdatedAt { get; set; }

    /// <summary>
    /// Navigation property for instances
    /// </summary>
    public virtual ICollection<TrackerInstanceEntity> Instances { get; set; } = new List<TrackerInstanceEntity>();

    /// <summary>
    /// Navigation property for presets
    /// </summary>
    public virtual ICollection<TrackerPresetEntity> Presets { get; set; } = new List<TrackerPresetEntity>();

    /// <summary>
    /// Navigation property for notification thresholds
    /// </summary>
    public virtual ICollection<TrackerNotificationThresholdEntity> NotificationThresholds { get; set; } = new List<TrackerNotificationThresholdEntity>();
}
