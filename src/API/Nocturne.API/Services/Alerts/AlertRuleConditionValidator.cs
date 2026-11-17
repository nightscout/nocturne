using System.Text.Json;
using System.Text.Json.Nodes;
using Nocturne.API.Services.Alerts.Conditions;
using Nocturne.Core.Alerts.Native;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;

namespace Nocturne.API.Services.Alerts;

/// <summary>
/// Checks the condition trees a rule evaluates for every problem that keeps one from being
/// evaluated, through the shared Rust engine's <c>validate</c> (docs/alerts/engine-semantics.md
/// §1.4). Those trees are the body, the auto-resolve tree when auto-resolve is on, and the
/// smart-snooze conditions when smart snooze is on. A smart-snooze <c>conditions</c> value that
/// is neither a list nor null is reported as <c>invalid_field</c> on <c>conditions</c>, since the
/// sweep cannot read it (<see cref="SmartSnoozeConfig.ConditionsUnreadable"/>).
/// </summary>
public interface IAlertRuleConditionValidator
{
    /// <summary>
    /// The problems with the rule's trees; empty when there are none. A tree that is not JSON is
    /// reported as <c>not_an_object</c> at its root.
    /// </summary>
    /// <exception cref="RustAlertEngineException">The engine failed or rejected the request.</exception>
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
    private const string ConditionScope = "condition";
    private const string AutoResolveScope = "auto_resolve";
    private const string SnoozeScope = "snooze";

    private static readonly JsonElement JsonNull = JsonDocument.Parse("null").RootElement.Clone();

    /// <inheritdoc />
    public IReadOnlyList<RustValidationIssue> Validate(
        AlertConditionType conditionType,
        string conditionParamsJson,
        bool autoResolveEnabled,
        string? autoResolveParamsJson,
        string? clientConfigurationJson)
    {
        var autoResolve = autoResolveEnabled ? autoResolveParamsJson : null;
        var snooze = SmartSnoozeConfig.Parse(clientConfigurationJson);
        if (JsonIssues(conditionType, conditionParamsJson, autoResolve) is { Count: > 0 } unreadable)
            return unreadable;
        var issues = NativeValidate(
            conditionType, conditionParamsJson, autoResolve, snooze.SmartSnooze ? snooze.Conditions : null).Issues!;
        return snooze.SmartSnooze && snooze.ConditionsUnreadable
            ? [.. issues, new(SnoozeScope, AlertConditionTypeNames.SnoozePathRoot, "invalid_field", "conditions")]
            : issues;
    }

    /// <summary>
    /// A body or auto-resolve tree that is not JSON, as <c>not_an_object</c> at its root. The
    /// engine reads only JSON, so such a tree goes no further.
    /// </summary>
    private static List<RustValidationIssue> JsonIssues(
        AlertConditionType conditionType, string conditionParamsJson, string? autoResolveJson)
    {
        var issues = new List<RustValidationIssue>();
        if (!TryParse(conditionParamsJson, out _))
            issues.Add(new(ConditionScope, AlertConditionTypeNames.ToWireString(conditionType), "not_an_object", null));
        if (!TryParse(autoResolveJson, out _))
            issues.Add(new(AutoResolveScope, AlertConditionTypeNames.AutoResolvePathRoot, "not_an_object", null));
        return issues;
    }

    /// <summary>The engine's verdict on trees that are JSON.</summary>
    /// <exception cref="RustAlertEngineException">The engine failed or rejected the request.</exception>
    private static RustValidateResponse NativeValidate(
        AlertConditionType conditionType,
        string conditionParamsJson,
        string? autoResolveJson,
        JsonElement? snooze)
    {
        TryParse(conditionParamsJson, out var body);
        TryParse(autoResolveJson, out var autoResolve);
        return RustAlertEngine.Validate(new RustValidateRequest
        {
            ConditionType = AlertConditionTypeNames.ToWireString(conditionType),
            ConditionParams = body ?? JsonNull,
            AutoResolveParams = autoResolve,
            SnoozeConditions = snooze,
        });
    }

    /// <summary>
    /// Whether <paramref name="json"/> is readable JSON; <paramref name="value"/> is null for a
    /// blank string (a missing tree).
    /// </summary>
    private static bool TryParse(string? json, out JsonElement? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(json))
            return true;
        try
        {
            using var doc = JsonDocument.Parse(json);
            value = doc.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
