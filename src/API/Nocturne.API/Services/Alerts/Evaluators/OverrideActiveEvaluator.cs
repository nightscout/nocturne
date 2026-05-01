using System.Text.Json;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;

namespace Nocturne.API.Services.Alerts.Evaluators;

/// <summary>
/// Evaluates an override-active state condition against
/// <see cref="SensorContext.ActiveOverride"/>.
/// </summary>
/// <remarks>
/// No <c>HasEver*</c> cold-start guard: absence of an active override is the legitimate
/// "no override" state, not a missing-data state. <see cref="OverrideActiveCondition.IsActive"/>
/// selects which side of the boolean is asserted; <see cref="OverrideActiveCondition.ForMinutes"/>
/// is only meaningful when <c>IsActive=true</c> (the inactive side has no anchor).
/// </remarks>
/// <seealso cref="IConditionEvaluator"/>
public class OverrideActiveEvaluator : IConditionEvaluator
{

    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Initialises a new <see cref="OverrideActiveEvaluator"/>.
    /// </summary>
    /// <param name="timeProvider">Abstraction for the current UTC time, enabling deterministic unit tests.</param>
    public OverrideActiveEvaluator(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    /// <inheritdoc/>
    public AlertConditionType ConditionType => AlertConditionType.OverrideActive;

    /// <inheritdoc/>
    /// <param name="conditionParamsJson">JSON representation of an <see cref="OverrideActiveCondition"/>.</param>
    /// <param name="context">Current sensor context including <see cref="SensorContext.ActiveOverride"/>.</param>
    /// <param name="ct">Cancellation token (unused; this evaluator performs no I/O).</param>
    public Task<bool> EvaluateAsync(string conditionParamsJson, SensorContext context, CancellationToken ct)
    {
        var condition = JsonSerializer.Deserialize<OverrideActiveCondition>(conditionParamsJson, EvaluatorJson.Options);
        if (condition is null)
            return Task.FromResult(false);

        var isCurrentlyActive = context.ActiveOverride is not null;
        if (isCurrentlyActive != condition.IsActive)
            return Task.FromResult(false);

        if (condition.ForMinutes is not { } forMinutes)
            return Task.FromResult(true);

        if (!condition.IsActive)
            return Task.FromResult(true);

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var elapsedMinutes = (now - context.ActiveOverride!.StartedAt).TotalMinutes;
        return Task.FromResult(elapsedMinutes >= forMinutes);
    }
}
