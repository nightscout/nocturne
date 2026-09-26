using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nocturne.Core.Models.Serializers;

/// <summary>
/// JSON converter for glucose trend direction that handles both string values
/// ("Flat", "SingleUp") and numeric values (1-9) from older Nightscout records.
/// </summary>
/// <remarks>
/// Numbers are read on the Dexcom trend scale; see <see cref="DirectionExtensions.TryFromTrendNumber"/>.
/// </remarks>
/// <seealso cref="Entry"/>
public class FlexibleDirectionConverter : JsonConverter<string?>
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
                if (reader.TryGetInt32(out var intValue))
                {
                    return DirectionExtensions.TryFromTrendNumber(intValue, out var direction)
                        ? direction.ToWireString()
                        : intValue.ToString();
                }
                return null;

            case JsonTokenType.Null:
                return null;

            default:
                return null;
        }
    }

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value);
    }
}
