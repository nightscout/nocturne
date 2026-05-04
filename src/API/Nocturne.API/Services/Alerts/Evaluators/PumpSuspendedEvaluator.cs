using System.Text.Json;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;

namespace Nocturne.API.Services.Alerts.Evaluators;

/// <summary>
/// Evaluates a pump-suspension state condition against
/// <see cref="SensorContext.ActivePumpSuspension"/>.
/// </summary>
/// <remarks>
/// The condition's <see cref="PumpSuspendedCondition.IsActive"/> field selects which side of
/// the boolean state is being asserted: true matches when a suspension is active, false matches
/// when none is. <see cref="PumpSuspendedCondition.ForMinutes"/> is only meaningful when
/// <c>IsActive=true</c> — the inactive side has no anchor timestamp to measure from.
/// Cold-start guard uses <see cref="SensorContext.HasEverPumpSnapshot"/>: a tenant that has
/// never reported pump status is treated as no-data (false) rather than asserting either side.
/// </remarks>
/// <seealso cref="IConditionEvaluator"/>
public class PumpSuspendedEvaluator : IConditionEvaluator
{

    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Initialises a new <see cref="PumpSuspendedEvaluator"/>.
    /// </summary>
    /// <param name="timeProvider">Abstraction for the current UTC time, enabling deterministic unit tests.</param>
    public PumpSuspendedEvaluator(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    /// <inheritdoc/>
    public AlertConditionType ConditionType => AlertConditionType.PumpSuspended;

    /// <inheritdoc/>
    /// <param name="conditionParamsJson">JSON representation of a <see cref="PumpSuspendedCondition"/>.</param>
    /// <param name="context">Current sensor context including <see cref="SensorContext.ActivePumpSuspension"/>.</param>
    /// <param name="ct">Cancellation token (unused; this evaluator performs no I/O).</param>
    public Task<bool> EvaluateAsync(string conditionParamsJson, SensorContext context, CancellationToken ct)
    {
        if (!context.HasEverPumpSnapshot)
            return Task.FromResult(false);

        var condition = JsonSerializer.Deserialize<PumpSuspendedCondition>(conditionParamsJson, EvaluatorJson.Options);
        if (condition is null)
            return Task.FromResult(false);

        var isCurrentlySuspended = context.ActivePumpSuspension is not null;
        if (isCurrentlySuspended != condition.IsActive)
            return Task.FromResult(false);

        if (condition.ForMinutes is not { } forMinutes)
            return Task.FromResult(true);

        // ForMinutes only applies to the IsActive=true case (the suspension StateSpan provides
        // the StartedAt anchor). For IsActive=false there is no anchor; treat ForMinutes as a no-op.
        if (!condition.IsActive)
            return Task.FromResult(true);

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var elapsedMinutes = (now - context.ActivePumpSuspension!.StartedAt).TotalMinutes;
        return Task.FromResult(elapsedMinutes >= forMinutes);
    }
}
