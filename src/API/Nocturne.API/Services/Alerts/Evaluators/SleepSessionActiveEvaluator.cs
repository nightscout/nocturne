using System.Text.Json;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;

namespace Nocturne.API.Services.Alerts.Evaluators;

/// <summary>
/// Evaluates a <see cref="SleepSessionActiveCondition"/> against
/// <see cref="SensorContext.SleepSessionActive"/>. A sleep session is "active" when it has
/// <c>StartTime &lt;= now &lt;= EndTime</c> for the tenant; the enricher computes that signal
/// from the sleep_sessions tables via <see cref="Nocturne.Core.Contracts.Sleep.ISleepService"/>.
/// </summary>
/// <remarks>
/// <see cref="SleepSessionActiveCondition.IsActive"/> selects which side of the boolean is
/// asserted: <c>true</c> matches while a session is active, <c>false</c> matches when none is.
/// </remarks>
/// <seealso cref="IConditionEvaluator"/>
public sealed class SleepSessionActiveEvaluator : IConditionEvaluator
{
    /// <inheritdoc/>
    public AlertConditionType ConditionType => AlertConditionType.SleepSessionActive;

    /// <inheritdoc/>
    public Task<bool> EvaluateAsync(string conditionParamsJson, SensorContext context, CancellationToken ct)
    {
        var condition = JsonSerializer.Deserialize<SleepSessionActiveCondition>(conditionParamsJson, EvaluatorJson.Options);
        if (condition is null)
            return Task.FromResult(false);

        return Task.FromResult(context.SleepSessionActive == condition.IsActive);
    }
}
