using System.Text.Json;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;

namespace Nocturne.API.Services.Alerts.Evaluators;

/// <summary>
/// Evaluates a generalised staleness condition by comparing the minutes elapsed since the
/// last CGM reading against a configured value using a relational operator.
/// </summary>
/// <remarks>
/// <para>When <see cref="SensorContext.LastReadingAt"/> is <see langword="null"/> the elapsed time
/// is treated as "infinity": operators that mean "elapsed greater than threshold"
/// (<c>&gt;</c>, <c>&gt;=</c>) return <see langword="true"/>; "elapsed less than threshold"
/// operators (<c>&lt;</c>, <c>&lt;=</c>) return <see langword="false"/>; <c>==</c> returns
/// <see langword="false"/> because infinity is never equal to a finite threshold.</para>
/// <para>Returns <see langword="false"/> when the tenant has no reading history at all
/// (both <see cref="SensorContext.LastReadingAt"/> and <see cref="SensorContext.LatestTimestamp"/>
/// are <see langword="null"/>) — a brand-new tenant should not page itself the moment
/// they configure a <c>&gt; 15 minutes</c> rule. This cold-start short-circuit takes
/// precedence over the infinity convention above.</para>
/// </remarks>
/// <seealso cref="IConditionEvaluator"/>
/// <seealso cref="ComparisonOps"/>
public class StalenessEvaluator : IConditionEvaluator
{

    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Initialises a new <see cref="StalenessEvaluator"/>.
    /// </summary>
    /// <param name="timeProvider">
    /// Abstraction for the current UTC time, enabling deterministic unit tests.
    /// </param>
    public StalenessEvaluator(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    /// <inheritdoc/>
    public AlertConditionType ConditionType => AlertConditionType.Staleness;

    /// <inheritdoc/>
    /// <param name="conditionParamsJson">JSON representation of a <see cref="StalenessCondition"/>.</param>
    /// <param name="context">Current sensor context including <see cref="SensorContext.LastReadingAt"/>.</param>
    /// <param name="ct">Cancellation token (unused; this evaluator performs no I/O).</param>
    public Task<bool> EvaluateAsync(string conditionParamsJson, SensorContext context, CancellationToken ct)
    {
        var condition = JsonSerializer.Deserialize<StalenessCondition>(conditionParamsJson, EvaluatorJson.Options);
        if (condition is null)
            return Task.FromResult(false);

        // Cold-start null-suppression: a tenant that has never received any reading at all
        // shouldn't fire a staleness alert — there's no history to be stale against. Both
        // LastReadingAt and LatestTimestamp null is the unambiguous "no data ever" signal.
        if (context.LastReadingAt is null && context.LatestTimestamp is null)
            return Task.FromResult(false);

        // No reading at all: elapsed time is effectively infinite. Short-circuit on
        // operator before doing decimal math, since "infinity > N" is always true,
        // "infinity < N" always false, and "infinity == N" always false.
        if (context.LastReadingAt is null)
        {
            var noReadingResult = condition.Operator switch
            {
                ">" => true,
                ">=" => true,
                _ => false
            };
            return Task.FromResult(noReadingResult);
        }

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var elapsedMinutes = (decimal)(now - context.LastReadingAt.Value).TotalMinutes;

        return Task.FromResult(ComparisonOps.Compare(elapsedMinutes, condition.Operator, condition.Value));
    }
}
