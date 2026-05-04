using System.Text.Json;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;

namespace Nocturne.API.Services.Alerts.Evaluators;

/// <summary>
/// Evaluates a CGM sensor age comparison. The configured value is in days; the actual age
/// is derived from <c>now - </c><see cref="SensorContext.LastSensorStartAt"/>.
/// </summary>
/// <remarks>
/// Returns <see langword="false"/> when <see cref="SensorContext.LastSensorStartAt"/> is null
/// (no sensor start recorded). Operator dispatch is delegated to <see cref="ComparisonOps"/>.
/// </remarks>
/// <seealso cref="IConditionEvaluator"/>
/// <seealso cref="ComparisonOps"/>
public class SensorAgeEvaluator : IConditionEvaluator
{

    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Initialises a new <see cref="SensorAgeEvaluator"/>.
    /// </summary>
    /// <param name="timeProvider">Abstraction for the current UTC time, enabling deterministic unit tests.</param>
    public SensorAgeEvaluator(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    /// <inheritdoc/>
    public AlertConditionType ConditionType => AlertConditionType.SensorAge;

    /// <inheritdoc/>
    /// <param name="conditionParamsJson">JSON representation of a <see cref="SensorAgeCondition"/>.</param>
    /// <param name="context">Current sensor context including <see cref="SensorContext.LastSensorStartAt"/>.</param>
    /// <param name="ct">Cancellation token (unused; this evaluator performs no I/O).</param>
    public Task<bool> EvaluateAsync(string conditionParamsJson, SensorContext context, CancellationToken ct)
    {
        if (context.LastSensorStartAt is null)
            return Task.FromResult(false);

        var condition = JsonSerializer.Deserialize<SensorAgeCondition>(conditionParamsJson, EvaluatorJson.Options);
        if (condition is null)
            return Task.FromResult(false);

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var ageDays = (decimal)(now - context.LastSensorStartAt.Value).TotalDays;

        return Task.FromResult(ComparisonOps.Compare(ageDays, condition.Operator, condition.Value));
    }
}
