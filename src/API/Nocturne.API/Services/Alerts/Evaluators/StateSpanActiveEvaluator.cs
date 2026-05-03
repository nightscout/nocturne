using System.Text.Json;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;

namespace Nocturne.API.Services.Alerts.Evaluators;

/// <summary>
/// Evaluates a <see cref="StateSpanActiveCondition"/> against
/// <see cref="SensorContext.ActiveStateSpans"/> for any non-pump-mode StateSpan category
/// (Override, Sleep, Exercise, Profile, Illness, Travel, DataExclusion, TemporaryTarget,
/// PumpConnectivity).
/// </summary>
/// <remarks>
/// Pump-mode rules must use <see cref="PumpStateEvaluator"/> instead — both because pump-mode
/// has dedicated context plumbing and because the controller-level validator rejects
/// <see cref="StateSpanCategory.PumpMode"/> in this leaf. As a defense-in-depth, this evaluator
/// also short-circuits to false for the PumpMode category so a malformed payload that bypassed
/// validation (e.g. a hand-edited DB row) cannot accidentally read pump state through the
/// generic dictionary.
///
/// State filter semantics: a null <see cref="StateSpanActiveCondition.State"/> matches any
/// state of the category — the enricher loaded <c>(category, null)</c> for that exact pair,
/// so the lookup key matches whatever the enricher stored.
///
/// Legacy <see cref="AlertConditionType.OverrideActive"/> rules continue to use
/// <see cref="OverrideActiveEvaluator"/> unchanged for back-compat.
/// </remarks>
/// <seealso cref="IConditionEvaluator"/>
public sealed class StateSpanActiveEvaluator : IConditionEvaluator
{
    private readonly TimeProvider _timeProvider;

    /// <summary>Initialises a new <see cref="StateSpanActiveEvaluator"/>.</summary>
    public StateSpanActiveEvaluator(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    /// <inheritdoc/>
    public AlertConditionType ConditionType => AlertConditionType.StateSpanActive;

    /// <inheritdoc/>
    public Task<bool> EvaluateAsync(string conditionParamsJson, SensorContext context, CancellationToken ct)
    {
        var condition = JsonSerializer.Deserialize<StateSpanActiveCondition>(conditionParamsJson, EvaluatorJson.Options);
        if (condition is null)
            return Task.FromResult(false);

        // Defense in depth: pump_mode must be evaluated by PumpStateEvaluator.
        if (condition.Category == StateSpanCategory.PumpMode)
            return Task.FromResult(false);

        var key = (condition.Category, condition.State);
        var hasSnapshot = context.ActiveStateSpans.TryGetValue(key, out var snapshot);

        if (!condition.IsActive)
            return Task.FromResult(!hasSnapshot);

        if (!hasSnapshot || snapshot is null)
            return Task.FromResult(false);

        if (condition.ForMinutes is not { } forMinutes)
            return Task.FromResult(true);

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var elapsedMinutes = (now - snapshot.StartedAt).TotalMinutes;
        return Task.FromResult(elapsedMinutes >= forMinutes);
    }
}
