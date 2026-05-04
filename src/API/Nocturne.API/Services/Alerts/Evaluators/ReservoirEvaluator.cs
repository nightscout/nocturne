using System.Text.Json;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;

namespace Nocturne.API.Services.Alerts.Evaluators;

/// <summary>
/// Evaluates a pump-reservoir comparison against <see cref="SensorContext.ReservoirUnits"/>.
/// </summary>
/// <remarks>
/// Returns <see langword="false"/> when <see cref="SensorContext.ReservoirUnits"/> is null
/// (no reservoir data available). Operator dispatch is delegated to <see cref="ComparisonOps"/>.
/// </remarks>
/// <seealso cref="IConditionEvaluator"/>
/// <seealso cref="ComparisonOps"/>
public class ReservoirEvaluator : IConditionEvaluator
{

    /// <inheritdoc/>
    public AlertConditionType ConditionType => AlertConditionType.Reservoir;

    /// <inheritdoc/>
    /// <param name="conditionParamsJson">JSON representation of a <see cref="ReservoirCondition"/>.</param>
    /// <param name="context">Current sensor context including <see cref="SensorContext.ReservoirUnits"/>.</param>
    /// <param name="ct">Cancellation token (unused; this evaluator performs no I/O).</param>
    public Task<bool> EvaluateAsync(string conditionParamsJson, SensorContext context, CancellationToken ct)
    {
        if (context.ReservoirUnits is null)
            return Task.FromResult(false);

        var condition = JsonSerializer.Deserialize<ReservoirCondition>(conditionParamsJson, EvaluatorJson.Options);
        if (condition is null)
            return Task.FromResult(false);

        return Task.FromResult(ComparisonOps.Compare(context.ReservoirUnits.Value, condition.Operator, condition.Value));
    }
}
