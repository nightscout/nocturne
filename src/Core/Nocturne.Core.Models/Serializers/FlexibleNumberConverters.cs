using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nocturne.Core.Models.Serializers;

/// <summary>
/// JSON converter that handles flexible int (Int32) serialization for Nightscout compatibility.
/// Nightscout may send numeric values as either numbers or strings depending on the context.
/// </summary>
/// <seealso cref="FlexibleNullableIntConverter"/>
/// <seealso cref="FlexibleDoubleConverter"/>
public class FlexibleIntConverter : JsonConverter<int>
{
    public override int Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options
    )
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Number:
                return reader.GetInt32();

            case JsonTokenType.String:
                var stringValue = reader.GetString();
                if (string.IsNullOrWhiteSpace(stringValue))
                    return 0;

                if (int.TryParse(stringValue, out var result))
                    return result;

                // Try parsing as double and truncating
                if (double.TryParse(stringValue, out var doubleResult))
                    return (int)doubleResult;

                return 0;

            case JsonTokenType.Null:
                return 0;

            default:
                return 0;
        }
    }

    public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options)
    {
        writer.WriteNumberValue(value);
    }
}

/// <summary>
/// JSON converter that handles flexible nullable int (Int32?) serialization for Nightscout compatibility.
/// </summary>
/// <seealso cref="FlexibleIntConverter"/>
public class FlexibleNullableIntConverter : JsonConverter<int?>
{
    public override int? Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options
    )
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Number:
                return reader.GetInt32();

            case JsonTokenType.String:
                var stringValue = reader.GetString();
                if (string.IsNullOrWhiteSpace(stringValue))
                    return null;

                if (int.TryParse(stringValue, out var result))
                    return result;

                // Try parsing as double and truncating
                if (double.TryParse(stringValue, out var doubleResult))
                    return (int)doubleResult;

                return null;

            case JsonTokenType.Null:
                return null;

            default:
                return null;
        }
    }

    public override void Write(Utf8JsonWriter writer, int? value, JsonSerializerOptions options)
    {
        if (value.HasValue)
        {
            writer.WriteNumberValue(value.Value);
        }
        else
        {
            writer.WriteNullValue();
        }
    }
}

/// <summary>
/// JSON converter that handles flexible double serialization for Nightscout compatibility.
/// Nightscout may send numeric values as either numbers or strings depending on the context.
/// </summary>
/// <seealso cref="FlexibleNullableDoubleConverter"/>
/// <seealso cref="FlexibleIntConverter"/>
public class FlexibleDoubleConverter : JsonConverter<double>
{
    public override double Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options
    )
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Number:
                return reader.GetDouble();

            case JsonTokenType.String:
                var stringValue = reader.GetString();
                if (string.IsNullOrWhiteSpace(stringValue))
                    return 0;

                if (double.TryParse(stringValue, out var result))
                    return result;

                return 0;

            case JsonTokenType.Null:
                return 0;

            default:
                return 0;
        }
    }

    public override void Write(Utf8JsonWriter writer, double value, JsonSerializerOptions options)
    {
        writer.WriteNumberValue(value);
    }
}

/// <summary>
/// JSON converter that handles flexible nullable double serialization for Nightscout compatibility.
/// </summary>
/// <seealso cref="FlexibleDoubleConverter"/>
public class FlexibleNullableDoubleConverter : JsonConverter<double?>
{
    public override double? Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options
    )
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Number:
                return reader.GetDouble();

            case JsonTokenType.String:
                var stringValue = reader.GetString();
                if (string.IsNullOrWhiteSpace(stringValue))
                    return null;

                if (double.TryParse(stringValue, out var result))
                    return result;

                return null;

            case JsonTokenType.Null:
                return null;

            default:
                return null;
        }
    }

    public override void Write(Utf8JsonWriter writer, double? value, JsonSerializerOptions options)
    {
        if (value.HasValue)
        {
            writer.WriteNumberValue(value.Value);
        }
        else
        {
            writer.WriteNullValue();
        }
    }
}

/// <summary>
/// JSON converter that handles flexible decimal serialization for Nightscout compatibility.
/// Nightscout may send numeric values as either numbers or strings depending on the context.
/// </summary>
/// <seealso cref="FlexibleNullableDecimalConverter"/>
/// <seealso cref="FlexibleDoubleConverter"/>
public class FlexibleDecimalConverter : JsonConverter<decimal>
{
    public override decimal Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options
    )
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Number:
                return reader.GetDecimal();

            case JsonTokenType.String:
                var stringValue = reader.GetString();
                if (string.IsNullOrWhiteSpace(stringValue))
                    return 0;

                if (decimal.TryParse(stringValue, out var result))
                    return result;

                return 0;

            case JsonTokenType.Null:
                return 0;

            default:
                return 0;
        }
    }

    public override void Write(Utf8JsonWriter writer, decimal value, JsonSerializerOptions options)
    {
        writer.WriteNumberValue(value);
    }
}

/// <summary>
/// JSON converter that handles flexible nullable decimal serialization for Nightscout compatibility.
/// </summary>
/// <seealso cref="FlexibleDecimalConverter"/>
public class FlexibleNullableDecimalConverter : JsonConverter<decimal?>
{
    public override decimal? Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options
    )
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Number:
                return reader.GetDecimal();

            case JsonTokenType.String:
                var stringValue = reader.GetString();
                if (string.IsNullOrWhiteSpace(stringValue))
                    return null;

                if (decimal.TryParse(stringValue, out var result))
                    return result;

                return null;

            case JsonTokenType.Null:
                return null;

            default:
                return null;
        }
    }

    public override void Write(Utf8JsonWriter writer, decimal? value, JsonSerializerOptions options)
    {
        if (value.HasValue)
        {
            writer.WriteNumberValue(value.Value);
        }
        else
        {
            writer.WriteNullValue();
        }
    }
}
