using System.Text.Json;
using System.Text.Json.Nodes;
using Nocturne.API.Services.Alerts.Evaluators;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;

namespace Nocturne.API.Services.Alerts;

/// <summary>
/// The per-rule <c>time_of_day.timezone</c> ids in stored condition JSON: rewriting Windows ids
/// to IANA, and finding ids no zone resolves from.
/// </summary>
/// <remarks>
/// The managed engine resolves a Windows id. The Rust engine reads the IANA database and does not
/// (docs/alerts/engine-semantics.md §4), so there a Windows id fails the window closed. Rules are
/// rewritten on save, and every request to the Rust engine is rewritten too, for rules stored
/// before that. The JSON is edited in place rather than round-tripped through
/// <see cref="ConditionNode"/>, which would drop fields the model does not declare.
/// </remarks>
internal static class ConditionTimeZones
{
    /// <summary>A problem found in one tree: the condition path of the offending node.</summary>
    public readonly record struct UnresolvedZone(string Path);

    /// <summary>
    /// <paramref name="conditionParamsJson"/>, the payload-only body of a rule of kind
    /// <paramref name="type"/>, with Windows timezone ids rewritten; the same string when none were.
    /// </summary>
    public static string CanonicaliseRule(AlertConditionType type, string conditionParamsJson) =>
        Rewrite(conditionParamsJson, payload => VisitRule(type, payload, (tod, _) => CanonicaliseZone(tod)),
            mentionsTimeOfDay: type == AlertConditionType.TimeOfDay);

    /// <summary>A full condition node (<c>{"type": …}</c>) with Windows timezone ids rewritten.</summary>
    public static string CanonicaliseNode(string nodeJson) =>
        Rewrite(nodeJson, node => VisitNode(node, string.Empty, (tod, _) => CanonicaliseZone(tod)));

    /// <inheritdoc cref="CanonicaliseRule(AlertConditionType, string)"/>
    public static JsonElement CanonicaliseRule(AlertConditionType type, JsonElement conditionParams) =>
        RewriteElement(conditionParams, json => CanonicaliseRule(type, json));

    /// <inheritdoc cref="CanonicaliseNode(string)"/>
    public static JsonElement CanonicaliseNode(JsonElement node) => RewriteElement(node, CanonicaliseNode);

    /// <summary>
    /// <c>client_configuration</c> with Windows timezone ids in its smart-snooze conditions
    /// rewritten; the same string when none were.
    /// </summary>
    public static string CanonicaliseClientConfiguration(string clientConfigurationJson) =>
        Rewrite(clientConfigurationJson, config =>
        {
            if (SnoozeConditions(config) is { } conditions)
                foreach (var condition in conditions)
                    VisitNode(condition, string.Empty, (tod, _) => CanonicaliseZone(tod));
        });

    /// <summary>The <c>time_of_day</c> nodes in a rule body whose timezone resolves to no zone.</summary>
    public static IReadOnlyList<UnresolvedZone> UnresolvedInRule(AlertConditionType type, string? conditionParamsJson)
    {
        var found = new List<UnresolvedZone>();
        if (TryParse(conditionParamsJson) is { } payload)
            VisitRule(type, payload, (tod, path) => CollectUnresolved(tod, path, found));
        return found;
    }

    /// <summary>
    /// The <c>time_of_day</c> nodes in a full condition node whose timezone resolves to no zone, with
    /// the root segment named <paramref name="root"/>.
    /// </summary>
    public static IReadOnlyList<UnresolvedZone> UnresolvedInNode(string? nodeJson, string root)
    {
        var found = new List<UnresolvedZone>();
        if (TryParse(nodeJson) is { } node)
            VisitNode(node, root, (tod, path) => CollectUnresolved(tod, path, found));
        return found;
    }

    /// <summary>
    /// The <c>time_of_day</c> nodes in a smart-snooze condition list whose timezone resolves to no
    /// zone, pathed as the list's implicit <c>and</c> group under <paramref name="root"/>.
    /// </summary>
    public static IReadOnlyList<UnresolvedZone> UnresolvedInConditionList(JsonElement conditions, string root)
    {
        var found = new List<UnresolvedZone>();
        if (JsonNode.Parse(conditions.GetRawText()) is JsonArray list)
            VisitChildren(list, root, (tod, path) => CollectUnresolved(tod, path, found));
        return found;
    }

    private static void CanonicaliseZone(JsonObject timeOfDay)
    {
        if (Property(timeOfDay, "timezone") is not { } key
            || timeOfDay[key] is not JsonValue value
            || !value.TryGetValue<string>(out var id)
            || string.IsNullOrEmpty(id))
            return;

        var iana = TimeZoneHelper.ToIanaIdIfWindows(id);
        if (!string.Equals(iana, id, StringComparison.Ordinal))
            timeOfDay[key] = iana;
    }

    private static void CollectUnresolved(JsonObject timeOfDay, string path, List<UnresolvedZone> found)
    {
        if (Property(timeOfDay, "timezone") is { } key
            && timeOfDay[key] is JsonValue value
            && value.TryGetValue<string>(out var id)
            && !string.IsNullOrEmpty(id)
            && !TimeZoneHelper.TryGetTimeZoneInfoFromId(id, out _))
            found.Add(new UnresolvedZone(path));
    }

    private static void VisitRule(AlertConditionType type, JsonNode payload, Action<JsonObject, string> onTimeOfDay)
    {
        var wire = AlertConditionTypeNames.ToWireString(type);
        var wrapper = new JsonObject { ["type"] = wire, [wire] = payload };
        VisitNode(wrapper, wire, onTimeOfDay);
        wrapper.Remove(wire);
    }

    /// <summary>
    /// Walks a condition node in <see cref="ConditionPath"/> order. <paramref name="path"/> is the
    /// node's own path; empty means its verbatim type.
    /// </summary>
    private static void VisitNode(JsonNode? node, string path, Action<JsonObject, string> onTimeOfDay)
    {
        if (node is not JsonObject obj
            || Property(obj, "type") is not { } typeKey
            || obj[typeKey] is not JsonValue typeValue
            || !typeValue.TryGetValue<string>(out var type))
            return;

        if (path.Length == 0)
            path = type;

        var kind = AlertConditionTypeNames.Resolve(type);
        if (kind is null
            || Property(obj, AlertConditionTypeNames.ToWireString(kind.Value)) is not { } payloadKey
            || obj[payloadKey] is not JsonObject payload)
            return;

        switch (kind.Value)
        {
            case AlertConditionType.TimeOfDay:
                onTimeOfDay(payload, path);
                break;
            case AlertConditionType.Composite:
                if (Property(payload, "conditions") is { } conditionsKey && payload[conditionsKey] is JsonArray children)
                    VisitChildren(children, path, onTimeOfDay);
                break;
            case AlertConditionType.Not or AlertConditionType.Sustained:
                if (Property(payload, "child") is { } childKey && payload[childKey] is JsonObject child)
                    VisitNode(child, $"{path}[0].{ChildType(child)}", onTimeOfDay);
                break;
        }
    }

    private static void VisitChildren(JsonArray children, string path, Action<JsonObject, string> onTimeOfDay)
    {
        for (var i = 0; i < children.Count; i++)
        {
            if (children[i] is JsonObject child)
                VisitNode(child, $"{path}[{i}].{ChildType(child)}", onTimeOfDay);
        }
    }

    private static string ChildType(JsonObject child) =>
        Property(child, "type") is { } key && child[key] is JsonValue v && v.TryGetValue<string>(out var type)
            ? type
            : string.Empty;

    /// <summary>The key matching <paramref name="name"/> case-insensitively, as the evaluators read it.</summary>
    private static string? Property(JsonObject obj, string name)
    {
        foreach (var (key, _) in obj)
        {
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
                return key;
        }
        return null;
    }

    private static JsonArray? SnoozeConditions(JsonNode config) =>
        config is JsonObject root
        && Property(root, "snooze") is { } snoozeKey
        && root[snoozeKey] is JsonObject snooze
        && Property(snooze, "conditions") is { } conditionsKey
            ? snooze[conditionsKey] as JsonArray
            : null;

    /// <summary>
    /// Edits JSON that mentions <c>time_of_day</c> at all, or that is known to be a
    /// <c>time_of_day</c> payload. Unparseable JSON is returned as is, for the evaluators to reject.
    /// </summary>
    private static string Rewrite(string json, Action<JsonNode> edit, bool mentionsTimeOfDay = false)
    {
        if (!mentionsTimeOfDay && json.IndexOf("time_of_day", StringComparison.OrdinalIgnoreCase) < 0)
            return json;
        if (TryParse(json) is not { } root)
            return json;

        var before = root.ToJsonString();
        edit(root);
        var after = root.ToJsonString();
        return string.Equals(before, after, StringComparison.Ordinal) ? json : after;
    }

    private static JsonElement RewriteElement(JsonElement element, Func<string, string> rewrite)
    {
        var json = element.GetRawText();
        var rewritten = rewrite(json);
        if (ReferenceEquals(rewritten, json))
            return element;
        using var doc = JsonDocument.Parse(rewritten);
        return doc.RootElement.Clone();
    }

    private static JsonNode? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            return JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
