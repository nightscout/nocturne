using System.Text.Json;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;

namespace Nocturne.API.Services.Alerts.Evaluators;

/// <summary>
/// Evaluates an uploader (phone) battery comparison against
/// <see cref="SensorContext.UploaderBatteryPercent"/>.
/// </summary>
/// <remarks>
/// Cold-start guard uses <see cref="SensorContext.HasEverUploaderSnapshot"/>: a tenant that
/// has never reported uploader status returns false. Returns false when the snapshot exists
/// but the battery field is null.
/// Operator dispatch is delegated to <see cref="ComparisonOps"/>.
/// </remarks>
/// <seealso cref="IConditionEvaluator"/>
/// <seealso cref="ComparisonOps"/>
public class UploaderBatteryEvaluator : IConditionEvaluator
{

    /// <inheritdoc/>
    public AlertConditionType ConditionType => AlertConditionType.UploaderBattery;

    /// <inheritdoc/>
    /// <param name="conditionParamsJson">JSON representation of an <see cref="UploaderBatteryCondition"/>.</param>
    /// <param name="context">Current sensor context including <see cref="SensorContext.UploaderBatteryPercent"/>.</param>
    /// <param name="ct">Cancellation token (unused; this evaluator performs no I/O).</param>
    public Task<bool> EvaluateAsync(string conditionParamsJson, SensorContext context, CancellationToken ct)
    {
        if (!context.HasEverUploaderSnapshot)
            return Task.FromResult(false);

        if (context.UploaderBatteryPercent is null)
            return Task.FromResult(false);

        var condition = JsonSerializer.Deserialize<UploaderBatteryCondition>(conditionParamsJson, EvaluatorJson.Options);
        if (condition is null)
            return Task.FromResult(false);

        return Task.FromResult(ComparisonOps.Compare(
            context.UploaderBatteryPercent.Value, condition.Operator, condition.Value));
    }
}
