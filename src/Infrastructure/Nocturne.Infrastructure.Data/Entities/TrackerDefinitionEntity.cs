using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Nocturne.Core.Models;

namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// PostgreSQL entity for TrackerDefinition (reusable tracker template)
/// Defines a type of tracker with notification thresholds and event matching
/// </summary>
[Table("tracker_definitions")]
public class TrackerDefinitionEntity : ITenantScoped
{
    /// <summary>
    /// Identifier of the tenant this tracker definition belongs to.
    /// </summary>
    [Column("tenant_id")]
    public Guid TenantId { get; set; }

    /// <summary>
    /// Primary key - UUID for tracker definition
    /// </summary>
    [Key]
    public Guid Id { get; set; }

    /// <summary>
    /// The subject who created this row. It grants nothing: trackers are the tenant's, and any
    /// member may read or change them.
    /// </summary>
    [Column("created_by")]
    [MaxLength(255)]
    public string CreatedBy { get; set; } = string.Empty;

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
    /// Device event types that end the running instance without starting another (JSON array),
    /// e.g. ["Pod Deactivated"]. Subject to <see cref="TriggerNotesContains"/> like the triggers.
    /// </summary>
    [Column("end_event_types", TypeName = "jsonb")]
    public string EndEventTypes { get; set; } = "[]";

    /// <summary>
    /// The <see cref="Nocturne.Core.Models.V4.PumpCapability"/> names the tracked hardware provides
    /// (JSON array), e.g. ["Site","Reservoir","Power"] for a pod. Empty when not known, and then what
    /// the tracker stands for is read from its triggers and category.
    /// </summary>
    [Column("covers", TypeName = "jsonb")]
    public string Covers { get; set; } = "[]";

    /// <summary>
    /// The catalog pump part the tracker follows, as the catalog id and the part's key
    /// (e.g. "omnipod-5/pod"). Null for a tracker that follows no catalog part.
    /// </summary>
    [Column("device_component")]
    [MaxLength(128)]
    public string? DeviceComponent { get; set; }

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
    /// Reservoir category only: units level below which the synced managed reservoir
    /// rule fires. Null = no level rule.
    /// </summary>
    [Column("low_reservoir_units")]
    public double? LowReservoirUnits { get; set; }

    /// <summary>
    /// Urgency of the low-reservoir level rule. Only meaningful while
    /// <see cref="LowReservoirUnits"/> is set.
    /// </summary>
    [Column("low_reservoir_urgency")]
    public NotificationUrgency LowReservoirUrgency { get; set; } = NotificationUrgency.Warn;

    /// <summary>
    /// Show in quick-add favorites
    /// </summary>
    [Column("is_favorite")]
    public bool IsFavorite { get; set; } = false;

    /// <summary>
    /// Event type to create when this tracker is started (for Nightscout compatibility)
    /// e.g., "Site Change", "Sensor Start"
    /// </summary>
    [Column("start_event_type")]
    [MaxLength(100)]
    public string? StartEventType { get; set; }

    /// <summary>
    /// Event type to create when this tracker is completed (for Nightscout compatibility)
    /// e.g., "Sensor Change", "Site Change"
    /// </summary>
    [Column("completion_event_type")]
    [MaxLength(100)]
    public string? CompletionEventType { get; set; }

    /// <summary>
    /// Dashboard visibility: Off, Always, Info, Warn, Hazard, Urgent
    /// "Off" = never show, "Always" = always show, others = show when age reaches that notification level
    /// </summary>
    [Column("dashboard_visibility")]
    public DashboardVisibility DashboardVisibility { get; set; } = DashboardVisibility.Always;

    /// <summary>
    /// Tracker mode: Duration (time-based) or Event (scheduled datetime)
    /// </summary>
    [Column("mode")]
    public TrackerMode Mode { get; set; } = TrackerMode.Duration;

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
