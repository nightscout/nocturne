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
/// When <see cref="SensorContext.LastReadingAt"/> is <see langword="null"/> the elapsed time
/// is treated as "infinity": operators that mean "elapsed greater than threshold"
/// (<c>&gt;</c>, <c>&gt;=</c>) return <see langword="true"/>; "elapsed less than threshold"
/// operators (<c>&lt;</c>, <c>&lt;=</c>) return <see langword="false"/>; <c>==</c> returns
/// <see langword="false"/> because infinity is never equal to a finite threshold.
/// </remarks>
/// <seealso cref="IConditionEvaluator"/>
/// <seealso cref="ComparisonOps"/>
public class StalenessEvaluator : IConditionEvaluator
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true
    };

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
    public bool Evaluate(string conditionParamsJson, SensorContext context)
    {
        var condition = JsonSerializer.Deserialize<StalenessCondition>(conditionParamsJson, JsonOptions);
        if (condition is null)
            return false;

        // No reading at all: elapsed time is effectively infinite. Short-circuit on
        // operator before doing decimal math, since "infinity > N" is always true,
        // "infinity < N" always false, and "infinity == N" always false.
        if (context.LastReadingAt is null)
        {
            return condition.Operator switch
            {
                ">" => true,
                ">=" => true,
                _ => false
            };
        }

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var elapsedMinutes = (decimal)(now - context.LastReadingAt.Value).TotalMinutes;

        return ComparisonOps.Compare(elapsedMinutes, condition.Operator, condition.Value);
    }
}
