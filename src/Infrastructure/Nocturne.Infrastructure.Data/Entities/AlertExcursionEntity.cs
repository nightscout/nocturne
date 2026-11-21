using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// A single continuous excursion (out-of-range episode) for a rule.
/// Spans from first trigger to resolution + hysteresis clear.
/// </summary>
[Table("alert_excursions")]
public class AlertExcursionEntity : ITenantScoped, IAuditable
{
    /// <summary>
    /// Unique identifier for the alert excursion
    /// </summary>
    [Key]
    public Guid Id { get; set; }

    /// <summary>
    /// The unique identifier of the tenant this excursion belongs to
    /// </summary>
    [Column("tenant_id")]
    public Guid TenantId { get; set; }

    /// <summary>
    /// Identifier of the alert rule that triggered this excursion
    /// </summary>
    [Column("alert_rule_id")]
    public Guid AlertRuleId { get; set; }

    /// <summary>
    /// When the excursion condition was first met
    /// </summary>
    [Column("started_at")]
    public DateTime StartedAt { get; set; }

    /// <summary>
    /// When the excursion condition cleared
    /// </summary>
    [Column("ended_at")]
    public DateTime? EndedAt { get; set; }

    /// <summary>
    /// When the excursion was acknowledged by a user
    /// </summary>
    [Column("acknowledged_at")]
    public DateTime? AcknowledgedAt { get; set; }

    /// <summary>
    /// Who acknowledged the excursion (subject display name or external identifier).
    /// </summary>
    [Column("acknowledged_by")]
    [MaxLength(256)]
    public string? AcknowledgedBy { get; set; }

    /// <summary>
    /// When hysteresis countdown began (condition cleared but waiting for hysteresis window).
    /// </summary>
    [Column("hysteresis_started_at")]
    public DateTime? HysteresisStartedAt { get; set; }

    /// <summary>
    /// Set only on the row of a notice that the rule's stored condition cannot be evaluated: the
    /// version of the condition the notice was sent for (<c>UnevaluableRuleNotice.VersionOf</c>).
    /// Its instance stays unresolved while the notice stands. Null on every excursion of a
    /// condition; <see cref="Extensions.AlertExcursionQueries"/> tells the two apart.
    /// </summary>
    [Column("unevaluable_condition_version")]
    [MaxLength(64)]
    public string? UnevaluableConditionVersion { get; set; }

    /// <summary>
    /// The <see cref="EndedAt"/> of a notice that stands (PostgreSQL <c>infinity</c>), set to the
    /// time it is resolved. A null end would make it an open excursion to every reader of one. An
    /// end in the past would let the 24-hour history clamp, which shows an excursion while its end
    /// is null or within the last day, hide a notice that still stands from a clamped member.
    /// </summary>
    public static readonly DateTime NoticeStandingEnd = DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc);

    // Navigation

    /// <summary>
    /// Navigation property to the associated alert rule
    /// </summary>
    public AlertRuleEntity? AlertRule { get; set; }

    /// <summary>
    /// Collection of alert instances triggered during this excursion
    /// </summary>
    public ICollection<AlertInstanceEntity> Instances { get; set; } = [];
}
