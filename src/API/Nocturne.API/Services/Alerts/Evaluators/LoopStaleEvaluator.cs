using System.Text.Json;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;

namespace Nocturne.API.Services.Alerts.Evaluators;

/// <summary>
/// Evaluates loop liveness — minutes since the latest APS cycle (suggested or enacted) —
/// against <see cref="SensorContext.LastApsCycleAt"/>.
/// </summary>
/// <remarks>
/// Returns <see langword="false"/> when <see cref="SensorContext.HasEverApsCycled"/> is false
/// (cold-start null-suppression: a tenant that has never run a closed loop must not match a
/// "loop has stopped" condition just because there is no APS cycle history).
/// Operator dispatch is delegated to <see cref="ComparisonOps"/>.
/// </remarks>
/// <seealso cref="IConditionEvaluator"/>
/// <seealso cref="ComparisonOps"/>
public class LoopStaleEvaluator : IConditionEvaluator
{

    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Initialises a new <see cref="LoopStaleEvaluator"/>.
    /// </summary>
    /// <param name="timeProvider">Abstraction for the current UTC time, enabling deterministic unit tests.</param>
    public LoopStaleEvaluator(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    /// <inheritdoc/>
    public AlertConditionType ConditionType => AlertConditionType.LoopStale;

    /// <inheritdoc/>
    /// <param name="conditionParamsJson">JSON representation of a <see cref="LoopStaleCondition"/>.</param>
    /// <param name="context">Current sensor context including <see cref="SensorContext.LastApsCycleAt"/>.</param>
    /// <param name="ct">Cancellation token (unused; this evaluator performs no I/O).</param>
    public Task<bool> EvaluateAsync(string conditionParamsJson, SensorContext context, CancellationToken ct)
    {
        if (!context.HasEverApsCycled)
            return Task.FromResult(false);

        var condition = JsonSerializer.Deserialize<LoopStaleCondition>(conditionParamsJson, EvaluatorJson.Options);
        if (condition is null)
            return Task.FromResult(false);

        if (context.LastApsCycleAt is not { } cycleAt)
            return Task.FromResult(false);

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var minutesSince = (decimal)(now - cycleAt).TotalMinutes;

        return Task.FromResult(ComparisonOps.Compare(minutesSince, condition.Operator, condition.Minutes));
    }
}
