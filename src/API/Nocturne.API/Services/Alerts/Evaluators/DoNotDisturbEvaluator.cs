using System.Text.Json;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;

namespace Nocturne.API.Services.Alerts.Evaluators;

/// <summary>
/// Evaluates a Do Not Disturb state condition against
/// <see cref="SensorContext.ActiveDoNotDisturb"/>.
/// </summary>
/// <remarks>
/// DND has two activation paths (manual toggle with optional auto-expire; scheduled window),
/// but both collapse into the same projection on the context — evaluators don't distinguish.
/// <see cref="DoNotDisturbCondition.IsActive"/> selects which side of the boolean is asserted:
/// <c>true</c> matches when DND is on, <c>false</c> matches when off.
/// <see cref="DoNotDisturbCondition.ForMinutes"/> only applies to the <c>IsActive=true</c> case
/// (the snapshot's <see cref="DoNotDisturbSnapshot.StartedAt"/> provides the anchor).
/// </remarks>
/// <seealso cref="IConditionEvaluator"/>
public class DoNotDisturbEvaluator : IConditionEvaluator
{
    private readonly TimeProvider _timeProvider;

    public DoNotDisturbEvaluator(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    /// <inheritdoc/>
    public AlertConditionType ConditionType => AlertConditionType.DoNotDisturb;

    /// <inheritdoc/>
    public Task<bool> EvaluateAsync(string conditionParamsJson, SensorContext context, CancellationToken ct)
    {
        var condition = JsonSerializer.Deserialize<DoNotDisturbCondition>(conditionParamsJson, EvaluatorJson.Options);
        if (condition is null)
            return Task.FromResult(false);

        var isCurrentlyActive = context.ActiveDoNotDisturb is not null;
        if (isCurrentlyActive != condition.IsActive)
            return Task.FromResult(false);

        if (condition.ForMinutes is not { } forMinutes)
            return Task.FromResult(true);

        // ForMinutes only applies to the IsActive=true case (the snapshot provides the anchor).
        // For IsActive=false there is no anchor; treat ForMinutes as a no-op.
        if (!condition.IsActive)
            return Task.FromResult(true);

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var elapsedMinutes = (now - context.ActiveDoNotDisturb!.StartedAt).TotalMinutes;
        return Task.FromResult(elapsedMinutes >= forMinutes);
    }
}
