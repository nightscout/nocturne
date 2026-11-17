using System.Text.Json;
using Nocturne.API.Services.Alerts.Conditions;
using Nocturne.Core.Alerts.Native;
using Nocturne.Core.Models.Alerts;

namespace Nocturne.API.Services.Alerts;

/// <summary>
/// Derives a rule's <see cref="RuleScopeClass"/> for scoped Do Not Disturb (ADR 0004)
/// from its condition type + params, by delegating to the shared Rust engine's
/// <c>classify</c> (the crate owns the directional-leaf tree walk so the rule never
/// drifts from how it is evaluated). Computed on rule create/update and backfilled
/// once for pre-existing rules.
/// </summary>
public interface IRuleScopeClassifier
{
    /// <summary>
    /// Classifies a rule from its stored <c>condition_type</c> + <c>condition_params</c>.
    /// Never throws: params that are not JSON, or an engine failure, fall back to
    /// <see cref="RuleScopeClass.Undirected"/> (all-only), the safe default that never lets a
    /// scoped <c>lows</c>/<c>highs</c> mute wrongly silence a rule.
    /// </summary>
    RuleScopeClass Classify(AlertConditionType conditionType, string conditionParamsJson);
}

/// <inheritdoc />
public sealed class RuleScopeClassifier : IRuleScopeClassifier
{
    private static readonly JsonElement EmptyParams = JsonDocument.Parse("{}").RootElement.Clone();

    private readonly ILogger<RuleScopeClassifier> _logger;

    public RuleScopeClassifier(ILogger<RuleScopeClassifier> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public RuleScopeClass Classify(AlertConditionType conditionType, string conditionParamsJson)
    {
        try
        {
            var request = new RustClassifyRequest
            {
                ConditionType = AlertConditionTypeNames.ToWireString(conditionType),
                ConditionParams = ParseParams(conditionParamsJson),
            };

            var wire = RustAlertEngine.Classify(request);
            var scopeClass = RuleScopeClassNames.FromWireString(wire);
            if (scopeClass is null)
                return Fallback(conditionType, $"engine returned unknown scope_class '{wire}'");

            return scopeClass.Value;
        }
        catch (Exception ex) when (ex is RustAlertEngineException or JsonException)
        {
            return Fallback(conditionType, ex.Message, ex);
        }
    }

    /// <summary>
    /// Parses the stored params into a <see cref="JsonElement"/>. Empty/blank params map
    /// to <c>{}</c>; the crate's <c>classify</c> treats any payload-less rule as undirected,
    /// so this stays faithful without a special case.
    /// </summary>
    private static JsonElement ParseParams(string conditionParamsJson)
    {
        if (string.IsNullOrWhiteSpace(conditionParamsJson))
            return EmptyParams;

        using var doc = JsonDocument.Parse(conditionParamsJson);
        return doc.RootElement.Clone();
    }

    private RuleScopeClass Fallback(AlertConditionType conditionType, string reason, Exception? ex = null)
    {
        _logger.LogWarning(
            ex,
            "Scope-class classification failed for {ConditionType} rule ({Reason}); defaulting to Undirected (all-only)",
            conditionType,
            reason);
        return RuleScopeClass.Undirected;
    }
}
