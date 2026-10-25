using System.Reflection;
using System.Text.Json;

namespace Nocturne.API.Helpers;

/// <summary>
/// Top-level JSON merge-patch onto a record in place. Each property the patch names replaces the
/// record's value whole; nested objects are not merged.
/// </summary>
internal static class JsonMergePatch
{
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
