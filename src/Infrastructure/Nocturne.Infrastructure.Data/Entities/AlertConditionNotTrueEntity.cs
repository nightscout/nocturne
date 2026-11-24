using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// A <c>sustained</c> path with no run, and the last evaluation recorded as finding its child not
/// true (<see cref="Core.Contracts.Alerts.ConditionTimers.NotTrue"/>). Kept apart from
/// <see cref="AlertConditionTimerEntity"/>, whose rows are all runs, so an image that knows only that
/// table still reads every row there as a run. Composite key is (AlertRuleId, ConditionPath).
/// </summary>
[Table("alert_condition_not_true")]
public class AlertConditionNotTrueEntity : ITenantScoped
{
    [Column("tenant_id")]
    public Guid TenantId { get; set; }

    [Column("alert_rule_id")]
    public Guid AlertRuleId { get; set; }

    [Column("condition_path")]
    [MaxLength(512)]
    public string ConditionPath { get; set; } = "";

    [Column("not_true_at")]
    public DateTime NotTrueAt { get; set; }

    public AlertRuleEntity? AlertRule { get; set; }
}
