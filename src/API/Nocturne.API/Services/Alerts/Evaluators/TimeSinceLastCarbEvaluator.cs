using System.Text.Json;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;

namespace Nocturne.API.Services.Alerts.Evaluators;

/// <summary>
/// Evaluates a <see cref="TimeSinceLastCarbCondition"/> against
/// <see cref="SensorContext.LastCarbAt"/>. Tenants with no recorded carb-bearing treatment
/// are treated as <c>+∞</c> minutes elapsed — predicates like "no carbs in 30 min" therefore
/// fire on cold start.
/// </summary>
/// <seealso cref="IConditionEvaluator"/>
public sealed class TimeSinceLastCarbEvaluator : IConditionEvaluator
{
    private readonly TimeProvider _timeProvider;

    /// <summary>Initialises a new <see cref="TimeSinceLastCarbEvaluator"/>.</summary>
    public TimeSinceLastCarbEvaluator(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    /// <inheritdoc/>
    public AlertConditionType ConditionType => AlertConditionType.TimeSinceLastCarb;

    /// <inheritdoc/>
    public Task<bool> EvaluateAsync(string conditionParamsJson, SensorContext context, CancellationToken ct)
    {
        var condition = JsonSerializer.Deserialize<TimeSinceLastCarbCondition>(conditionParamsJson, EvaluatorJson.Options);
        if (condition is null)
            return Task.FromResult(false);

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var elapsed = TimeSinceComparator.Apply(now, context.LastCarbAt, condition.Operator, condition.Minutes);
        return Task.FromResult(elapsed);
    }
}
