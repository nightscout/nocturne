using System.Text.Json;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;

namespace Nocturne.API.Services.Alerts.Evaluators;

/// <summary>
/// Evaluates a <see cref="TrackerAgeCondition"/> against
/// <see cref="SensorContext.ActiveTrackers"/>: minutes since the active tracker
/// instance's reference timestamp (start for Duration trackers, scheduled time for Event
/// trackers — resolved by the enricher). Elapsed time is negative before a scheduled event.
/// </summary>
/// <remarks>
/// Returns <see langword="false"/> when no active instance exists for the referenced
/// definition — a tracker that isn't running has no age, deliberately opposite to the
/// time_since_last_* cold-start infinity. Operator dispatch is delegated to
/// <see cref="ComparisonOps"/>.
/// </remarks>
/// <seealso cref="IConditionEvaluator"/>
/// <seealso cref="ComparisonOps"/>
public sealed class TrackerAgeEvaluator : IConditionEvaluator
{
    private readonly TimeProvider _timeProvider;

    /// <summary>Initialises a new <see cref="TrackerAgeEvaluator"/>.</summary>
    /// <param name="timeProvider">Abstraction for the current UTC time, enabling deterministic unit tests.</param>
    public TrackerAgeEvaluator(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    /// <inheritdoc/>
    public AlertConditionType ConditionType => AlertConditionType.TrackerAge;

    /// <inheritdoc/>
    /// <param name="conditionParamsJson">JSON representation of a <see cref="TrackerAgeCondition"/>.</param>
    /// <param name="context">Current sensor context including <see cref="SensorContext.ActiveTrackers"/>.</param>
    /// <param name="ct">Cancellation token (unused; this evaluator performs no I/O).</param>
    public Task<bool> EvaluateAsync(string conditionParamsJson, SensorContext context, CancellationToken ct)
    {
        var condition = JsonSerializer.Deserialize<TrackerAgeCondition>(conditionParamsJson, EvaluatorJson.Options);
        if (condition is null)
            return Task.FromResult(false);

        if (!context.ActiveTrackers.TryGetValue(condition.TrackerDefinitionId, out var referenceAt))
            return Task.FromResult(false);

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var minutesSince = (decimal)(now - referenceAt).TotalMinutes;

        return Task.FromResult(ComparisonOps.Compare(minutesSince, condition.Operator, condition.Minutes));
    }
}
