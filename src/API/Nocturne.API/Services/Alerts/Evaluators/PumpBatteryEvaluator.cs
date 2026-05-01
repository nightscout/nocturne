using System.Text.Json;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;

namespace Nocturne.API.Services.Alerts.Evaluators;

/// <summary>
/// Evaluates a pump-battery comparison against
/// <see cref="SensorContext.PumpBatteryPercent"/>.
/// </summary>
/// <remarks>
/// Cold-start guard uses <see cref="SensorContext.HasEverPumpSnapshot"/>: a tenant that has
/// never reported pump status returns false rather than matching low-battery rules on absent
/// data. Returns false when the snapshot exists but the battery field is null.
/// Operator dispatch is delegated to <see cref="ComparisonOps"/>.
/// </remarks>
/// <seealso cref="IConditionEvaluator"/>
/// <seealso cref="ComparisonOps"/>
public class PumpBatteryEvaluator : IConditionEvaluator
{

    /// <inheritdoc/>
    public AlertConditionType ConditionType => AlertConditionType.PumpBattery;

    /// <inheritdoc/>
    /// <param name="conditionParamsJson">JSON representation of a <see cref="PumpBatteryCondition"/>.</param>
    /// <param name="context">Current sensor context including <see cref="SensorContext.PumpBatteryPercent"/>.</param>
    /// <param name="ct">Cancellation token (unused; this evaluator performs no I/O).</param>
    public Task<bool> EvaluateAsync(string conditionParamsJson, SensorContext context, CancellationToken ct)
    {
        if (!context.HasEverPumpSnapshot)
            return Task.FromResult(false);

        if (context.PumpBatteryPercent is null)
            return Task.FromResult(false);

        var condition = JsonSerializer.Deserialize<PumpBatteryCondition>(conditionParamsJson, EvaluatorJson.Options);
        if (condition is null)
            return Task.FromResult(false);

        return Task.FromResult(ComparisonOps.Compare(
            context.PumpBatteryPercent.Value, condition.Operator, condition.Value));
    }
}
