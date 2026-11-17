using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nocturne.Core.Models.Serializers;

/// <summary>
/// JSON converter that handles flexible <see cref="LoopTargetRange"/> serialization for Nightscout compatibility.
/// </summary>
/// <remarks>
/// Nightscout Loop may send targetRange as either:
/// <list type="bullet">
/// <item><description>An array of exactly two numbers: [minValue, maxValue] (e.g., [100, 120]). Any other
/// array reads as no range.</description></item>
/// <item><description>An object: { "minValue": 100, "maxValue": 120 }</description></item>
/// </list>
/// On write, outputs the object format; <see cref="LoopTargetRangeArrayConverter"/> writes the array.
/// </remarks>
public class LoopTargetRangeConverter : JsonConverter<LoopTargetRange?>
{
    public override LoopTargetRange? Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options
    )
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.StartArray:
                return ReadFromArray(ref reader);

            case JsonTokenType.StartObject:
                return ReadFromObject(ref reader, options);

            case JsonTokenType.Null:
                return null;

            default:
                // Skip the value and return null for unexpected types
                reader.Skip();
                return null;
        }
    }

    private static LoopTargetRange? ReadFromArray(ref Utf8JsonReader reader)
    {
        var values = new List<double?>();

        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType is JsonTokenType.StartArray or JsonTokenType.StartObject)
                reader.Skip();

            values.Add(ReadNullableDouble(ref reader));
        }

        return values is [{ } min, { } max] ? new LoopTargetRange { MinValue = min, MaxValue = max } : null;
    }

    private static LoopTargetRange? ReadFromObject(ref Utf8JsonReader reader, JsonSerializerOptions options)
    {
        var result = new LoopTargetRange();

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
                break;

            if (reader.TokenType == JsonTokenType.PropertyName)
            {
                var propertyName = reader.GetString();
                reader.Read(); // Move to the value

                switch (propertyName?.ToLowerInvariant())
                {
                    case "minvalue":
                        result.MinValue = ReadNullableDouble(ref reader);
                        break;
                    case "maxvalue":
                        result.MaxValue = ReadNullableDouble(ref reader);
                        break;
                    default:
                        reader.Skip(); // Skip unknown properties
                        break;
                }
            }
        }

        return result;
    }

    private static double? ReadNullableDouble(ref Utf8JsonReader reader)
    {
        return reader.TokenType switch
        {
            JsonTokenType.Number => reader.GetDouble(),
            JsonTokenType.String when double.TryParse(reader.GetString(), out var val) => val,
            JsonTokenType.Null => null,
            _ => null
        };
    }

    public override void Write(Utf8JsonWriter writer, LoopTargetRange? value, JsonSerializerOptions options)
    {
        if (value == null)
        {
            writer.WriteNullValue();
            return;
        }

        // Write as object format for consistency
        writer.WriteStartObject();

        if (value.MinValue.HasValue)
        {
            writer.WriteNumber("minValue", value.MinValue.Value);
        }
        else
        {
            writer.WriteNull("minValue");
        }

        if (value.MaxValue.HasValue)
        {
            writer.WriteNumber("maxValue", value.MaxValue.Value);
        }
        else
        {
            writer.WriteNull("maxValue");
        }

        writer.WriteEndObject();
    }
}
