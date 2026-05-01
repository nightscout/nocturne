using System.Text.Json;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;

namespace Nocturne.API.Services.Alerts.Evaluators;

/// <summary>
/// Evaluates a glucose threshold condition, triggering when the latest CGM reading
/// is strictly above or below a configured value.
/// </summary>
/// <remarks>
/// Returns <see langword="false"/> when <see cref="SensorContext.LatestValue"/> is
/// <see langword="null"/> (no current reading available).
/// </remarks>
/// <seealso cref="IConditionEvaluator"/>
public class ThresholdEvaluator : IConditionEvaluator
{

    /// <inheritdoc/>
    public AlertConditionType ConditionType => AlertConditionType.Threshold;

    /// <inheritdoc/>
    /// <param name="conditionParamsJson">JSON representation of a <see cref="ThresholdCondition"/>.</param>
    /// <param name="context">Current sensor context containing <see cref="SensorContext.LatestValue"/>.</param>
    /// <param name="ct">Cancellation token (unused; this evaluator performs no I/O).</param>
    /// <returns>
    /// <see langword="true"/> when the latest glucose value satisfies the configured
    /// direction (<c>above</c> or <c>below</c>) and threshold value.
    /// </returns>
    public Task<bool> EvaluateAsync(string conditionParamsJson, SensorContext context, CancellationToken ct)
    {
        if (context.LatestValue is null)
            return Task.FromResult(false);

        var condition = JsonSerializer.Deserialize<ThresholdCondition>(conditionParamsJson, EvaluatorJson.Options);
        if (condition is null)
            return Task.FromResult(false);

        var result = condition.Direction.ToLowerInvariant() switch
        {
            "below" => context.LatestValue.Value < condition.Value,
            "above" => context.LatestValue.Value > condition.Value,
            _ => false
        };
        return Task.FromResult(result);
    }
}
