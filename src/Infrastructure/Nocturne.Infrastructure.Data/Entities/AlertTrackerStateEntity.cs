using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// Per-rule state machine tracker. 1:1 with AlertRuleEntity (PK = AlertRuleId).
/// States: idle -> confirming -> active -> hysteresis -> idle.
/// </summary>
[Table("alert_tracker_state")]
public class AlertTrackerStateEntity : ITenantScoped
{
    /// <summary>
    /// Primary key — same as the owning AlertRule's Id (1:1 relationship).
    /// </summary>
    [Key]
    [Column("alert_rule_id")]
    public Guid AlertRuleId { get; set; }

    /// <summary>
    /// Identifier of the tenant this tracker state belongs to
    /// </summary>
    [Column("tenant_id")]
    public Guid TenantId { get; set; }

    /// <summary>
    /// Current state: "idle" | "confirming" | "active" | "hysteresis"
    /// </summary>
    [Column("state")]
    [MaxLength(16)]
    public string State { get; set; } = "idle";

    /// <summary>
    /// Number of consecutive confirming readings observed.
    /// </summary>
    [Column("confirmation_count")]
    public int ConfirmationCount { get; set; }

    /// <summary>
    /// FK to the currently-open excursion, if any.
    /// </summary>
    [Column("active_excursion_id")]
    public Guid? ActiveExcursionId { get; set; }

    /// <summary>
    /// When the tracker state was last updated
    /// </summary>
    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When the active excursion entered hysteresis; null outside the "hysteresis" state.
    /// </summary>
    [Column("hysteresis_started_at")]
    public DateTime? HysteresisStartedAt { get; set; }

    /// <summary>
    /// Idle after an auto-resolve closed an excursion whose condition still held; cleared by the
    /// first evaluation that finds the condition false.
    /// </summary>
    [Column("awaiting_rearm")]
    public bool AwaitingRearm { get; set; }

    // Navigation

    /// <summary>
    /// Navigation property to the associated alert rule
    /// </summary>
    public AlertRuleEntity? AlertRule { get; set; }

    /// <summary>
    /// Navigation property to the currently active excursion
    /// </summary>
    public AlertExcursionEntity? ActiveExcursion { get; set; }
}
