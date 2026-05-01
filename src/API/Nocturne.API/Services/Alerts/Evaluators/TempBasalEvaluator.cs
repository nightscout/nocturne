using System.Text.Json;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;

namespace Nocturne.API.Services.Alerts.Evaluators;

/// <summary>
/// Evaluates an active-temp-basal comparison against
/// <see cref="SensorContext.ActiveTempBasal"/>.
/// </summary>
/// <remarks>
/// No <c>HasEver*</c> cold-start guard: absence of an active temp basal is the legitimate
/// "no temp" state, and the condition only concerns active temps. Returns false when no temp
/// is active. <see cref="TempBasalCondition.Metric"/> selects the field:
/// <list type="bullet">
///   <item><see cref="TempBasalMetric.Rate"/>: compares <see cref="TempBasalSnapshot.Rate"/> in U/hr.</item>
///   <item><see cref="TempBasalMetric.PercentOfScheduled"/>: computes
///     <c>Rate / ScheduledRate * 100</c>; returns false when <c>ScheduledRate</c> is null or zero
///     (no source-of-truth basal to express the temp as a percentage of).</item>
/// </list>
/// Operator dispatch is delegated to <see cref="ComparisonOps"/>.
/// </remarks>
/// <seealso cref="IConditionEvaluator"/>
/// <seealso cref="ComparisonOps"/>
public class TempBasalEvaluator : IConditionEvaluator
{

    /// <inheritdoc/>
    public AlertConditionType ConditionType => AlertConditionType.TempBasal;

    /// <inheritdoc/>
    /// <param name="conditionParamsJson">JSON representation of a <see cref="TempBasalCondition"/>.</param>
    /// <param name="context">Current sensor context including <see cref="SensorContext.ActiveTempBasal"/>.</param>
    /// <param name="ct">Cancellation token (unused; this evaluator performs no I/O).</param>
    public Task<bool> EvaluateAsync(string conditionParamsJson, SensorContext context, CancellationToken ct)
    {
        if (context.ActiveTempBasal is not { } temp)
            return Task.FromResult(false);

        var condition = JsonSerializer.Deserialize<TempBasalCondition>(conditionParamsJson, EvaluatorJson.Options);
        if (condition is null)
            return Task.FromResult(false);

        var actual = condition.Metric switch
        {
            TempBasalMetric.Rate => (decimal?)temp.Rate,
            TempBasalMetric.PercentOfScheduled
                when temp.ScheduledRate is decimal scheduled && scheduled != 0m
                    => temp.Rate / scheduled * 100m,
            _ => null,
        };

        if (actual is null)
            return Task.FromResult(false);

        return Task.FromResult(ComparisonOps.Compare(actual.Value, condition.Operator, condition.Value));
    }
}
