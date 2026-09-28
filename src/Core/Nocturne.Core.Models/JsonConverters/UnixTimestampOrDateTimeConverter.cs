using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nocturne.Core.Models.JsonConverters;

/// <summary>
/// JSON converter that can handle both Unix timestamps (numbers) and DateTime strings.
/// This is needed because some APIs return dates as Unix timestamps while others use ISO strings.
/// </summary>
/// <remarks>
/// On read, accepts either a JSON number (Unix milliseconds) or a JSON string (ISO 8601).
/// On write, always outputs a JSON number (Unix milliseconds).
/// </remarks>
public class UnixTimestampOrDateTimeConverter : JsonConverter<DateTime?>
{
    public override DateTime? Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options
    )
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Number:
                // Handle Unix timestamp (milliseconds)
                var timestamp = reader.GetInt64();
                return DateTimeOffset.FromUnixTimeMilliseconds(timestamp).UtcDateTime;

            case JsonTokenType.String:
                // Handle ISO datetime string
                return UploaderTimestamp.ParseUtcDateTime(reader.GetString());

            case JsonTokenType.Null:
                return null;

            default:
                throw new JsonException(
                    $"Unexpected token type {reader.TokenType} when reading DateTime"
                );
        }
    }

    public override void Write(
        Utf8JsonWriter writer,
        DateTime? value,
        JsonSerializerOptions options
    )
    {
        if (value.HasValue)
        {
            // Write as Unix timestamp in milliseconds
            var timestamp = (
                (DateTimeOffset)DateTime.SpecifyKind(value.Value, DateTimeKind.Utc)
            ).ToUnixTimeMilliseconds();
            writer.WriteNumberValue(timestamp);
        }
        else
        {
            writer.WriteNullValue();
        }
    }
}
