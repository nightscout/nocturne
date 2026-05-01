using System.Text.Json;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;

namespace Nocturne.API.Services.Alerts.Evaluators;

/// <summary>
/// Evaluates an infusion-site age comparison. The configured value is in hours; the actual
/// age is derived from <c>now - </c><see cref="SensorContext.LastSiteChangeAt"/>.
/// </summary>
/// <remarks>
/// Returns <see langword="false"/> when <see cref="SensorContext.LastSiteChangeAt"/> is null
/// (no site change recorded). Operator dispatch is delegated to <see cref="ComparisonOps"/>.
/// </remarks>
/// <seealso cref="IConditionEvaluator"/>
/// <seealso cref="ComparisonOps"/>
public class SiteAgeEvaluator : IConditionEvaluator
{

    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Initialises a new <see cref="SiteAgeEvaluator"/>.
    /// </summary>
    /// <param name="timeProvider">Abstraction for the current UTC time, enabling deterministic unit tests.</param>
    public SiteAgeEvaluator(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    /// <inheritdoc/>
    public AlertConditionType ConditionType => AlertConditionType.SiteAge;

    /// <inheritdoc/>
    /// <param name="conditionParamsJson">JSON representation of a <see cref="SiteAgeCondition"/>.</param>
    /// <param name="context">Current sensor context including <see cref="SensorContext.LastSiteChangeAt"/>.</param>
    /// <param name="ct">Cancellation token (unused; this evaluator performs no I/O).</param>
    public Task<bool> EvaluateAsync(string conditionParamsJson, SensorContext context, CancellationToken ct)
    {
        if (context.LastSiteChangeAt is null)
            return Task.FromResult(false);

        var condition = JsonSerializer.Deserialize<SiteAgeCondition>(conditionParamsJson, EvaluatorJson.Options);
        if (condition is null)
            return Task.FromResult(false);

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var ageHours = (decimal)(now - context.LastSiteChangeAt.Value).TotalHours;

        return Task.FromResult(ComparisonOps.Compare(ageHours, condition.Operator, condition.Value));
    }
}
