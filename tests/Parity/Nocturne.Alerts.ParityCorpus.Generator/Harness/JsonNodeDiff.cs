using System.Globalization;
using System.Text.Json.Nodes;

namespace Nocturne.Alerts.ParityCorpus.Generator.Harness;

/// <summary>
/// Deep semantic comparison of two JSON trees with contextual diff messages.
/// Numbers compare numerically (decimal) and timestamp strings compare as
/// instants, so formatting differences between engines (e.g. fractional-second
/// rendering) don't produce false mismatches.
/// </summary>
public static class JsonNodeDiff
{
    /// <param name="actualName">Label for the left side in diff messages (e.g. "csharp", "ffi").</param>
    public static void Compare(JsonNode? actual, JsonNode? expected, string path, string actualName, List<string> failures)
    {
        if (actual is null && expected is null)
            return;
        if (actual is null || expected is null)
        {
            failures.Add($"{path}: {actualName} = {Render(actual)}, expected = {Render(expected)}");
            return;
        }

        switch (actual, expected)
        {
            case (JsonObject a, JsonObject e):
                foreach (var key in a.Select(p => p.Key).Union(e.Select(p => p.Key)).OrderBy(k => k, StringComparer.Ordinal))
                {
                    var childPath = $"{path}.{key}";
                    var inActual = a.TryGetPropertyValue(key, out var av);
                    var inExpected = e.TryGetPropertyValue(key, out var ev);
                    if (inActual && inExpected)
                        Compare(av, ev, childPath, actualName, failures);
                    else if (inActual)
                        failures.Add($"{childPath}: unexpected field in {actualName} (value = {Render(av)})");
                    else
                        failures.Add($"{childPath}: missing field in {actualName} (expected = {Render(ev)})");
                }
                break;

            case (JsonArray a, JsonArray e):
                if (a.Count != e.Count)
                {
                    failures.Add($"{path}: array length mismatch ({actualName} = {a.Count}, expected = {e.Count})");
                    return;
                }
                for (var i = 0; i < a.Count; i++)
                    Compare(a[i], e[i], $"{path}[{i}]", actualName, failures);
                break;

            case (JsonValue a, JsonValue e):
                if (!ValuesEqual(a, e))
                    failures.Add($"{path}: {actualName} = {Render(actual)}, expected = {Render(expected)}");
                break;

            default:
                failures.Add($"{path}: kind mismatch ({actualName} = {Render(actual)}, expected = {Render(expected)})");
                break;
        }
    }

    private static bool ValuesEqual(JsonValue a, JsonValue e)
    {
        if (a.TryGetValue<string>(out var sa) && e.TryGetValue<string>(out var se))
        {
            if (TryParseInstant(sa, out var da) && TryParseInstant(se, out var de))
                return da == de;
            return string.Equals(sa, se, StringComparison.Ordinal);
        }

        if (a.TryGetValue<decimal>(out var na) && e.TryGetValue<decimal>(out var ne))
            return na == ne;

        if (a.TryGetValue<bool>(out var ba) && e.TryGetValue<bool>(out var be))
            return ba == be;

        return a.ToJsonString() == e.ToJsonString();
    }

    private static bool TryParseInstant(string value, out DateTimeOffset instant)
    {
        // Cheap shape check so plain strings (states, paths) skip the parse.
        if (value.Length < 16 || value[4] != '-' || !value.Contains('T'))
        {
            instant = default;
            return false;
        }
        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out instant);
    }

    private static string Render(JsonNode? node) => node?.ToJsonString() ?? "(absent)";
}
