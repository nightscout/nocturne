using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// Per-rule, per-condition-path timer used by the sustained evaluator to track when a child
/// condition first became true. The sustained window is measured from <see cref="FirstTrueAt"/>;
/// rows are removed once the child condition is no longer satisfied. Composite key is
/// (AlertRuleId, ConditionPath).
/// </summary>
[Table("alert_condition_timers")]
public class AlertConditionTimerEntity : ITenantScoped
{
    /// <summary>
    /// Tenant that owns the timer; used by RLS to scope the row.
    /// </summary>
    [Column("tenant_id")]
    public Guid TenantId { get; set; }

    /// <summary>
    /// Identifier of the alert rule the timer belongs to.
    /// </summary>
    [Column("alert_rule_id")]
    public Guid AlertRuleId { get; set; }

    /// <summary>
    /// Stable path to the sustained node within the rule's condition tree
    /// (for example, "composite[0].sustained").
    /// </summary>
    [Column("condition_path")]
    [MaxLength(512)]
    public string ConditionPath { get; set; } = "";

    /// <summary>
    /// UTC timestamp captured the first time the child evaluated true.
    /// </summary>
    [Column("first_true_at")]
    public DateTime FirstTrueAt { get; set; }

    /// <summary>
    /// Navigation property to the associated alert rule.
    /// </summary>
    public AlertRuleEntity? AlertRule { get; set; }
}
