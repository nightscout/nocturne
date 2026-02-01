using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Nocturne.Core.Models;

namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// PostgreSQL entity for TrackerInstance (active tracking session)
/// Represents a running or completed tracker with lifecycle timestamps
/// </summary>
[Table("tracker_instances")]
public class TrackerInstanceEntity
{
    /// <summary>
    /// Primary key - UUID for tracker instance
    /// </summary>
    [Key]
    public Guid Id { get; set; }

    /// <summary>
    /// User identifier this instance belongs to
    /// </summary>
    [Column("user_id")]
    [MaxLength(255)]
    public string UserId { get; set; } = string.Empty;

    /// <summary>
    /// Foreign key to TrackerDefinition
    /// </summary>
    [Column("definition_id")]
    public Guid DefinitionId { get; set; }

    /// <summary>
    /// When the tracker was started
    /// </summary>
    [Column("started_at")]
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When the tracker was completed (null = still active)
    /// </summary>
    [Column("completed_at")]
    public DateTime? CompletedAt { get; set; }

    /// <summary>
    /// For Event mode: the scheduled datetime of the event
    /// </summary>
    [Column("scheduled_at")]
    public DateTime? ScheduledAt { get; set; }

    /// <summary>
    /// Optional: Treatment ID that started this tracker
    /// </summary>
    [Column("start_treatment_id")]
    [MaxLength(255)]
    public string? StartTreatmentId { get; set; }

    /// <summary>
    /// Optional: Treatment ID that completed this tracker
    /// </summary>
    [Column("complete_treatment_id")]
    [MaxLength(255)]
    public string? CompleteTreatmentId { get; set; }

    /// <summary>
    /// Notes when starting (e.g., "Left arm", "Lot #12345")
    /// </summary>
    [Column("start_notes")]
    [MaxLength(1000)]
    public string? StartNotes { get; set; }

    /// <summary>
    /// Notes when completing (e.g., "Sensor error E2 on day 8")
    /// </summary>
    [Column("completion_notes")]
    [MaxLength(1000)]
    public string? CompletionNotes { get; set; }

    /// <summary>
    /// Reason for completion (category-aware enum)
    /// </summary>
    [Column("completion_reason")]
    public CompletionReason? CompletionReason { get; set; }

    /// <summary>
    /// When the notification was last acknowledged
    /// </summary>
    [Column("last_acked_at")]
    public DateTime? LastAckedAt { get; set; }

    /// <summary>
    /// Snooze duration in minutes (from last ack)
    /// </summary>
    [Column("ack_snooze_mins")]
    public int? AckSnoozeMins { get; set; }

    /// <summary>
    /// Navigation property to definition
    /// </summary>
    [ForeignKey(nameof(DefinitionId))]
    public virtual TrackerDefinitionEntity Definition { get; set; } = null!;

    /// <summary>
    /// Calculated: expected end time based on definition lifespan or scheduled time
    /// </summary>
    [NotMapped]
    public DateTime? ExpectedEndAt =>
        Definition?.Mode == TrackerMode.Event
            ? ScheduledAt
            : Definition?.LifespanHours != null
                ? StartedAt.AddHours(Definition.LifespanHours.Value)
                : null;

    /// <summary>
    /// Calculated: is this tracker still active?
    /// </summary>
    [NotMapped]
    public bool IsActive => CompletedAt == null;

    /// <summary>
    /// Calculated: age in hours since start
    /// </summary>
    [NotMapped]
    public double AgeHours => (DateTime.UtcNow - StartedAt).TotalHours;

    /// <summary>
    /// Calculated: hours relative to reference point (StartedAt for Duration, ScheduledAt for Event)
    /// Negative means before the reference point (for Event mode)
    /// </summary>
    [NotMapped]
    public double? HoursFromScheduled =>
        ScheduledAt.HasValue
            ? (DateTime.UtcNow - ScheduledAt.Value).TotalHours
            : null;
}
