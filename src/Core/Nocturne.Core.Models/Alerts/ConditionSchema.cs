using System.Text.Json;
using System.Text.Json.Nodes;

namespace Nocturne.Core.Models.Alerts;

/// <summary>
/// The condition schema a stored tree names at its root (docs/alerts/engine-semantics.md §1.2):
/// <c>"schema_version": 2</c> on a rule body (the <c>condition_params</c> object), on an
/// auto-resolve tree's root node and on each smart-snooze condition. No node below a root carries
/// it, and the engine reads no root without it.
/// </summary>
public static class ConditionSchema
{
    public const string Property = "schema_version";

    /// <summary>The schema the engine reads.</summary>
    public const int Version = 2;

    /// <summary>
    /// Whether <paramref name="root"/> names <see cref="Version"/>: exactly the JSON integer, as the
    /// engine reads it, so <c>2.0</c> does not.
    /// </summary>
    public static bool Names(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty(Property, out var version)
        && version.ValueKind == JsonValueKind.Number
        && version.TryGetUInt64(out var number)
        && number == Version;

    /// <summary><paramref name="root"/> naming <see cref="Version"/> as its first property.</summary>
    public static JsonObject Stamp(JsonObject root)
    {
        root.Remove(Property);
        root.Insert(0, Property, Version);
        return root;
    }

    /// <summary>The JSON object <paramref name="rootJson"/> naming <see cref="Version"/>.</summary>
    /// <exception cref="JsonException"><paramref name="rootJson"/> is not a JSON object.</exception>
    public static string Stamp(string rootJson) =>
        Stamp(JsonNode.Parse(rootJson) as JsonObject ?? throw new JsonException("A condition tree's root is a JSON object."))
            .ToJsonString();

    /// <summary><paramref name="node"/> without a schema name, as a node below a root is written.</summary>
    public static JsonObject Unstamp(JsonObject node)
    {
        node.Remove(Property);
        return node;
    }
}
