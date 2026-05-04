using System.Text.Json;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;

namespace Nocturne.API.Services.Alerts.Evaluators;

/// <summary>
/// Evaluates a rate-of-change alert condition against the current glucose trend rate
/// supplied in the <see cref="SensorContext"/>.
/// </summary>
/// <remarks>
/// A "falling" condition triggers when <see cref="SensorContext.TrendRate"/> is at or below
/// the negated threshold; a "rising" condition triggers when the rate is at or above the threshold.
/// Returns <see langword="false"/> when <see cref="SensorContext.TrendRate"/> is <see langword="null"/>.
/// </remarks>
/// <seealso cref="IConditionEvaluator"/>
public class RateOfChangeEvaluator : IConditionEvaluator
{

    /// <inheritdoc/>
    public AlertConditionType ConditionType => AlertConditionType.RateOfChange;

    /// <inheritdoc/>
    /// <param name="conditionParamsJson">JSON representation of a <see cref="RateOfChangeCondition"/>.</param>
    /// <param name="context">Current sensor reading context containing <see cref="SensorContext.TrendRate"/>.</param>
    /// <param name="ct">Cancellation token (unused; this evaluator performs no I/O).</param>
    /// <returns>
    /// <see langword="true"/> when the current trend rate satisfies the configured direction and magnitude.
    /// </returns>
    public Task<bool> EvaluateAsync(string conditionParamsJson, SensorContext context, CancellationToken ct)
    {
        if (context.TrendRate is null)
            return Task.FromResult(false);

        var condition = JsonSerializer.Deserialize<RateOfChangeCondition>(conditionParamsJson, EvaluatorJson.Options);
        if (condition is null)
            return Task.FromResult(false);

        var result = condition.Direction.ToLowerInvariant() switch
        {
            // A "falling" rate of 3.0 means trigger when the actual rate <= -3.0
            "falling" => context.TrendRate.Value <= -condition.Rate,
            // A "rising" rate of 3.0 means trigger when the actual rate >= 3.0
            "rising" => context.TrendRate.Value >= condition.Rate,
            _ => false
        };
        return Task.FromResult(result);
    }
}
