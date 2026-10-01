using System.Text.Json;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;

namespace Nocturne.API.Services.Alerts.Evaluators;

/// <summary>
/// True once no CGM reading has arrived for <see cref="SignalLossCondition.TimeoutMinutes"/>:
/// <c>now - LastReadingAt &gt;= timeout</c>, compared as exact durations.
/// </summary>
/// <remarks>
/// Null handling follows <see cref="StalenessEvaluator"/>: no reading history at all is false,
/// and <see cref="SensorContext.LastReadingAt"/> null with a <see cref="SensorContext.LatestTimestamp"/>
/// is infinitely stale. A timeout of zero or less is false rather than permanently firing.
/// The condition only turns true between readings, so it relies on
/// <see cref="AlertSweepService"/> evaluating the rule on the wall clock.
/// </remarks>
public class SignalLossEvaluator(TimeProvider timeProvider) : IConditionEvaluator
{
    /// <inheritdoc/>
    public AlertConditionType ConditionType => AlertConditionType.SignalLoss;

    /// <inheritdoc/>
    public Task<bool> EvaluateAsync(string conditionParamsJson, SensorContext context, CancellationToken ct)
    {
        var condition = JsonSerializer.Deserialize<SignalLossCondition>(conditionParamsJson, EvaluatorJson.Options);
        if (condition is null || condition.TimeoutMinutes <= 0)
            return Task.FromResult(false);

        if (context.LastReadingAt is null)
            return Task.FromResult(context.LatestTimestamp is not null);

        var elapsed = timeProvider.GetUtcNow().UtcDateTime - context.LastReadingAt.Value;
        return Task.FromResult(elapsed >= TimeSpan.FromMinutes(condition.TimeoutMinutes));
    }
}
