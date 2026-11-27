using System.Text.Json;
using System.Text.Json.Nodes;
using Nocturne.Core.Models.Alerts;

namespace Nocturne.API.Services.Alerts.Conditions;

/// <summary>
/// The condition that closes an alert at once: the opposite of the alert's own condition tree, for
/// the conditions that have one. The alert wizard stores it, the rule editor offers it, and the
/// setup hub stores it for its starter rules.
/// <para>
/// A node is the envelope a rule stores: <c>{"type":"threshold","threshold":{...}}</c>, naming its
/// <see cref="ConditionSchema"/> at the root only. An "and"
/// of conditions closes when any one is no longer true, so its opposites join with "or", and the
/// reverse. A wait (<c>sustained</c>) is dropped: the alert closes when the condition lifts, not
/// after it has been lifted for the wait. A condition with no opposite is left out of an "and",
/// whose other opposites still close it. An "or" with one has no opposite at all: the alert can
/// be true through that condition alone, with every other opposite true beside it.
/// </para>
/// <para>
/// An opposite must never be true together with its condition: the engine closes the alert on the
/// evaluation that finds the resolve tree true, which would be the one that starts it.
/// </para>
/// </summary>
internal static class AutoResolveSuggestion
{
    /// <summary>The opposite of <paramref name="condition"/> as JSON, or null when it has none.</summary>
    public static string? For(string? condition)
    {
        if (string.IsNullOrWhiteSpace(condition))
            return null;
        try
        {
            var root = JsonNode.Parse(condition);
            var opposite = For(root is JsonObject node ? ConditionSchema.Unstamp(node) : root);
            if (opposite is null)
                return null;
            DropClearMargins(opposite);
            return ConditionSchema.Stamp(opposite).ToJsonString();
        }
        catch (JsonException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            // A name given twice in one object, which the node model refuses when it is read.
            return null;
        }
    }

    public static JsonObject? For(JsonNode? condition)
    {
        if (condition is not JsonObject node || TextOf(node["type"]) is not { } type)
            return null;
        var payload = node[type] as JsonObject;

        switch (type)
        {
            case "composite":
            {
                if (payload?["conditions"] is not JsonArray conditions)
                    return null;
                var all = TextOf(payload["operator"]) == "and";
                var opposites = conditions.Select(For).OfType<JsonObject>().ToList();
                if (!all && opposites.Count < conditions.Count)
                    return null;
                if (opposites.Count <= 1)
                    return opposites.FirstOrDefault();
                var joined = all ? "or" : "and";
                return Node("composite", new JsonObject
                {
                    ["operator"] = joined,
                    ["conditions"] = new JsonArray([.. opposites]),
                });
            }

            case "not":
                return payload?["child"] is JsonObject negated ? (JsonObject)negated.DeepClone() : null;

            case "sustained":
                return For(payload?["child"]);

            case "threshold":
            {
                if (payload is null || OppositeThresholdOperator(TextOf(payload["operator"])) is not { } turned)
                    return null;
                var opposite = (JsonObject)payload.DeepClone();
                opposite["operator"] = turned;
                return Node(type, opposite);
            }

            // The trend is in one bucket at a time, so another bucket is never true with this one.
            case "trend":
                return payload is null || TextOf(payload["bucket"]) == "flat" ? null : Flat();

            // Only the negation is the opposite of these. A forecast is true when any of its points
            // matches, so one with the comparison turned round is true together with it.
            case "rate_of_change":
            case "predicted":
                return payload is null ? null : Negated(node);

            case "staleness":
            case "iob":
            case "cob":
            case "reservoir":
            case "site_age":
            case "sensor_age":
            case "pump_battery":
            case "uploader_battery":
            case "transmitter_battery":
            case "sensitivity_ratio":
            case "eventual_glucose":
            case "carbs_required":
            case "tracker_age":
            case "glucose_change":
            case "glucose_extreme":
            {
                if (payload is null || OppositeOperator(TextOf(payload["operator"])) is not { } turned)
                    return null;
                var opposite = (JsonObject)payload.DeepClone();
                opposite["operator"] = turned;
                return Node(type, opposite);
            }

            // The tally is turned round, not the comparison each reading meets.
            case "glucose_count":
            {
                var tally = payload?["count"] is JsonObject ? "count" : "percent";
                if (payload?[tally] is not JsonObject comparison
                    || OppositeOperator(TextOf(comparison["operator"])) is not { } turned)
                    return null;
                var opposite = (JsonObject)payload.DeepClone();
                opposite[tally]!["operator"] = turned;
                return Node(type, opposite);
            }

            case "pump_suspended":
            case "override_active":
            case "do_not_disturb":
            {
                if (payload?["is_active"] is not JsonValue active || !active.TryGetValue<bool>(out var isActive))
                    return null;
                return Node(type, new JsonObject { ["is_active"] = !isActive });
            }

            // An acknowledged alert is still one that started, so those two states are true
            // together. Only "not acknowledged" has another state as its opposite.
            case "alert_state":
            {
                if (payload is null)
                    return null;
                if (TextOf(payload["state"]) != "unacknowledged")
                    return Negated(node);
                var opposite = (JsonObject)payload.DeepClone();
                opposite["state"] = "acknowledged";
                return Node(type, opposite);
            }

            // The loop conditions take only "longer than", a time window needs a negation to turn
            // round, and a temp basal or a lost signal has more than one reading of "opposite".
            default:
                return null;
        }
    }

    /// <summary>
    /// An auto-resolve tree reads every leaf against its plain limit
    /// (docs/alerts/engine-semantics.md §3, clear margin), so a margin copied into one would be
    /// stored without effect.
    /// </summary>
    private static void DropClearMargins(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                obj.Remove("clear_margin");
                foreach (var (_, child) in obj)
                    DropClearMargins(child);
                break;
            case JsonArray array:
                foreach (var child in array)
                    DropClearMargins(child);
                break;
        }
    }

    private static JsonObject Node(string type, JsonObject payload) =>
        new() { ["type"] = type, [type] = payload };

    private static JsonObject Flat() => Node("trend", new JsonObject { ["bucket"] = "flat" });

    private static JsonObject Negated(JsonObject node) =>
        Node("not", new JsonObject { ["child"] = node.DeepClone() });

    private static string? TextOf(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    /// <summary>
    /// A strict limit turns to the strict other side, so the two leave the limit itself to neither;
    /// an inclusive one turns as <see cref="OppositeOperator"/> does.
    /// </summary>
    private static string? OppositeThresholdOperator(string? op) => op switch
    {
        "<" => ">",
        ">" => "<",
        _ => OppositeOperator(op),
    };

    private static string? OppositeOperator(string? op) => op switch
    {
        "<" => ">=",
        "<=" => ">",
        ">" => "<=",
        ">=" => "<",
        _ => null,
    };
}
