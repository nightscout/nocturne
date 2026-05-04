using System.Text.Json;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;

namespace Nocturne.API.Services.Alerts.Evaluators;

/// <summary>
/// Evaluates a carbs-on-board comparison against <see cref="SensorContext.CobGrams"/>.
/// </summary>
/// <remarks>
/// Returns <see langword="false"/> when <see cref="SensorContext.CobGrams"/> is null
/// (no COB data available). Operator dispatch is delegated to <see cref="ComparisonOps"/>.
/// </remarks>
/// <seealso cref="IConditionEvaluator"/>
/// <seealso cref="ComparisonOps"/>
public class CobEvaluator : IConditionEvaluator
{

    /// <inheritdoc/>
    public AlertConditionType ConditionType => AlertConditionType.Cob;

    /// <inheritdoc/>
    /// <param name="conditionParamsJson">JSON representation of a <see cref="CobCondition"/>.</param>
    /// <param name="context">Current sensor context including <see cref="SensorContext.CobGrams"/>.</param>
    /// <param name="ct">Cancellation token (unused; this evaluator performs no I/O).</param>
    public Task<bool> EvaluateAsync(string conditionParamsJson, SensorContext context, CancellationToken ct)
    {
        if (context.CobGrams is null)
            return Task.FromResult(false);

        var condition = JsonSerializer.Deserialize<CobCondition>(conditionParamsJson, EvaluatorJson.Options);
        if (condition is null)
            return Task.FromResult(false);

        return Task.FromResult(ComparisonOps.Compare(context.CobGrams.Value, condition.Operator, condition.Value));
    }
}
