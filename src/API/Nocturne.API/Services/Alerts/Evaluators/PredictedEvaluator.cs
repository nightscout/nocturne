using System.Text.Json;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;

namespace Nocturne.API.Services.Alerts.Evaluators;

/// <summary>
/// Evaluates a predicted-glucose condition by scanning <see cref="SensorContext.Predictions"/>
/// for any forecast point inside the configured horizon that satisfies the comparison.
/// </summary>
/// <remarks>
/// Returns <see langword="true"/> as soon as one prediction within
/// <see cref="PredictedCondition.WithinMinutes"/> matches; if no predictions are present, or
/// none inside the horizon match, returns <see langword="false"/>. Operator dispatch is
/// delegated to <see cref="ComparisonOps"/>.
/// </remarks>
/// <seealso cref="IConditionEvaluator"/>
/// <seealso cref="ComparisonOps"/>
public class PredictedEvaluator : IConditionEvaluator
{

    /// <inheritdoc/>
    public AlertConditionType ConditionType => AlertConditionType.Predicted;

    /// <inheritdoc/>
    /// <param name="conditionParamsJson">JSON representation of a <see cref="PredictedCondition"/>.</param>
    /// <param name="context">Current sensor context including <see cref="SensorContext.Predictions"/>.</param>
    /// <param name="ct">Cancellation token (unused; this evaluator performs no I/O).</param>
    public Task<bool> EvaluateAsync(string conditionParamsJson, SensorContext context, CancellationToken ct)
    {
        var condition = JsonSerializer.Deserialize<PredictedCondition>(conditionParamsJson, EvaluatorJson.Options);
        if (condition is null)
            return Task.FromResult(false);

        foreach (var p in context.Predictions)
        {
            if (p.OffsetMinutes > condition.WithinMinutes)
                continue;
            if (ComparisonOps.Compare(p.Mgdl, condition.Operator, condition.Value))
                return Task.FromResult(true);
        }
        return Task.FromResult(false);
    }
}
