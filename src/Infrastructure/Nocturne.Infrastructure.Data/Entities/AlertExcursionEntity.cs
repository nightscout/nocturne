using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Nocturne.Core.Constants;

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
    [MaxLength(ColumnLengths.AlertExcursion.AcknowledgedBy)]
    public string? AcknowledgedBy { get; set; }

    /// <summary>
    /// When hysteresis countdown began (condition cleared but waiting for hysteresis window).
    /// </summary>
    [Column("hysteresis_started_at")]
    public DateTime? HysteresisStartedAt { get; set; }

    /// <summary>
    /// Set only on the row of a notice about the rule: for a notice that its stored condition cannot
    /// be evaluated, the version of the condition the notice was sent for
    /// (<c>UnevaluableRuleNotice.VersionOf</c>), or of its auto-resolve and snooze trees for a notice
    /// that only those cannot (<c>UnevaluableRuleNotice.PartsVersionOf</c>); for a notice that its
    /// hold reached the bound, which
    /// also has <see cref="HeldSince"/>, <c>held:</c> and that instant. Its instance stays unresolved
    /// while the notice stands. Null on every excursion of a condition;
    /// <see cref="Extensions.AlertExcursionQueries"/> tells the two apart.
    /// </summary>
    [Column("unevaluable_condition_version")]
    [MaxLength(64)]
    public string? UnevaluableConditionVersion { get; set; }

    /// <summary>
    /// Set only on the row of a notice that the rule's hold reached the engine's bound: the instant
    /// the hold it is for began (<see cref="AlertTrackerStateEntity.HeldSince"/>). Its instance stays
    /// unresolved while that hold goes on. Such a row carries an
    /// <see cref="UnevaluableConditionVersion"/> as well (<c>held:</c> and the instant), so every
    /// reader that excludes notices excludes it, an image that predates this column among them.
    /// </summary>
    [Column("held_since")]
    public DateTime? HeldSince { get; set; }

    /// <summary>
    /// Set while the host still owes the work its opening calls for: the instance, its delivery on
    /// each channel and the info auto-acknowledge for an excursion, the delivery for a notice. It is
    /// written with the row, in the transaction that opens it, and cleared once that work is done.
    /// The value is when the work was last taken up, which a later taker reads as a lease
    /// (<c>AlertSweepJob.OwedTransitionLease</c>). Null on a row written before the column existed,
    /// so an upgrade owes nothing for it. A notice keeps it when it is resolved, until its dispatch or
    /// the sweep settles it, so a run that dies in the dispatch has its close run again by the sweep.
    /// A snooze's re-notification sets it while it sends, when it is not set, for the same reason.
    /// </summary>
    [Column("opened_owed_at")]
    public DateTime? OpenedOwedAt { get; set; }

    /// <summary>
    /// As <see cref="OpenedOwedAt"/>, for the work the excursion's close calls for
    /// (<c>IExcursionResolutionHandler</c>), written in the transaction that closes it, or that ends a
    /// notice.
    /// </summary>
    [Column("closed_owed_at")]
    public DateTime? ClosedOwedAt { get; set; }

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
