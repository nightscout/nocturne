using System.Text.Json;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;

namespace Nocturne.API.Services.Alerts.Evaluators;

/// <summary>
/// Evaluates a <see cref="PumpStateCondition"/> against
/// <see cref="SensorContext.ActivePumpState"/>. Pump-mode StateSpans are mutually exclusive
/// (one mode active at a time), so the context exposes a single snapshot rather than a set.
/// </summary>
/// <remarks>
/// Mirrors the shape of the legacy <see cref="PumpSuspendedEvaluator"/> but generalised over
/// every <see cref="PumpModeState"/>. The IsActive=false branch returns true when the active
/// mode is anything other than the configured <see cref="PumpStateCondition.Mode"/> — including
/// when no mode-span is active at all. <see cref="PumpStateCondition.ForMinutes"/> only applies
/// to the IsActive=true side (no anchor exists for "not in mode X for N minutes").
///
/// Note: legacy <see cref="AlertConditionType.PumpSuspended"/> rules continue to use
/// <see cref="PumpSuspendedEvaluator"/> unchanged for back-compat.
/// </remarks>
/// <seealso cref="IConditionEvaluator"/>
public sealed class PumpStateEvaluator : IConditionEvaluator
{
    private readonly TimeProvider _timeProvider;

    /// <summary>Initialises a new <see cref="PumpStateEvaluator"/>.</summary>
    public PumpStateEvaluator(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    /// <inheritdoc/>
    public AlertConditionType ConditionType => AlertConditionType.PumpState;

    /// <inheritdoc/>
    public Task<bool> EvaluateAsync(string conditionParamsJson, SensorContext context, CancellationToken ct)
    {
        var condition = JsonSerializer.Deserialize<PumpStateCondition>(conditionParamsJson, EvaluatorJson.Options);
        if (condition is null)
            return Task.FromResult(false);

        var activeMode = context.ActivePumpState?.Mode;

        if (!condition.IsActive)
        {
            // "Not in mode X" — true whenever the active mode is anything other than X
            // (including when no mode is active at all).
            return Task.FromResult(activeMode != condition.Mode);
        }

        // IsActive=true: mode must match; ForMinutes anchors against the StateSpan start.
        if (activeMode != condition.Mode || context.ActivePumpState is null)
            return Task.FromResult(false);

        if (condition.ForMinutes is not { } forMinutes)
            return Task.FromResult(true);

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var elapsedMinutes = (now - context.ActivePumpState.StartedAt).TotalMinutes;
        return Task.FromResult(elapsedMinutes >= forMinutes);
    }
}
