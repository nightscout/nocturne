using System.Text.Json;
using System.Text.Json.Nodes;
using Nocturne.API.Services.Alerts.Conditions;
using Nocturne.Core.Alerts.Native;
using Nocturne.Core.Models;
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

    /// <summary>
    /// <see cref="Validate"/> for an edit of <paramref name="stored"/>. An <c>unknown_field</c>
    /// property the stored rule already has at the same scope, path and object is removed from
    /// the trees to save instead of reported (docs/alerts/engine-semantics.md §1.4), since the
    /// rule editor sends back whatever it loaded.
    /// </summary>
    /// <exception cref="RustAlertEngineException">The engine failed or rejected the request.</exception>
    ConditionUpdateCheck ValidateUpdate(
        AlertConditionType conditionType,
        string conditionParamsJson,
        bool autoResolveEnabled,
        string? autoResolveParamsJson,
        string? clientConfigurationJson,
        StoredConditionTrees stored);
}

/// <summary>A stored rule's condition columns, as <see cref="IAlertRuleConditionValidator.ValidateUpdate"/> reads them.</summary>
public sealed record StoredConditionTrees(
    AlertConditionType ConditionType,
    string ConditionParams,
    string? AutoResolveParams,
    string? ClientConfiguration);

/// <summary>
/// The outcome of <see cref="IAlertRuleConditionValidator.ValidateUpdate"/>: the issues left, and
/// the trees to store, which are the request's less <see cref="Stripped"/>.
/// </summary>
public sealed record ConditionUpdateCheck(
    IReadOnlyList<RustValidationIssue> Issues,
    string ConditionParams,
    string? AutoResolveParams,
    string? ClientConfiguration,
    IReadOnlyList<RustStrippedField> Stripped);

/// <inheritdoc />
public sealed class AlertRuleConditionValidator : IAlertRuleConditionValidator
{
    private const string ConditionScope = "condition";
    private const string AutoResolveScope = "auto_resolve";

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
        var snooze = SmartSnoozeConfig.EvaluatedConditions(clientConfigurationJson);
        if (JsonIssues(conditionType, conditionParamsJson, autoResolve) is { Count: > 0 } unreadable)
            return unreadable;
        return NativeValidate(conditionType, conditionParamsJson, autoResolve, snooze, stored: null).Issues!;
    }

    /// <inheritdoc />
    public ConditionUpdateCheck ValidateUpdate(
        AlertConditionType conditionType,
        string conditionParamsJson,
        bool autoResolveEnabled,
        string? autoResolveParamsJson,
        string? clientConfigurationJson,
        StoredConditionTrees stored)
    {
        ArgumentNullException.ThrowIfNull(stored);

        var autoResolve = autoResolveEnabled ? autoResolveParamsJson : null;
        var snooze = SmartSnoozeConfig.EvaluatedConditions(clientConfigurationJson);
        if (JsonIssues(conditionType, conditionParamsJson, autoResolve) is { Count: > 0 } unreadable)
            return new ConditionUpdateCheck(unreadable, conditionParamsJson, autoResolveParamsJson, clientConfigurationJson, []);
        var response = NativeValidate(conditionType, conditionParamsJson, autoResolve, snooze, StoredRequest(stored));

        var body = response.ConditionParams?.GetRawText() ?? conditionParamsJson;
        var autoResolveTree = response.AutoResolveParams?.GetRawText() ?? autoResolveParamsJson;
        var clientConfiguration = response.SnoozeConditions is { } conditions
            ? WithSnoozeConditions(clientConfigurationJson, conditions)
            : clientConfigurationJson;
        return new ConditionUpdateCheck(
            response.Issues!, body, autoResolveTree, clientConfiguration, response.Stripped!);
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
        JsonElement? snooze,
        RustStoredConditions? stored)
    {
        TryParse(conditionParamsJson, out var body);
        TryParse(autoResolveJson, out var autoResolve);
        return RustAlertEngine.Validate(new RustValidateRequest
        {
            ConditionType = AlertConditionTypeNames.ToWireString(conditionType),
            ConditionParams = body ?? JsonNull,
            AutoResolveParams = autoResolve,
            SnoozeConditions = snooze,
            Stored = stored,
        });
    }

    /// <summary>Every tree <paramref name="stored"/> holds that reads as JSON, evaluated or not.</summary>
    private static RustStoredConditions StoredRequest(StoredConditionTrees stored) => new()
    {
        ConditionType = AlertConditionTypeNames.ToWireString(stored.ConditionType),
        ConditionParams = TryParse(stored.ConditionParams, out var body) && body is { } b ? b : JsonNull,
        AutoResolveParams = TryParse(stored.AutoResolveParams, out var autoResolve) ? autoResolve : null,
        SnoozeConditions = SmartSnoozeConfig.StoredConditions(stored.ClientConfiguration),
    };

    /// <summary><paramref name="clientConfiguration"/> with its snooze conditions replaced.</summary>
    private static string? WithSnoozeConditions(string? clientConfiguration, JsonElement conditions)
    {
        var root = JsonNode.Parse(clientConfiguration ?? "null");
        if (SmartSnoozeConfig.ConditionsNode(root) is not { } list)
            return clientConfiguration;
        list.ReplaceWith(JsonNode.Parse(conditions.GetRawText()));
        return root!.ToJsonString();
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
