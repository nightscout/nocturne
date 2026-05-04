using System.Text.Json;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;

namespace Nocturne.API.Services.Alerts.Evaluators;

/// <summary>
/// Evaluates an insulin-on-board comparison against <see cref="SensorContext.IobUnits"/>.
/// </summary>
/// <remarks>
/// Returns <see langword="false"/> when <see cref="SensorContext.IobUnits"/> is null
/// (no IOB data available). Operator dispatch is delegated to <see cref="ComparisonOps"/>.
/// </remarks>
/// <seealso cref="IConditionEvaluator"/>
/// <seealso cref="ComparisonOps"/>
public class IobEvaluator : IConditionEvaluator
{

    /// <inheritdoc/>
    public AlertConditionType ConditionType => AlertConditionType.Iob;

    /// <inheritdoc/>
    /// <param name="conditionParamsJson">JSON representation of an <see cref="IobCondition"/>.</param>
    /// <param name="context">Current sensor context including <see cref="SensorContext.IobUnits"/>.</param>
    /// <param name="ct">Cancellation token (unused; this evaluator performs no I/O).</param>
    public Task<bool> EvaluateAsync(string conditionParamsJson, SensorContext context, CancellationToken ct)
    {
        if (context.IobUnits is null)
            return Task.FromResult(false);

        var condition = JsonSerializer.Deserialize<IobCondition>(conditionParamsJson, EvaluatorJson.Options);
        if (condition is null)
            return Task.FromResult(false);

        return Task.FromResult(ComparisonOps.Compare(context.IobUnits.Value, condition.Operator, condition.Value));
    }
}
