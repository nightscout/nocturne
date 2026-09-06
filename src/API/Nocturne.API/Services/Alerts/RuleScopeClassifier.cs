using System.Text.Json;
using Nocturne.API.Services.Alerts.Evaluators;
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
    /// Never throws: an unavailable native engine or unintelligible response falls back
    /// to <see cref="RuleScopeClass.Undirected"/> (all-only), the safe default that never
    /// lets a scoped <c>lows</c>/<c>highs</c> mute wrongly silence a rule.
    /// </summary>
    RuleScopeClass Classify(AlertConditionType conditionType, string conditionParamsJson);

    /// <summary>
    /// Whether the native engine backing <see cref="Classify"/> can be loaded. False means
    /// every call returns the <see cref="RuleScopeClass.Undirected"/> fallback, so scoped
    /// <c>lows</c>/<c>highs</c> mutes cannot narrow-match any rule — callers that would
    /// otherwise do bulk classification should skip the work and say so once.
    /// </summary>
    bool IsAvailable { get; }
}

/// <inheritdoc />
public sealed class RuleScopeClassifier : IRuleScopeClassifier
{
    private static readonly JsonElement EmptyParams = JsonDocument.Parse("{}").RootElement.Clone();

    private readonly ILogger<RuleScopeClassifier> _logger;
    private readonly Lazy<bool> _available;

    public RuleScopeClassifier(ILogger<RuleScopeClassifier> logger)
    {
        _logger = logger;
        // Probed once and cached: the answer cannot change over a process lifetime, and
        // the alternative is one failed P/Invoke per rule on every classify.
        _available = new Lazy<bool>(AlertsInterop.IsAvailable);
    }

    /// <inheritdoc />
    public bool IsAvailable => _available.Value;

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
        catch (Exception ex) when (
            ex is RustAlertEngineException
                or JsonException
                or DllNotFoundException
                or EntryPointNotFoundException
                // A wrong-architecture cdylib (an arm64 .so on the amd64 host) throws this
                // rather than DllNotFoundException. Without it, classification faults out of
                // the alert-rule create/update path as a 500 instead of degrading.
                or BadImageFormatException)
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
