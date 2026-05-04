using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// A live instance of an alert within an excursion. Now flat — schedule/escalation-step
/// linkage was removed when the alerts redesign collapsed multi-step delivery into a
/// per-rule channel list.
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
    /// Instance lifecycle status: "triggered" | "acknowledged" | "resolved".
    /// (The legacy <c>"escalating"</c> status is gone — escalation is now expressed
    /// as separate alert rules referencing each other via the <c>alert_state</c> condition.)
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
    /// Time until which the alert is snoozed
    /// </summary>
    [Column("snoozed_until")]
    public DateTime? SnoozedUntil { get; set; }

    /// <summary>
    /// Number of times the alert has been snoozed
    /// </summary>
    [Column("snooze_count")]
    public int SnoozeCount { get; set; }

    /// <summary>
    /// Why delivery for this instance was suppressed at fire time, if applicable.
    /// One of: <c>dnd</c> (tenant Do Not Disturb mode active and the rule was not allowed
    /// through). Null when delivery proceeded normally. Used by Replay/History so the user
    /// can see what they would have been notified of had DND been off.
    /// </summary>
    [Column("suppression_reason")]
    [MaxLength(32)]
    public string? SuppressionReason { get; set; }

    /// <summary>
    /// True when the instance was created by a test-fire endpoint rather than a real
    /// excursion transition. Test instances are excluded from active-alerts queries and
    /// the FE renders them distinctly in History so users can verify their channels work
    /// without polluting their real alert log.
    /// </summary>
    [Column("is_test")]
    public bool IsTest { get; set; }

    // Navigation

    /// <summary>
    /// Navigation property to the associated alert excursion
    /// </summary>
    public AlertExcursionEntity? AlertExcursion { get; set; }
}
