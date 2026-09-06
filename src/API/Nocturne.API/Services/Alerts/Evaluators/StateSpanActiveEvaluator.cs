using System.Text.Json;
using Microsoft.Extensions.Logging;
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
///
/// A payload whose <c>category</c> no longer maps to a <see cref="StateSpanCategory"/> member
/// (e.g. a stored <c>Sleep</c> rule left behind after that category was removed) fails to
/// deserialize. Rather than let the <see cref="JsonException"/> propagate — which would throw on
/// every evaluation cycle — the rule is skipped (evaluates to false) and a warning is logged.
/// The data migration converts such <c>Sleep</c> rules to the dedicated
/// <see cref="AlertConditionType.SleepSessionActive"/> condition; this guard covers any that
/// predate or bypass the migration.
/// </remarks>
/// <seealso cref="IConditionEvaluator"/>
public sealed class StateSpanActiveEvaluator : IConditionEvaluator
{
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<StateSpanActiveEvaluator> _logger;

    /// <summary>Initialises a new <see cref="StateSpanActiveEvaluator"/>.</summary>
    public StateSpanActiveEvaluator(TimeProvider timeProvider, ILogger<StateSpanActiveEvaluator> logger)
    {
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc/>
    public AlertConditionType ConditionType => AlertConditionType.StateSpanActive;

    /// <inheritdoc/>
    public Task<bool> EvaluateAsync(string conditionParamsJson, SensorContext context, CancellationToken ct)
    {
        StateSpanActiveCondition? condition;
        try
        {
            condition = JsonSerializer.Deserialize<StateSpanActiveCondition>(conditionParamsJson, EvaluatorJson.Options);
        }
        catch (JsonException ex)
        {
            // An unparseable/removed category (e.g. a legacy "Sleep" rule) must not throw every
            // cycle — skip the rule and warn so the stale payload is visible in logs.
            _logger.LogWarning(ex, "Skipping state-span-active rule with unparseable condition params");
            return Task.FromResult(false);
        }

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
