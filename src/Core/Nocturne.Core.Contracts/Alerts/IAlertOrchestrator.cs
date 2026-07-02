using Nocturne.Core.Models;

namespace Nocturne.Core.Contracts.Alerts;

/// <summary>
/// Top-level orchestrator for the alert pipeline. Evaluates all enabled
/// <see cref="AlertRule"/> instances against incoming sensor data and triggers
/// excursion tracking, escalation, and delivery as needed.
/// </summary>
/// <seealso cref="IExcursionTracker"/>
/// <seealso cref="IConditionEvaluator"/>
/// <seealso cref="IAlertDeliveryService"/>
public interface IAlertOrchestrator
{
    /// <summary>
    /// Evaluate all enabled rules for the current tenant against the latest sensor data.
    /// Called by the glucose ingest pipeline on each new reading.
    /// </summary>
    /// <param name="context">The current <see cref="SensorContext"/> containing the latest glucose reading and trend data.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when all rules have been evaluated and any resulting alerts dispatched.</returns>
    Task EvaluateAsync(SensorContext context, CancellationToken ct);

    /// <summary>
    /// Evaluate a specific set of rules for the current tenant against <paramref name="context"/>,
    /// with the same enrichment, DND suppression, and delivery side effects as
    /// <see cref="EvaluateAsync"/>. Used by the sweep service for wall-clock-driven rules
    /// (e.g. <c>tracker_age</c>) that must fire even when no new reading arrives.
    /// </summary>
    /// <param name="rules">The rule snapshots to evaluate. Rules from other tenants are a caller bug.</param>
    /// <param name="context">The base <see cref="SensorContext"/> to enrich and evaluate against.</param>
    /// <param name="ct">Cancellation token.</param>
    Task EvaluateRulesAsync(IReadOnlyList<AlertRuleSnapshot> rules, SensorContext context, CancellationToken ct);
}
