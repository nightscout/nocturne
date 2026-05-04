using System.Text.Json;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;

namespace Nocturne.API.Services.Alerts.Evaluators;

/// <summary>
/// Evaluates loop enaction liveness — minutes since the latest enacted APS cycle —
/// against <see cref="SensorContext.LastApsEnactedAt"/>.
/// </summary>
/// <remarks>
/// Cold-start guard uses <see cref="SensorContext.HasEverApsCycled"/>: a tenant that has
/// never seen any APS cycle could not have seen an enacted one, so we share that flag rather
/// than tracking a separate "has ever enacted" bit. Open-loop users will have cycle history
/// without enaction history; for them, this condition is permanently true after the threshold,
/// which is by design — they should not enable the rule.
/// Operator dispatch is delegated to <see cref="ComparisonOps"/>.
/// </remarks>
/// <seealso cref="IConditionEvaluator"/>
/// <seealso cref="ComparisonOps"/>
public class LoopEnactionStaleEvaluator : IConditionEvaluator
{

    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Initialises a new <see cref="LoopEnactionStaleEvaluator"/>.
    /// </summary>
    /// <param name="timeProvider">Abstraction for the current UTC time, enabling deterministic unit tests.</param>
    public LoopEnactionStaleEvaluator(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    /// <inheritdoc/>
    public AlertConditionType ConditionType => AlertConditionType.LoopEnactionStale;

    /// <inheritdoc/>
    /// <param name="conditionParamsJson">JSON representation of a <see cref="LoopEnactionStaleCondition"/>.</param>
    /// <param name="context">Current sensor context including <see cref="SensorContext.LastApsEnactedAt"/>.</param>
    /// <param name="ct">Cancellation token (unused; this evaluator performs no I/O).</param>
    public Task<bool> EvaluateAsync(string conditionParamsJson, SensorContext context, CancellationToken ct)
    {
        if (!context.HasEverApsCycled)
            return Task.FromResult(false);

        var condition = JsonSerializer.Deserialize<LoopEnactionStaleCondition>(conditionParamsJson, EvaluatorJson.Options);
        if (condition is null)
            return Task.FromResult(false);

        if (context.LastApsEnactedAt is not { } enactedAt)
            return Task.FromResult(false);

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var minutesSince = (decimal)(now - enactedAt).TotalMinutes;

        return Task.FromResult(ComparisonOps.Compare(minutesSince, condition.Operator, condition.Minutes));
    }
}
