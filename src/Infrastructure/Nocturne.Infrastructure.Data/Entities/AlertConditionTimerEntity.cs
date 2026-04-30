using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// Per-rule, per-condition-path timer used by the sustained evaluator to track when a child
/// condition first became true. The sustained window is measured from <see cref="FirstTrueAt"/>;
/// rows are removed once the child condition is no longer satisfied. Composite key is
/// (RuleId, ConditionPath).
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
    /// Owning rule; part of the composite key alongside <see cref="ConditionPath"/>.
    /// </summary>
    [Column("rule_id")]
    public Guid RuleId { get; set; }

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
    /// Navigation to the owning rule.
    /// </summary>
    public AlertRuleEntity? Rule { get; set; }
}
