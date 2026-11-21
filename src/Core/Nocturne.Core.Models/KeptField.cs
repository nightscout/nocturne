using System.Globalization;
using System.Text.Json;

namespace Nocturne.Core.Models;

/// <summary>
/// Reads and writes a value kept under a key in a record's additional properties. A value read
/// back from the database is a <see cref="JsonElement"/>, one not yet persisted is the CLR value,
/// and each typed reader accepts both and rejects any other kind. The lenient and any-kind readers
/// instead turn a value of any kind into text, for fields an uploader may send as a string or a number.
/// </summary>
public static class KeptField
{
    /// <summary><paramref name="record"/> with <paramref name="value"/> added under <paramref name="key"/>, or unchanged when it is null.</summary>
    public static Dictionary<string, object?>? Add(Dictionary<string, object?>? record, string key, object? value)
    {
        if (value is null)
            return record;

        record ??= new();
        record[key] = value;
        return record;
    }

    public static string? String(IReadOnlyDictionary<string, object?>? record, string key) =>
        record?.GetValueOrDefault(key) switch
        {
            string text => text,
            JsonElement { ValueKind: JsonValueKind.String } text => text.GetString(),
            _ => null,
        };

    public static double? Double(IReadOnlyDictionary<string, object?>? record, string key) =>
        record?.GetValueOrDefault(key) switch
        {
            double number => number,
            JsonElement { ValueKind: JsonValueKind.Number } number => number.GetDouble(),
            _ => null,
        };

    public static bool? Bool(IReadOnlyDictionary<string, object?>? record, string key) =>
        record?.GetValueOrDefault(key) switch
        {
            bool flag => flag,
            JsonElement { ValueKind: JsonValueKind.True } => true,
            JsonElement { ValueKind: JsonValueKind.False } => false,
            _ => null,
        };

    /// <summary>
    /// A string or JSON string as itself, null as null, and any other value as its invariant text
    /// (a JSON number as its literal, JSON <c>true</c> as <c>True</c>, JSON null as empty).
    /// </summary>
    public static string? LenientString(object? value) => value switch
    {
        string text => text,
        JsonElement { ValueKind: JsonValueKind.String } text => text.GetString(),
        null => null,
        _ => Convert.ToString(value, CultureInfo.InvariantCulture),
    };

    /// <summary>
    /// The value under <paramref name="key"/> as text whatever its kind: a string as itself, a JSON
    /// value of any other kind as its raw JSON text, any other CLR value by <c>ToString</c>.
    /// Null when absent or JSON null.
    /// </summary>
    public static string? AnyKindAsText(IReadOnlyDictionary<string, object?>? record, string key) =>
        AnyKindAsText(Value(record, key));

    /// <inheritdoc cref="AnyKindAsText(IReadOnlyDictionary{string, object?}?, string)"/>
    public static string? AnyKindAsText(object? value) => Present(value) switch
    {
        JsonElement { ValueKind: JsonValueKind.String } text => text.GetString(),
        JsonElement element => element.GetRawText(),
        { } other => other.ToString(),
        null => null,
    };

    /// <summary>The value under <paramref name="key"/> as stored, or null when the record or key is absent or the value is JSON null.</summary>
    public static object? Value(IReadOnlyDictionary<string, object?>? record, string key) =>
        Present(record?.GetValueOrDefault(key));

    /// <summary>The value as stored, whatever its kind, or null when absent or JSON null.</summary>
    public static object? Present(object? value) =>
        value is null or JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } ? null : value;

    /// <summary>The value as a <see cref="JsonElement"/>, serialising one not yet persisted, or null when absent or JSON null.</summary>
    public static JsonElement? Json(object? value) => Present(value) switch
    {
        null => null,
        JsonElement element => element,
        var other => JsonSerializer.SerializeToElement(other),
    };
}
