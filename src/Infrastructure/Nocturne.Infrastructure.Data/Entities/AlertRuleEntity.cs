using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Nocturne.Core.Models.Alerts;

namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// A composable alert rule with condition tree, hysteresis, and confirmation settings.
/// Each rule owns schedules, which own escalation chains.
/// </summary>
[Table("alert_rules")]
public class AlertRuleEntity : ITenantScoped, IAuditable
{
    /// <summary>
    /// Unique identifier for the alert rule
    /// </summary>
    [Key]
    public Guid Id { get; set; }

    /// <summary>
    /// Identifier of the tenant this alert rule belongs to
    /// </summary>
    [Column("tenant_id")]
    public Guid TenantId { get; set; }

    /// <summary>
    /// Display name of the alert rule
    /// </summary>
    [Column("name")]
    [MaxLength(128)]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Detailed description of the alert rule
    /// </summary>
    [Column("description")]
    [MaxLength(512)]
    public string? Description { get; set; }

    /// <summary>
    /// Condition type discriminator.
    /// </summary>
    [Column("condition_type")]
    [MaxLength(32)]
    public AlertConditionType ConditionType { get; set; } = AlertConditionType.Threshold;

    /// <summary>
    /// JSONB condition parameters (thresholds, rates, durations, composite children, etc.)
    /// </summary>
    [Column("condition_params", TypeName = "jsonb")]
    public string ConditionParams { get; set; } = "{}";

    /// <summary>
    /// Minutes the condition must remain cleared before transitioning back to idle.
    /// </summary>
    [Column("hysteresis_minutes")]
    public int HysteresisMinutes { get; set; }

    /// <summary>
    /// Number of consecutive readings that must satisfy the condition before firing.
    /// </summary>
    [Column("confirmation_readings")]
    public int ConfirmationReadings { get; set; } = 1;

    /// <summary>
    /// Alert severity. Critical alerts bypass quiet hours.
    /// </summary>
    [Column("severity")]
    [MaxLength(16)]
    public AlertRuleSeverity Severity { get; set; } = AlertRuleSeverity.Warning;

    /// <summary>
    /// Client-side presentation config (audio, visual, snooze). Stored as JSONB.
    /// The server stores this but does not make decisions based on it.
    /// </summary>
    [Column("client_configuration", TypeName = "jsonb")]
    public string ClientConfiguration { get; set; } = "{}";

    /// <summary>
    /// Whether the alert rule is currently active
    /// </summary>
    [Column("is_enabled")]
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// Order in which the rule should be processed or displayed
    /// </summary>
    [Column("sort_order")]
    public int SortOrder { get; set; }

    /// <summary>
    /// When the alert rule was created
    /// </summary>
    [AuditIgnored]
    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When the alert rule was last updated
    /// </summary>
    [AuditIgnored]
    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Navigation

    /// <summary>
    /// Collection of schedules associated with this alert rule
    /// </summary>
    public ICollection<AlertScheduleEntity> Schedules { get; set; } = [];

    /// <summary>
    /// Current state tracker for this alert rule
    /// </summary>
    public AlertTrackerStateEntity? TrackerState { get; set; }
}
