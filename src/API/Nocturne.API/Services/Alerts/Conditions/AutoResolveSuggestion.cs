using System.Text.Json;
using System.Text.Json.Nodes;

namespace Nocturne.API.Services.Alerts.Conditions;

/// <summary>
/// The condition that closes an alert at once: the opposite of the alert's own condition tree, for
/// the conditions that have one. The alert wizard stores it, the rule editor offers it, and the
/// setup hub stores it for its starter rules.
/// <para>
/// A node is the envelope a rule stores: <c>{"type":"threshold","threshold":{...}}</c>. An "and"
/// of conditions closes when any one is no longer true, so its opposites join with "or", and the
/// reverse. A wait (<c>sustained</c>) is dropped: the alert closes when the condition lifts, not
/// after it has been lifted for the wait. A condition with no opposite is left out, and a tree
/// with none has no suggestion.
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
            return For(JsonNode.Parse(condition))?.ToJsonString();
        }
        catch (JsonException)
        {
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
                var opposites = conditions.Select(For).OfType<JsonObject>().ToList();
                if (opposites.Count <= 1)
                    return opposites.FirstOrDefault();
                var joined = TextOf(payload["operator"]) == "and" ? "or" : "and";
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
                if (payload is null)
                    return null;
                var opposite = (JsonObject)payload.DeepClone();
                opposite["direction"] = TextOf(payload["direction"]) == "below" ? "above" : "below";
                return Node(type, opposite);
            }

            // A rate has no comparison to turn round. A flat trend is the nearest condition.
            case "rate_of_change":
                return payload is null ? null : Flat();

            case "trend":
                return payload is null || TextOf(payload["bucket"]) == "flat" ? null : Flat();

            // A forecast is true when any of its points matches, so one with the comparison turned
            // round is true together with it. Only its negation is its opposite.
            case "predicted":
                return payload is null ? null : Node("not", new JsonObject { ["child"] = node.DeepClone() });

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
            case "tracker_age":
            {
                if (payload is null || OppositeOperator(TextOf(payload["operator"])) is not { } turned)
                    return null;
                var opposite = (JsonObject)payload.DeepClone();
                opposite["operator"] = turned;
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

            case "alert_state":
            {
                if (payload is null)
                    return null;
                var opposite = (JsonObject)payload.DeepClone();
                opposite["state"] = TextOf(payload["state"]) == "firing" ? "acknowledged" : "firing";
                return Node(type, opposite);
            }

            // The loop conditions take only "longer than", a time window needs a negation to turn
            // round, and a temp basal or a lost signal has more than one reading of "opposite".
            default:
                return null;
        }
    }

    private static JsonObject Node(string type, JsonObject payload) =>
        new() { ["type"] = type, [type] = payload };

    private static JsonObject Flat() => Node("trend", new JsonObject { ["bucket"] = "flat" });

    private static string? TextOf(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static string? OppositeOperator(string? op) => op switch
    {
        "<" => ">=",
        "<=" => ">",
        ">" => "<=",
        ">=" => "<",
        _ => null,
    };
}
