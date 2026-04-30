using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;

namespace Nocturne.Core.Contracts.Alerts;

/// <summary>
/// Evaluator that determines whether a single condition type is met given the
/// current sensor data. Implementations may be stateful (e.g. the sustained
/// evaluator persists per-path timers) and so the contract is asynchronous and
/// accepts a <see cref="CancellationToken"/>.
/// </summary>
public interface IConditionEvaluator
{
    /// <summary>
    /// Discriminator value matching the condition type stored on the rule.
    /// </summary>
    AlertConditionType ConditionType { get; }

    /// <summary>
    /// Evaluate the condition against the current sensor context.
    /// </summary>
    /// <param name="conditionParamsJson">JSON string containing the condition parameters.</param>
    /// <param name="context">Current sensor snapshot, including the rule id and condition path used by stateful evaluators.</param>
    /// <param name="ct">Cancellation token forwarded to any I/O performed during evaluation.</param>
    /// <returns>True if the condition is met (alert should fire).</returns>
    Task<bool> EvaluateAsync(string conditionParamsJson, SensorContext context, CancellationToken ct);
}
