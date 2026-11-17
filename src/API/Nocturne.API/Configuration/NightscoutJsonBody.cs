using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Mvc;
using Nocturne.Core.Models.Serializers;

namespace Nocturne.API.Configuration;

/// <summary>
/// Reads a legacy request body held as a <see cref="JsonElement"/> with the options
/// <see cref="NightscoutJsonInputFormatter"/> binds typed bodies with, so a POST and a PUT accept
/// the same document. A malformed document throws <see cref="JsonException"/>.
/// </summary>
/// <remarks>
/// Static because it holds no state beyond the options, which the formatter must share.
/// </remarks>
public static class NightscoutJsonBody
{
    internal static JsonOptions Shared { get; } = Create();

    private static JsonSerializerOptions Options => Shared.JsonSerializerOptions;

    private static JsonOptions Create()
    {
        var options = new JsonOptions();
        options.JsonSerializerOptions.TypeInfoResolver =
            (options.JsonSerializerOptions.TypeInfoResolver ?? new DefaultJsonTypeInfoResolver())
            .WithAddedModifier(IgnoredBasePropertyModifier.RemoveIgnoredBaseProperties);
        return options;
    }

    public static T? ReadObject<T>(JsonElement body) => body.Deserialize<T>(Options);

    /// <summary>The documents in an object or an array body, or null for any other body. Null elements are skipped.</summary>
    public static List<T>? ReadObjects<T>(JsonElement body) where T : class =>
        ReadObjects(body, element => element.Deserialize<T>(Options));

    /// <summary><see cref="ReadObjects{T}(JsonElement)"/> with each document read by <paramref name="read"/>.</summary>
    public static List<T>? ReadObjects<T>(JsonElement body, Func<JsonElement, T?> read) where T : class => body.ValueKind switch
    {
        JsonValueKind.Array => body.EnumerateArray().Select(read).OfType<T>().ToList(),
        JsonValueKind.Object => read(body) is { } single ? [single] : [],
        _ => null,
    };
}
