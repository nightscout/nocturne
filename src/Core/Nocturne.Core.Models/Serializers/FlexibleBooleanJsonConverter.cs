using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nocturne.Core.Models.Serializers;

/// <summary>
/// JSON converter that handles flexible boolean serialization for Nightscout compatibility.
/// Converts various string representations to boolean values (like "true", "false", "1", "0")
/// and handles null values gracefully.
/// </summary>
/// <remarks>
/// Nightscout clients may send booleans as strings ("true"/"false"), numbers (1/0),
/// or alternative forms ("yes"/"no", "on"/"off"). This converter normalizes all forms.
/// </remarks>
/// <seealso cref="FlexibleNonNullableBooleanJsonConverter"/>
public class FlexibleBooleanJsonConverter : JsonConverter<bool?>
{
    public override bool? Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options
    )
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.True:
                return true;
            case JsonTokenType.False:
                return false;
            case JsonTokenType.String:
                var stringValue = reader.GetString();
                if (string.IsNullOrWhiteSpace(stringValue))
                    return null;

                return stringValue.ToLowerInvariant() switch
                {
                    "true" => true,
                    "false" => false,
                    "1" => true,
                    "0" => false,
                    "yes" => true,
                    "no" => false,
                    "on" => true,
                    "off" => false,
                    _ => null,
                };
            case JsonTokenType.Number:
                return reader.TryGetInt32(out var numberValue)
                    ? numberValue switch
                    {
                        1 => true,
                        0 => false,
                        _ => null,
                    }
                    : null;
            case JsonTokenType.Null:
                return null;
            default:
                reader.TrySkip();
                return null;
        }
    }

    public override void Write(Utf8JsonWriter writer, bool? value, JsonSerializerOptions options)
    {
        if (value.HasValue)
        {
            writer.WriteBooleanValue(value.Value);
        }
        else
        {
            writer.WriteNullValue();
        }
    }
}

/// <summary>
/// JSON converter that handles flexible non-nullable boolean serialization for Nightscout compatibility.
/// Converts various string representations to boolean values (like "true", "false", "1", "0").
/// Defaults to <c>false</c> for unrecognized or null values.
/// </summary>
/// <seealso cref="FlexibleBooleanJsonConverter"/>
public class FlexibleNonNullableBooleanJsonConverter : JsonConverter<bool>
{
    public override bool Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options
    )
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.True:
                return true;
            case JsonTokenType.False:
                return false;
            case JsonTokenType.String:
                var stringValue = reader.GetString();
                if (string.IsNullOrWhiteSpace(stringValue))
                    return false;

                return stringValue.ToLowerInvariant() switch
                {
                    "true" => true,
                    "false" => false,
                    "1" => true,
                    "0" => false,
                    "yes" => true,
                    "no" => false,
                    "on" => true,
                    "off" => false,
                    _ => false,
                };
            case JsonTokenType.Number:
                return reader.TryGetInt32(out var numberValue) && numberValue == 1;
            case JsonTokenType.Null:
                return false;
            default:
                reader.TrySkip();
                return false;
        }
    }

    public override void Write(Utf8JsonWriter writer, bool value, JsonSerializerOptions options)
    {
        writer.WriteBooleanValue(value);
    }
}
