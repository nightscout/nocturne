using System.Text.Json;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;

namespace Nocturne.API.Services.Alerts.Evaluators;

/// <summary>
/// Evaluates a cross-alert state condition by inspecting
/// <see cref="SensorContext.ActiveAlerts"/> for the referenced alert.
/// </summary>
/// <remarks>
/// State semantics:
/// <list type="bullet">
///   <item><c>firing</c>: snapshot exists and <see cref="ActiveAlertSnapshot.State"/> equals "firing".</item>
///   <item><c>unacknowledged</c>: snapshot exists, state is "firing", and <see cref="ActiveAlertSnapshot.AcknowledgedAt"/> is null.</item>
///   <item><c>acknowledged</c>: snapshot exists and <see cref="ActiveAlertSnapshot.AcknowledgedAt"/> is non-null.</item>
/// </list>
/// When <see cref="AlertStateCondition.ForMinutes"/> is set the elapsed-since-relevant-event
/// must be at least that many minutes:
/// <see cref="ActiveAlertSnapshot.TriggeredAt"/> for "firing"/"unacknowledged",
/// <see cref="ActiveAlertSnapshot.AcknowledgedAt"/> for "acknowledged".
/// </remarks>
/// <seealso cref="IConditionEvaluator"/>
public class AlertStateEvaluator : IConditionEvaluator
{

    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Initialises a new <see cref="AlertStateEvaluator"/>.
    /// </summary>
    /// <param name="timeProvider">Abstraction for the current UTC time, enabling deterministic unit tests.</param>
    public AlertStateEvaluator(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    /// <inheritdoc/>
    public AlertConditionType ConditionType => AlertConditionType.AlertState;

    /// <inheritdoc/>
    /// <param name="conditionParamsJson">JSON representation of an <see cref="AlertStateCondition"/>.</param>
    /// <param name="context">Current sensor context including <see cref="SensorContext.ActiveAlerts"/>.</param>
    /// <param name="ct">Cancellation token (unused; this evaluator performs no I/O).</param>
    public Task<bool> EvaluateAsync(string conditionParamsJson, SensorContext context, CancellationToken ct)
    {
        var condition = JsonSerializer.Deserialize<AlertStateCondition>(conditionParamsJson, EvaluatorJson.Options);
        if (condition is null)
            return Task.FromResult(false);

        if (!context.ActiveAlerts.TryGetValue(condition.AlertId, out var snapshot))
            return Task.FromResult(false);

        var stateMatches = condition.State.ToLowerInvariant() switch
        {
            "firing" => string.Equals(snapshot.State, "firing", StringComparison.OrdinalIgnoreCase),
            "unacknowledged" =>
                string.Equals(snapshot.State, "firing", StringComparison.OrdinalIgnoreCase)
                && snapshot.AcknowledgedAt is null,
            "acknowledged" => snapshot.AcknowledgedAt is not null,
            _ => false
        };

        if (!stateMatches)
            return Task.FromResult(false);

        if (condition.ForMinutes is null)
            return Task.FromResult(true);

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var anchor = condition.State.ToLowerInvariant() == "acknowledged"
            ? snapshot.AcknowledgedAt!.Value
            : snapshot.TriggeredAt;
        var elapsedMinutes = (now - anchor).TotalMinutes;

        return Task.FromResult(elapsedMinutes >= condition.ForMinutes.Value);
    }
}
