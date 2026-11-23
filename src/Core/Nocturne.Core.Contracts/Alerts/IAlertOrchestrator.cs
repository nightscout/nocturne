using Nocturne.Core.Models;

namespace Nocturne.Core.Contracts.Alerts;

/// <summary>
/// Top-level orchestrator for the alert pipeline. Evaluates enabled
/// <see cref="AlertRule"/> instances against incoming sensor data and triggers
/// excursion tracking, escalation, and delivery as needed.
/// </summary>
/// <seealso cref="IExcursionTracker"/>
/// <seealso cref="IAlertEvaluationEngine"/>
/// <seealso cref="IAlertDeliveryService"/>
public interface IAlertOrchestrator
{
    /// <summary>
    /// Evaluate a specific set of rules for the current tenant against <paramref name="context"/>, with
    /// enrichment, DND suppression and delivery side effects. Used for the rules a committed write
    /// moved (<see cref="ICanonicalAlertEvaluator"/>) and by the alert sweep for wall-clock-driven rules
    /// (e.g. <c>tracker_age</c>) that must fire even when no new reading arrives.
    /// </summary>
    /// <param name="rules">The rule snapshots to evaluate. Rules from other tenants are a caller bug.</param>
    /// <param name="enabledRuleIds">
    /// Every enabled rule of the tenant, <paramref name="rules"/> or not. An <c>alert_state</c>
    /// reference resolves when its target is in this set.
    /// </param>
    /// <param name="context">The base <see cref="SensorContext"/> to enrich and evaluate against.</param>
    Task EvaluateRulesAsync(
        IReadOnlyList<AlertRuleSnapshot> rules,
        IReadOnlySet<Guid> enabledRuleIds,
        SensorContext context,
        CancellationToken ct) =>
        EvaluateRulesAsync(rules, enabledRuleIds, context, readAt: null, ct);

    /// <summary>
    /// <see cref="EvaluateRulesAsync(IReadOnlyList{AlertRuleSnapshot}, IReadOnlySet{Guid}, SensorContext, CancellationToken)"/>
    /// for a context built from a read of the canonical stream that began at <paramref name="readAt"/>.
    /// A rule a newer context has already decided is not evaluated again with this one; every other
    /// rule is.
    /// </summary>
    /// <param name="readAt">
    /// When the read began, in the host's read order. Null for a context whose read is not ordered
    /// with the others (the sweep's): it is neither checked nor recorded, since a read ordered late
    /// could stop a newer reading's evaluation of a rule.
    /// </param>
    Task<AlertPassOutcome> EvaluateRulesAsync(
        IReadOnlyList<AlertRuleSnapshot> rules,
        IReadOnlySet<Guid> enabledRuleIds,
        SensorContext context,
        long? readAt,
        CancellationToken ct);
}

/// <summary>What an evaluation pass made of the rules it was given.</summary>
/// <remarks>
/// A rule whose condition cannot be evaluated, or whose <c>alert_state</c> names no enabled rule, has
/// nothing to decide and counts as decided.
/// </remarks>
public enum AlertPassOutcome
{
    /// <summary>Every rule was decided by this pass.</summary>
    Decided,

    /// <summary>No rule failed, and a newer context had decided at least one.</summary>
    Superseded,

    /// <summary>At least one rule threw, so it is undecided.</summary>
    Failed,
}
