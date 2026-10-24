using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nocturne.Core.Models.Configuration;

/// <summary>
/// Reads a stored list of enum values and drops the ones this build does not define, so a value
/// written by a newer build (read after a rollback) costs that one entry rather than the whole
/// preferences blob. Writes enum names, as <see cref="JsonStringEnumConverter"/> does.
/// </summary>
internal sealed class LenientEnumListConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) =>
        typeToConvert.IsGenericType
        && typeToConvert.GetGenericTypeDefinition() == typeof(List<>)
        && typeToConvert.GetGenericArguments()[0].IsEnum;

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
        (JsonConverter)Activator.CreateInstance(
            typeof(LenientEnumListConverter<>).MakeGenericType(typeToConvert.GetGenericArguments()[0]))!;
}

internal sealed class LenientEnumListConverter<TEnum> : JsonConverter<List<TEnum>>
    where TEnum : struct, Enum
{
    public override List<TEnum>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
            return null;
        if (reader.TokenType != JsonTokenType.StartArray)
            throw new JsonException($"Expected an array of {typeof(TEnum).Name}.");

        var values = new List<TEnum>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.String
                    when Enum.TryParse<TEnum>(reader.GetString(), ignoreCase: true, out var named)
                        && Enum.IsDefined(named):
                    values.Add(named);
                    break;
                case JsonTokenType.Number when reader.TryGetInt32(out var number)
                    && Enum.IsDefined(typeof(TEnum), number):
                    values.Add((TEnum)Enum.ToObject(typeof(TEnum), number));
                    break;
                default:
                    reader.Skip();
                    break;
            }
        }
        return values;
    }

    public override void Write(Utf8JsonWriter writer, List<TEnum> value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var item in value)
            writer.WriteStringValue(item.ToString());
        writer.WriteEndArray();
    }
}
