using System.Globalization;
using System.Text.Json;

namespace Nocturne.Infrastructure.Data.Mappers;

/// <summary>
/// Shared helpers for entity mappers.
/// </summary>
internal static class MapperHelpers
{
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
