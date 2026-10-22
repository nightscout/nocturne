using System.Globalization;
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
    /// The legacy <c>created_at</c> of a record that stores no creation time, taken from the
    /// reading's own timestamp so a v1 reader paging on <c>created_at</c> sees a stable value.
    /// </summary>
    public static string CreatedAtFromReading(DateTime timestamp) =>
        DateTime.SpecifyKind(timestamp, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

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
