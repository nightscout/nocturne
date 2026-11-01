using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace Nocturne.API.Helpers;

/// <summary>
/// JSON merge-patch. <see cref="ApplyInPlace{T}"/> overlays top-level properties onto a record:
/// each property the patch names replaces the record's value whole, and nested objects are not
/// merged. <see cref="Merge"/> is RFC 7386.
/// </summary>
internal static class JsonMergePatch
{
    /// <summary>
    /// RFC 7386: a property the patch names replaces the target's, a <c>null</c> deletes it, a
    /// property it leaves out is kept, and a nested object merges recursively. Anything other
    /// than an object replaces the target whole.
    /// </summary>
    /// <param name="target">Edited in place when it is an object.</param>
    /// <param name="patch">The patch to merge over <paramref name="target"/>.</param>
    /// <returns>The merged document.</returns>
    public static JsonNode? Merge(JsonNode? target, JsonElement patch)
    {
        if (patch.ValueKind != JsonValueKind.Object)
            return JsonNode.Parse(patch.GetRawText());

        var result = target as JsonObject ?? new JsonObject();
        foreach (var prop in patch.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.Null)
                result.Remove(prop.Name);
            else if (prop.Value.ValueKind == JsonValueKind.Object && result[prop.Name] is JsonObject child)
                Merge(child, prop.Value);
            else
                result[prop.Name] = Merge(null, prop.Value);
        }

        return result;
    }

    /// <summary>
    /// Refuses a patch that names, at any depth, a key that is not the JSON name of a property of
    /// the type it lands on. The merge and the typed read both match names exactly, so a key that
    /// differs only in case would merge as a second property and then be dropped on the read.
    /// Objects typed as a dictionary take any key, and a value typed as <see cref="object"/> or a
    /// JSON node is not checked.
    /// </summary>
    /// <param name="type">The type <paramref name="patch"/> is read as.</param>
    /// <param name="patch">The patch to check.</param>
    /// <param name="options">The options the merged document is read with.</param>
    /// <exception cref="ArgumentException">A key in the patch is not a property name.</exception>
    public static void EnsureNamesOf(Type type, JsonElement patch, JsonSerializerOptions options)
    {
        options.MakeReadOnly(populateMissingResolver: true);
        EnsureNamesOf(type, patch, options, "");
    }

    private static void EnsureNamesOf(Type type, JsonElement patch, JsonSerializerOptions options, string path)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        switch (patch.ValueKind)
        {
            case JsonValueKind.Array:
                if (ElementTypeOf(type, dictionary: false) is { } item)
                {
                    var index = 0;
                    foreach (var element in patch.EnumerateArray())
                        EnsureNamesOf(item, element, options, $"{path}[{index++}]");
                }
                break;

            case JsonValueKind.Object:
                var info = options.GetTypeInfo(type);
                if (info.Kind == JsonTypeInfoKind.Dictionary)
                {
                    if (ElementTypeOf(type, dictionary: true) is { } value)
                    {
                        foreach (var prop in patch.EnumerateObject())
                            EnsureNamesOf(value, prop.Value, options, Join(path, prop.Name));
                    }
                }
                else if (info.Kind == JsonTypeInfoKind.Object)
                {
                    foreach (var prop in patch.EnumerateObject())
                    {
                        var at = Join(path, prop.Name);
                        var known = info.Properties.FirstOrDefault(p => p.Name == prop.Name)
                            ?? throw new ArgumentException($"{at} is not a property of {type.Name}", nameof(patch));
                        EnsureNamesOf(known.PropertyType, prop.Value, options, at);
                    }
                }
                break;
        }
    }

    private static string Join(string path, string name) => path.Length == 0 ? name : $"{path}.{name}";

    private static Type? ElementTypeOf(Type type, bool dictionary)
    {
        if (type.IsArray)
            return type.GetElementType();

        foreach (var candidate in type.GetInterfaces().Prepend(type))
        {
            if (!candidate.IsGenericType)
                continue;

            var definition = candidate.GetGenericTypeDefinition();
            if (dictionary
                    ? definition == typeof(IDictionary<,>) || definition == typeof(IReadOnlyDictionary<,>)
                    : definition == typeof(IEnumerable<>))
                return candidate.GetGenericArguments()[^1];
        }

        return null;
    }

    /// <summary>
    /// Overlays <paramref name="patch"/> onto <paramref name="target"/>. <paramref name="adjust"/> sees
    /// the merged copy and the patch before the copy lands on the target, for a rule that ties one
    /// property to another.
    /// </summary>
    public static void ApplyInPlace<T>(T target, JsonElement patch, Action<T, JsonElement>? adjust = null)
        where T : class
    {
        using var existing = JsonSerializer.SerializeToDocument(target);

        var merged = new Dictionary<string, JsonElement>();
        foreach (var prop in existing.RootElement.EnumerateObject())
            merged[prop.Name] = prop.Value;
        foreach (var prop in patch.EnumerateObject())
            merged[prop.Name] = prop.Value;

        var patched = JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(merged))!;
        adjust?.Invoke(patched, patch);

        foreach (var prop in typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (prop.CanRead && prop.CanWrite && prop.GetIndexParameters().Length == 0)
                prop.SetValue(target, prop.GetValue(patched));
        }
    }
}
