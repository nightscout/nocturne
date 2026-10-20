using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nocturne.Core.Models.Serializers;

/// <summary>
/// Writes a nullable <see cref="DateTimeOffset"/> as Unix milliseconds, the form upstream gives
/// <c>srvCreated</c> and <c>srvModified</c> and AAPS reads them in. Reads milliseconds as a number
/// or a string, or an ISO 8601 string; anything else reads as null.
/// </summary>
public class UnixMillisecondsConverter : JsonConverter<DateTimeOffset?>
{
    public override DateTimeOffset? Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options
    )
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var text = reader.GetString();
            if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)
                && DateTimeOffset.TryParse(
                    text,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal,
                    out var parsed
                ))
            {
                return parsed;
            }
        }

        var mills = FlexibleNumberReader.ReadLong(ref reader);
        return mills is >= -62135596800000 and <= 253402300799999
            ? DateTimeOffset.FromUnixTimeMilliseconds(mills.Value)
            : null;
    }

    public override void Write(Utf8JsonWriter writer, DateTimeOffset? value, JsonSerializerOptions options)
    {
        if (value.HasValue)
        {
            writer.WriteNumberValue(value.Value.ToUnixTimeMilliseconds());
        }
        else
        {
            writer.WriteNullValue();
        }
    }
}
