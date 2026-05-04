using System.Text.Json;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;

namespace Nocturne.API.Services.Alerts.Evaluators;

/// <summary>
/// Evaluates a <see cref="TimeSinceLastBolusCondition"/> against
/// <see cref="SensorContext.LastBolusAt"/>. Same cold-start semantics as
/// <see cref="TimeSinceLastCarbEvaluator"/>: a tenant with no recorded insulin-bearing
/// treatment is treated as <c>+∞</c> minutes elapsed.
/// </summary>
/// <seealso cref="IConditionEvaluator"/>
public sealed class TimeSinceLastBolusEvaluator : IConditionEvaluator
{
    private readonly TimeProvider _timeProvider;

    /// <summary>Initialises a new <see cref="TimeSinceLastBolusEvaluator"/>.</summary>
    public TimeSinceLastBolusEvaluator(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    /// <inheritdoc/>
    public AlertConditionType ConditionType => AlertConditionType.TimeSinceLastBolus;

    /// <inheritdoc/>
    public Task<bool> EvaluateAsync(string conditionParamsJson, SensorContext context, CancellationToken ct)
    {
        var condition = JsonSerializer.Deserialize<TimeSinceLastBolusCondition>(conditionParamsJson, EvaluatorJson.Options);
        if (condition is null)
            return Task.FromResult(false);

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var elapsed = TimeSinceComparator.Apply(now, context.LastBolusAt, condition.Operator, condition.Minutes);
        return Task.FromResult(elapsed);
    }
}
