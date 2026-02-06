using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// PostgreSQL entity for AlertHistory (alert state and delivery tracking)
/// Maps to notification alert history and state management
/// </summary>
[Table("alert_history")]
public class AlertHistoryEntity : IHasCreatedAt, IHasUpdatedAt
{
    /// <summary>
    /// Primary key - UUID for alert history entry
    /// </summary>
    [Key]
    public Guid Id { get; set; }

    /// <summary>
    /// User identifier this alert belongs to
    /// </summary>
    [Column("user_id")]
    [MaxLength(255)]
    public string UserId { get; set; } = string.Empty;

    /// <summary>
    /// Foreign key to the alert rule that triggered this alert
    /// </summary>
    [Column("alert_rule_id")]
    public Guid? AlertRuleId { get; set; }

    /// <summary>
    /// Type of alert (LOW, HIGH, URGENT_LOW, URGENT_HIGH, etc.)
    /// </summary>
    [Column("alert_type")]
    [MaxLength(50)]
    public string AlertType { get; set; } = string.Empty;

    /// <summary>
    /// Glucose value that triggered the alert (mg/dL)
    /// </summary>
    [Column("glucose_value")]
    public decimal? GlucoseValue { get; set; }

    /// <summary>
    /// Threshold value that was crossed
    /// </summary>
    [Column("threshold")]
    public decimal? Threshold { get; set; }

    /// <summary>
    /// Current status of the alert (ACTIVE, ACKNOWLEDGED, SNOOZED, RESOLVED, etc.)
    /// </summary>
    [Column("status")]
    [MaxLength(50)]
    public string Status { get; set; } = string.Empty;

    /// <summary>
    /// When the alert was first triggered
    /// </summary>
    [Column("trigger_time")]
    public DateTime TriggerTime { get; set; }

    /// <summary>
    /// When the alert was acknowledged (if applicable)
    /// </summary>
    [Column("acknowledged_at")]
    public DateTime? AcknowledgedAt { get; set; }

    /// <summary>
    /// When the alert was resolved (if applicable)
    /// </summary>
    [Column("resolved_at")]
    public DateTime? ResolvedAt { get; set; }

    /// <summary>
    /// Tracking of notifications sent (stored as JSON)
    /// </summary>
    [Column("notifications_sent", TypeName = "jsonb")]
    public string NotificationsSent { get; set; } = "[]";

    /// <summary>
    /// Current escalation level (0 = initial alert)
    /// </summary>
    [Column("escalation_level")]
    public int EscalationLevel { get; set; } = 0;

    /// <summary>
    /// Time until which the alert is snoozed (if applicable)
    /// </summary>
    [Column("snooze_until")]
    public DateTime? SnoozeUntil { get; set; }

    /// <summary>
    /// Next scheduled escalation time
    /// </summary>
    [Column("next_escalation_time")]
    public DateTime? NextEscalationTime { get; set; }

    /// <summary>
    /// Escalation attempts (stored as JSON)
    /// </summary>
    [Column("escalation_attempts", TypeName = "jsonb")]
    public string EscalationAttempts { get; set; } = "[]";

    /// <summary>
    /// Reason for escalation
    /// </summary>
    [Column("escalation_reason")]
    [MaxLength(500)]
    public string? EscalationReason { get; set; }

    /// <summary>
    /// Whether escalation is paused
    /// </summary>
    [Column("escalation_paused")]
    public bool EscalationPaused { get; set; } = false;

    /// <summary>
    /// Acknowledgment notes
    /// </summary>
    [Column("acknowledgment_notes")]
    [MaxLength(1000)]
    public string? AcknowledgmentNotes { get; set; }

    /// <summary>
    /// Snooze reason
    /// </summary>
    [Column("snooze_reason")]
    [MaxLength(500)]
    public string? SnoozeReason { get; set; }

    /// <summary>
    /// Number of times this alert has been snoozed
    /// </summary>
    [Column("snooze_count")]
    public int SnoozeCount { get; set; } = 0;

    /// <summary>
    /// When this alert history entry was created
    /// </summary>
    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When this alert history entry was last updated
    /// </summary>
    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Navigation property to the alert rule
    /// </summary>
    [ForeignKey(nameof(AlertRuleId))]
    public virtual AlertRuleEntity? AlertRule { get; set; }
}
