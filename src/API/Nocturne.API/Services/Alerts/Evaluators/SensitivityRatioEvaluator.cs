using System.Text.Json;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;

namespace Nocturne.API.Services.Alerts.Evaluators;

/// <summary>
/// Evaluates an OpenAPS-style sensitivity-ratio (autosens) comparison against
/// <see cref="SensorContext.SensitivityRatio"/>.
/// </summary>
/// <remarks>
/// Cold-start guard uses <see cref="SensorContext.HasEverApsSensitivity"/>: a tenant whose
/// APS has never reported a sensitivity ratio (e.g. Loop iOS) returns false rather than
/// matching on absent data. Returns false when no sensitivity ratio is currently observed.
/// Operator dispatch is delegated to <see cref="ComparisonOps"/>.
/// </remarks>
/// <seealso cref="IConditionEvaluator"/>
/// <seealso cref="ComparisonOps"/>
public class SensitivityRatioEvaluator : IConditionEvaluator
{

    /// <inheritdoc/>
    public AlertConditionType ConditionType => AlertConditionType.SensitivityRatio;

    /// <inheritdoc/>
    /// <param name="conditionParamsJson">JSON representation of a <see cref="SensitivityRatioCondition"/>.</param>
    /// <param name="context">Current sensor context including <see cref="SensorContext.SensitivityRatio"/>.</param>
    /// <param name="ct">Cancellation token (unused; this evaluator performs no I/O).</param>
    public Task<bool> EvaluateAsync(string conditionParamsJson, SensorContext context, CancellationToken ct)
    {
        if (!context.HasEverApsSensitivity)
            return Task.FromResult(false);

        if (context.SensitivityRatio is null)
            return Task.FromResult(false);

        var condition = JsonSerializer.Deserialize<SensitivityRatioCondition>(conditionParamsJson, EvaluatorJson.Options);
        if (condition is null)
            return Task.FromResult(false);

        return Task.FromResult(ComparisonOps.Compare(
            context.SensitivityRatio.Value, condition.Operator, condition.Value));
    }
}
