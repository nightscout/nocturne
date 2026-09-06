using System.Text.Json;

namespace Nocturne.Infrastructure.Data.Mappers;

/// <summary>
/// Shared helpers for entity mappers.
/// </summary>
internal static class MapperHelpers
{
    /// <summary>
    /// Parse string ID to GUID. Returns a new GUID for null/empty input,
    /// or a deterministic SHA1-derived GUID for non-GUID strings.
    /// </summary>
    public static Guid ParseIdToGuid(string? id)
    {
        if (string.IsNullOrEmpty(id))
            return Guid.CreateVersion7();

        if (Guid.TryParse(id, out var guid))
            return guid;

        var hash = System.Security.Cryptography.SHA1.HashData(
            System.Text.Encoding.UTF8.GetBytes(id)
        );
        var guidBytes = new byte[16];
        Array.Copy(hash, guidBytes, 16);
        return new Guid(guidBytes);
    }

    /// <summary>
    /// Deserialize a JSON string, returning default for null, empty, "null", or malformed input.
    /// </summary>
    public static T? DeserializeJson<T>(string? json)
    {
        if (string.IsNullOrEmpty(json) || json == "null")
            return default;

        try
        {
            return JsonSerializer.Deserialize<T>(json);
        }
        catch (JsonException)
        {
            return default;
        }
    }
}
