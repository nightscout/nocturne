using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nocturne.Connectors.Glooko.Xt;

/// <summary>
///     Reads a flag the server writes as <c>true</c>, <c>1</c>, <c>"true"</c> or <c>"1"</c>
///     depending on which client stored the record; null, <c>false</c>, <c>0</c> and anything
///     else read as null so an absent flag and a cleared one look the same to the mapper.
/// </summary>
public sealed class GlookoXtLenientBoolConverter : JsonConverter<bool?>
{
    public override bool? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.True: return true;
            case JsonTokenType.False: return false;
            case JsonTokenType.Null: return null;
            case JsonTokenType.Number:
                return reader.TryGetDouble(out var n) ? n != 0 : null;
            case JsonTokenType.String:
                var s = reader.GetString();
                if (bool.TryParse(s, out var b)) return b;
                if (double.TryParse(s, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var d)) return d != 0;
                return null;
            default:
                reader.Skip();
                return null;
        }
    }

    public override void Write(Utf8JsonWriter writer, bool? value, JsonSerializerOptions options)
    {
        if (value is null) writer.WriteNullValue();
        else writer.WriteBooleanValue(value.Value);
    }
}

/// <summary>
///     Reads a number the server may quote (<c>"6.7"</c>) or leave empty (<c>""</c>); an empty or
///     unparsable string is null rather than a failed batch.
/// </summary>
public sealed class GlookoXtLenientDoubleConverter : JsonConverter<double?>
{
    public override double? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Number: return reader.GetDouble();
            case JsonTokenType.Null: return null;
            case JsonTokenType.String:
                return double.TryParse(reader.GetString(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : null;
            default:
                reader.Skip();
                return null;
        }
    }

    public override void Write(Utf8JsonWriter writer, double? value, JsonSerializerOptions options)
    {
        if (value is null) writer.WriteNullValue();
        else writer.WriteNumberValue(value.Value);
    }
}

/// <summary>Reads a server id that arrives as a number or a numeric string.</summary>
public sealed class GlookoXtLenientLongConverter : JsonConverter<long?>
{
    public override long? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Number:
                if (reader.TryGetInt64(out var l)) return l;
                return (long)reader.GetDouble();
            case JsonTokenType.Null: return null;
            case JsonTokenType.String:
                return long.TryParse(reader.GetString(), out var p) ? p : null;
            default:
                reader.Skip();
                return null;
        }
    }

    public override void Write(Utf8JsonWriter writer, long? value, JsonSerializerOptions options)
    {
        if (value is null) writer.WriteNullValue();
        else writer.WriteNumberValue(value.Value);
    }
}
