using System.Text.Json;
using Nocturne.API.Services.Alerts.Evaluators;
using Nocturne.Core.Alerts.Native;
using Nocturne.Core.Models.Alerts;

namespace Nocturne.API.Services.Alerts;

/// <summary>
/// Checks the condition trees a rule evaluates for everything a save rejects, through the shared
/// Rust engine's <c>validate</c> (docs/alerts/engine-semantics.md §1.4). Those trees are the body,
/// the auto-resolve tree when auto-resolve is on, and the smart-snooze conditions when smart
/// snooze is on.
/// </summary>
public interface IAlertRuleConditionValidator
{
    /// <summary>Whether the native engine backing <see cref="Validate"/> can be loaded.</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// The problems with the rule's trees; empty when there are none. Never throws: when the
    /// native engine is unavailable or fails, the rule is not checked, which is logged.
    /// </summary>
    IReadOnlyList<RustValidationIssue> Validate(
        AlertConditionType conditionType,
        string conditionParamsJson,
        bool autoResolveEnabled,
        string? autoResolveParamsJson,
        string? clientConfigurationJson);
}

/// <inheritdoc />
public sealed class AlertRuleConditionValidator : IAlertRuleConditionValidator
{
    private static readonly JsonElement JsonNull = JsonDocument.Parse("null").RootElement.Clone();

    private readonly ILogger<AlertRuleConditionValidator> _logger;
    private readonly Lazy<bool> _available = new(AlertsInterop.IsAvailable);

    public AlertRuleConditionValidator(ILogger<AlertRuleConditionValidator> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public bool IsAvailable => _available.Value;

    /// <inheritdoc />
    public IReadOnlyList<RustValidationIssue> Validate(
        AlertConditionType conditionType,
        string conditionParamsJson,
        bool autoResolveEnabled,
        string? autoResolveParamsJson,
        string? clientConfigurationJson)
    {
        if (!IsAvailable)
        {
            _logger.LogWarning(
                "nocturne_alerts native library unavailable; alert rule conditions were not validated");
            return [];
        }

        try
        {
            return RustAlertEngine.Validate(new RustValidateRequest
            {
                ConditionType = AlertConditionTypeNames.ToWireString(conditionType),
                ConditionParams = Parse(conditionParamsJson) ?? JsonNull,
                AutoResolveParams = autoResolveEnabled ? Parse(autoResolveParamsJson) : null,
                SnoozeConditions = SmartSnoozeConditions(clientConfigurationJson),
            });
        }
        catch (Exception ex) when (
            ex is RustAlertEngineException
                or JsonException
                or DllNotFoundException
                or EntryPointNotFoundException
                or BadImageFormatException)
        {
            _logger.LogWarning(ex, "Alert rule condition validation failed; the rule was not validated");
            return [];
        }
    }

    /// <summary>A JSON value, or null for a blank string (a missing tree).</summary>
    private static JsonElement? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    /// <summary>
    /// <c>snooze.conditions</c> when smart snooze is on and they are a list, the only case the
    /// sweep evaluates them (<see cref="SmartSnoozeConfig"/>).
    /// </summary>
    private static JsonElement? SmartSnoozeConditions(string? clientConfigurationJson)
    {
        if (Parse(clientConfigurationJson) is not { ValueKind: JsonValueKind.Object } config
            || !config.TryGetProperty("snooze", out var snooze)
            || snooze.ValueKind != JsonValueKind.Object
            || !snooze.TryGetProperty("smartSnooze", out var smart)
            || smart.ValueKind != JsonValueKind.True
            || !snooze.TryGetProperty("conditions", out var conditions)
            || conditions.ValueKind != JsonValueKind.Array)
        {
            return null;
        }
        return conditions;
    }
}
