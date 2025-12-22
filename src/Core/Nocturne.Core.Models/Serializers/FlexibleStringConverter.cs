using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nocturne.Core.Models.Serializers;

/// <summary>
/// JSON converter that handles flexible string deserialization.
/// Converts numbers, booleans, and other primitives to their string representation.
/// </summary>
public class FlexibleStringConverter : JsonConverter<string?>
{
    public override string? Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options
    )
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                return reader.GetString();

            case JsonTokenType.Number:
                if (reader.TryGetInt64(out var longValue))
                {
                    return longValue.ToString();
                }
                return reader.GetDouble().ToString(System.Globalization.CultureInfo.InvariantCulture);

            case JsonTokenType.True:
                return "true";

            case JsonTokenType.False:
                return "false";

            case JsonTokenType.Null:
                return null;

            default:
                // For other types (like objects/arrays), just return null or allow default exception?
                // For robust "stringify", we could use JsonDocument, but typically we only expect primitives in string fields.
                // Returning null is safer than crashing for unexpected objects.
                return null;
        }
    }

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value);
    }
}
