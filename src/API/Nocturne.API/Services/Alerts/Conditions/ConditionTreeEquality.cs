using System.Text.Json;

namespace Nocturne.API.Services.Alerts.Conditions;

/// <summary>
/// Whether two stored condition trees are the same tree as both engines read them. Formatting and
/// key order do not matter; anything else does.
/// </summary>
/// <remarks>
/// Property names match exactly, as the engine reads them (docs/alerts/engine-semantics.md §1.1);
/// a repeated name binds its last occurrence. A number compares by its written token, so <c>1</c>
/// and <c>1.0</c> differ. Both
/// engines read an integer field only from an integral token. The store keeps the token as
/// written, so the two can mean different things. Identical text is always the same tree. Past
/// that, doubt reads as changed: a tree that is not JSON compares as text.
/// <para>
/// A rule edit clears the re-arm hold on a change, and <see cref="AlertRuleConditions.HeldBy"/>
/// drops a tracker decision on one. A false change there costs the in-flight decision, so a tree
/// must always equal its own stored text, or its rule would never alert.
/// </para>
/// </remarks>
internal static class ConditionTreeEquality
{
    /// <summary>Blank is no tree.</summary>
    public static bool Same(string? stored, string? requested)
    {
        if (string.IsNullOrWhiteSpace(stored) || string.IsNullOrWhiteSpace(requested))
            return string.IsNullOrWhiteSpace(stored) && string.IsNullOrWhiteSpace(requested);
        if (string.Equals(stored, requested, StringComparison.Ordinal))
            return true;
        try
        {
            using var a = JsonDocument.Parse(stored);
            using var b = JsonDocument.Parse(requested);
            return Same(a.RootElement, b.RootElement);
        }
        catch (JsonException)
        {
            return string.Equals(stored, requested, StringComparison.Ordinal);
        }
    }

    private static bool Same(JsonElement a, JsonElement b)
    {
        if (a.ValueKind != b.ValueKind)
            return false;
        switch (a.ValueKind)
        {
            case JsonValueKind.Object:
                var left = Properties(a);
                var right = Properties(b);
                return left.Count == right.Count
                       && left.All(p => right.TryGetValue(p.Key, out var other) && Same(p.Value, other));
            case JsonValueKind.Array:
                return a.GetArrayLength() == b.GetArrayLength()
                       && a.EnumerateArray().Zip(b.EnumerateArray()).All(pair => Same(pair.First, pair.Second));
            case JsonValueKind.String:
                return a.GetString() == b.GetString();
            case JsonValueKind.Number:
                return a.GetRawText() == b.GetRawText();
            default:
                return true;
        }
    }

    /// <summary>The object's properties by name as the engine binds them.</summary>
    private static Dictionary<string, JsonElement> Properties(JsonElement obj)
    {
        var properties = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var p in obj.EnumerateObject())
            properties[p.Name] = p.Value;
        return properties;
    }
}
