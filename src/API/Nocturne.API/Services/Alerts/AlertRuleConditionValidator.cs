using System.Text.Json;
using System.Text.Json.Nodes;
using Nocturne.API.Services.Alerts.Evaluators;
using Nocturne.Core.Alerts.Native;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;

namespace Nocturne.API.Services.Alerts;

/// <summary>
/// Checks the condition trees a rule evaluates for everything a save rejects, through the shared
/// Rust engine's <c>validate</c> (docs/alerts/engine-semantics.md §1.4). Those trees are the body,
/// the auto-resolve tree when auto-resolve is on, and the smart-snooze conditions when smart
/// snooze is on. A <c>time_of_day</c> timezone no zone resolves from is reported as
/// <c>invalid_field</c> on <c>timezone</c>.
/// </summary>
public interface IAlertRuleConditionValidator
{
    /// <summary>
    /// The problems with the rule's trees; empty when there are none. Never throws. When the
    /// native engine is unavailable or fails, only the problems that fail evaluation are found
    /// (<see cref="ConditionTreeFaults"/>), plus a <c>PumpMode</c> <c>state_span_active</c> and
    /// timezones, and that is logged.
    /// </summary>
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
    /// rule editor sends back whatever it loaded. Without the native engine nothing is removed,
    /// as nothing reports <c>unknown_field</c>.
    /// </summary>
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
    private const string SnoozeScope = "snooze";

    private static readonly JsonElement JsonNull = JsonDocument.Parse("null").RootElement.Clone();

    private readonly ILogger<AlertRuleConditionValidator> _logger;
    private readonly Lazy<bool> _available;
    private int _unavailableLogged;

    public AlertRuleConditionValidator(ILogger<AlertRuleConditionValidator> logger)
        : this(logger, AlertsInterop.IsAvailable)
    {
    }

    internal AlertRuleConditionValidator(ILogger<AlertRuleConditionValidator> logger, Func<bool> isAvailable)
    {
        _logger = logger;
        _available = new Lazy<bool>(isAvailable);
    }

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
        var issues = NativeValidate(conditionType, conditionParamsJson, autoResolve, snooze, stored: null)?.Issues
                     ?? ManagedIssues(conditionType, conditionParamsJson, autoResolve, snooze);
        return [.. issues, .. TimeZoneIssues(conditionType, conditionParamsJson, autoResolve, snooze)];
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
        var response = NativeValidate(conditionType, conditionParamsJson, autoResolve, snooze, StoredRequest(stored));
        if (response is null)
        {
            return new ConditionUpdateCheck(
                [.. ManagedIssues(conditionType, conditionParamsJson, autoResolve, snooze),
                    .. TimeZoneIssues(conditionType, conditionParamsJson, autoResolve, snooze)],
                conditionParamsJson, autoResolveParamsJson, clientConfigurationJson, []);
        }

        var body = response.ConditionParams?.GetRawText() ?? conditionParamsJson;
        var autoResolveTree = response.AutoResolveParams?.GetRawText() ?? autoResolveParamsJson;
        var clientConfiguration = response.SnoozeConditions is { } conditions
            ? WithSnoozeConditions(clientConfigurationJson, conditions)
            : clientConfigurationJson;
        var timeZones = TimeZoneIssues(
            conditionType,
            body,
            autoResolveEnabled ? autoResolveTree : null,
            SmartSnoozeConfig.EvaluatedConditions(clientConfiguration));
        return new ConditionUpdateCheck(
            [.. response.Issues!, .. timeZones], body, autoResolveTree, clientConfiguration, response.Stripped!);
    }

    /// <summary>
    /// The native engine's verdict, or null when it is unavailable, fails, or a tree is not JSON,
    /// for the caller to fall back on <see cref="ManagedIssues"/>.
    /// </summary>
    private RustValidateResponse? NativeValidate(
        AlertConditionType conditionType,
        string conditionParamsJson,
        string? autoResolveJson,
        JsonElement? snooze,
        RustStoredConditions? stored)
    {
        if (!_available.Value)
        {
            if (Interlocked.Exchange(ref _unavailableLogged, 1) == 0)
            {
                _logger.LogWarning(
                    "nocturne_alerts native library unavailable; alert rule conditions are checked only for problems that fail evaluation");
            }
            return null;
        }

        if (!TryParse(conditionParamsJson, out var body) || !TryParse(autoResolveJson, out var autoResolve))
            return null;

        try
        {
            return RustAlertEngine.Validate(new RustValidateRequest
            {
                ConditionType = AlertConditionTypeNames.ToWireString(conditionType),
                ConditionParams = body ?? JsonNull,
                AutoResolveParams = autoResolve,
                SnoozeConditions = snooze,
                Stored = stored,
            });
        }
        catch (Exception ex) when (
            ex is RustAlertEngineException
                or JsonException
                or DllNotFoundException
                or EntryPointNotFoundException
                or BadImageFormatException)
        {
            _logger.LogWarning(ex,
                "Alert rule condition validation failed in the native engine; checking only for problems that fail evaluation");
            return null;
        }
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
    /// What makes the managed engine skip the rule on every tick, and a <c>state_span_active</c>
    /// on the <c>PumpMode</c> category, which never fires. The first is the shapes
    /// <see cref="ConditionTreeFaults"/> finds and JSON that does not read as the evaluators'
    /// models. All are coded and pathed as the Rust engine reports them.
    /// </summary>
    private static List<RustValidationIssue> ManagedIssues(
        AlertConditionType conditionType, string conditionParamsJson, string? autoResolveJson, JsonElement? snooze)
    {
        var issues = new List<RustValidationIssue>();
        var wire = AlertConditionTypeNames.ToWireString(conditionType);

        if (!TryParse(conditionParamsJson, out var body))
            issues.Add(Issue(ConditionScope, wire, "not_an_object"));
        else if (body is null or { ValueKind: JsonValueKind.Null })
            issues.Add(Issue(ConditionScope, wire, "payload_missing"));
        else if (!ReadNode(new JsonObject { ["type"] = wire, [wire] = JsonNode.Parse(body.Value.GetRawText()) }.ToJsonString(), out var bodyNode))
            issues.Add(Issue(ConditionScope, wire, "invalid_field"));
        else if (ConditionTreeFaults.InRule(conditionType, conditionParamsJson) is { } fault)
            issues.Add(Issue(ConditionScope, fault));
        else
            AddPumpModeIssues(issues, ConditionScope, bodyNode, wire);

        if (!TryParse(autoResolveJson, out var autoResolve))
            issues.Add(Issue(AutoResolveScope, AlertConditionTypeNames.AutoResolvePathRoot, "not_an_object"));
        else if (autoResolve is { ValueKind: not JsonValueKind.Null } tree)
            AddNodeIssues(issues, AutoResolveScope, AlertConditionTypeNames.AutoResolvePathRoot, tree.GetRawText());

        if (snooze is { ValueKind: JsonValueKind.Array } list && list.GetArrayLength() > 0)
        {
            var composite = new JsonObject
            {
                ["type"] = "composite",
                ["composite"] = new JsonObject
                {
                    ["operator"] = "and",
                    ["conditions"] = JsonNode.Parse(list.GetRawText()),
                },
            };
            AddNodeIssues(issues, SnoozeScope, AlertConditionTypeNames.SnoozePathRoot, composite.ToJsonString());
        }

        return issues;
    }

    private static void AddNodeIssues(List<RustValidationIssue> issues, string scope, string root, string nodeJson)
    {
        if (!ReadNode(nodeJson, out var node))
            issues.Add(Issue(scope, root, "invalid_field"));
        else if (ConditionTreeFaults.InNode(node, root) is { } fault)
            issues.Add(Issue(scope, fault));
        else
            AddPumpModeIssues(issues, scope, node, root);
    }

    /// <summary>
    /// Every <c>state_span_active</c> on <see cref="StateSpanCategory.PumpMode"/> in the tree, in
    /// pre-order: it evaluates false (<see cref="StateSpanActiveEvaluator"/>), so a rule holding
    /// one never fires on it; pump modes are <c>pump_state</c>'s.
    /// </summary>
    private static void AddPumpModeIssues(
        List<RustValidationIssue> issues, string scope, ConditionNode? node, string path)
    {
        if (node?.Type is null)
            return;
        switch (ConditionNodePayloads.Select(node))
        {
            case StateSpanActiveCondition { Category: StateSpanCategory.PumpMode }:
                issues.Add(new RustValidationIssue(scope, path, "pump_mode_category", "category"));
                break;
            case CompositeCondition { Conditions: { } children }:
                for (var i = 0; i < children.Count; i++)
                    AddPumpModeIssues(issues, scope, children[i], $"{path}[{i}].{children[i]?.Type}");
                break;
            case NotCondition { Child: { } child }:
                AddPumpModeIssues(issues, scope, child, $"{path}[0].{child.Type}");
                break;
            case SustainedCondition { Child: { } child }:
                AddPumpModeIssues(issues, scope, child, $"{path}[0].{child.Type}");
                break;
        }
    }

    private static bool ReadNode(string json, out ConditionNode? node)
    {
        try
        {
            node = JsonSerializer.Deserialize<ConditionNode>(json, EvaluatorJson.Options);
            return true;
        }
        catch (JsonException)
        {
            node = null;
            return false;
        }
    }

    private static RustValidationIssue Issue(string scope, string path, string reason) =>
        new(scope, path, reason, null);

    private static RustValidationIssue Issue(string scope, ConditionTreeFault fault) =>
        new(scope, fault.Path, fault.Reason, FaultField(fault.Reason));

    /// <summary>The field the Rust engine names for each <see cref="ConditionTreeFaults"/> reason.</summary>
    private static string? FaultField(string reason) => reason switch
    {
        "type_missing" => "type",
        "conditions_missing" => "conditions",
        "operator_missing" => "operator",
        "direction_missing" => "direction",
        "state_missing" => "state",
        _ => null,
    };

    private static IEnumerable<RustValidationIssue> TimeZoneIssues(
        AlertConditionType conditionType, string conditionParamsJson, string? autoResolveJson, JsonElement? snooze)
    {
        static RustValidationIssue ZoneIssue(string scope, ConditionTimeZones.UnresolvedZone zone) =>
            new(scope, zone.Path, "invalid_field", "timezone");

        foreach (var zone in ConditionTimeZones.UnresolvedInRule(conditionType, conditionParamsJson))
            yield return ZoneIssue(ConditionScope, zone);

        foreach (var zone in ConditionTimeZones.UnresolvedInNode(
                     autoResolveJson, AlertConditionTypeNames.AutoResolvePathRoot))
            yield return ZoneIssue(AutoResolveScope, zone);

        if (snooze is { } conditions)
        {
            foreach (var zone in ConditionTimeZones.UnresolvedInConditionList(
                         conditions, AlertConditionTypeNames.SnoozePathRoot))
                yield return ZoneIssue(SnoozeScope, zone);
        }
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
