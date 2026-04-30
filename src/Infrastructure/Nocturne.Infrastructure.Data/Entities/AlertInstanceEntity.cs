using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// A schedule-bound instance of an alert within an excursion.
/// Tracks which escalation step is active and when to escalate next.
/// </summary>
[Table("alert_instances")]
public class AlertInstanceEntity : ITenantScoped
{
    /// <summary>
    /// Unique identifier for the alert instance
    /// </summary>
    [Key]
    public Guid Id { get; set; }

    /// <summary>
    /// The unique identifier of the tenant this alert instance belongs to
    /// </summary>
    [Column("tenant_id")]
    public Guid TenantId { get; set; }

    /// <summary>
    /// Identifier of the alert excursion this instance belongs to
    /// </summary>
    [Column("alert_excursion_id")]
    public Guid AlertExcursionId { get; set; }

    /// <summary>
    /// Identifier of the alert schedule associated with this instance
    /// </summary>
    [Column("alert_schedule_id")]
    public Guid AlertScheduleId { get; set; }

    /// <summary>
    /// The current escalation step order being processed
    /// </summary>
    [Column("current_step_order")]
    public int CurrentStepOrder { get; set; }

    /// <summary>
    /// Instance lifecycle status: "triggered" | "escalating" | "acknowledged" | "resolved"
    /// </summary>
    [Column("status")]
    [MaxLength(16)]
    public string Status { get; set; } = "triggered";

    /// <summary>
    /// When the alert was first triggered for this instance
    /// </summary>
    [Column("triggered_at")]
    public DateTime TriggeredAt { get; set; }

    /// <summary>
    /// When the alert instance was resolved
    /// </summary>
    [Column("resolved_at")]
    public DateTime? ResolvedAt { get; set; }

    /// <summary>
    /// Why the instance was resolved. Mirrors
    /// <see cref="Nocturne.Core.Models.Alerts.ExcursionCloseReason"/>.
    /// Null on rows resolved before the column existed.
    /// </summary>
    [Column("resolution_reason")]
    [MaxLength(32)]
    public string? ResolutionReason { get; set; }

    /// <summary>
    /// When the engine should next attempt escalation to the following step.
    /// </summary>
    [Column("next_escalation_at")]
    public DateTime? NextEscalationAt { get; set; }

    /// <summary>
    /// Time until which the alert is snoozed
    /// </summary>
    [Column("snoozed_until")]
    public DateTime? SnoozedUntil { get; set; }

    /// <summary>
    /// Number of times the alert has been snoozed
    /// </summary>
    [Column("snooze_count")]
    public int SnoozeCount { get; set; }

    // Navigation

    /// <summary>
    /// Navigation property to the associated alert excursion
    /// </summary>
    public AlertExcursionEntity? AlertExcursion { get; set; }

    /// <summary>
    /// Navigation property to the associated alert schedule
    /// </summary>
    public AlertScheduleEntity? AlertSchedule { get; set; }
}
