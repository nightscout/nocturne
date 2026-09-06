using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nocturne.Core.Models.Serializers;

/// <summary>
/// Token reads shared by the flexible number converters. Each returns <c>null</c> for a value it
/// cannot represent, leaving the non-nullable converters to substitute their own zero default.
/// Wire numbers are culture-free, so every string parse is invariant regardless of the thread's
/// culture.
/// </summary>
internal static class FlexibleNumberReader
{
    /// <summary>
    /// AAPS types treatment fields such as <c>originalDuration</c> as <c>Int</c>/<c>Long</c> yet writes
    /// fractional values into them (<c>29.999999966</c>), so a fractional value is rounded rather than
    /// rejected. Rounding rather than truncating is what keeps these fields comparable to the
    /// <c>double?</c> fields AAPS checks them against: <c>duration</c> goes out through
    /// <see cref="RoundedNullableDoubleConverter"/>, so a truncated <c>originalDuration</c> alongside a
    /// rounded <c>duration</c> would read to AAPS as a temp basal that was cut a minute short.
    /// </summary>
    public static int? ReadInt(ref Utf8JsonReader reader)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Number:
                if (reader.TryGetInt32(out var intValue))
                    return intValue;

                return reader.TryGetDouble(out var number) ? RoundToInt(number) : null;

            case JsonTokenType.String:
                var stringValue = reader.GetString();
                if (string.IsNullOrWhiteSpace(stringValue))
                    return null;

                if (
                    int.TryParse(
                        stringValue,
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out var result
                    )
                )
                    return result;

                return TryParseDouble(stringValue, out var parsed) ? RoundToInt(parsed) : null;

            case JsonTokenType.StartObject:
            case JsonTokenType.StartArray:
                reader.Skip();
                return null;

            default:
                return null;
        }
    }

    /// <inheritdoc cref="ReadInt"/>
    public static long? ReadLong(ref Utf8JsonReader reader)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Number:
                if (reader.TryGetInt64(out var longValue))
                    return longValue;

                return reader.TryGetDouble(out var number) ? RoundToLong(number) : null;

            case JsonTokenType.String:
                var stringValue = reader.GetString();
                if (string.IsNullOrWhiteSpace(stringValue))
                    return null;

                if (
                    long.TryParse(
                        stringValue,
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out var result
                    )
                )
                    return result;

                return TryParseDouble(stringValue, out var parsed) ? RoundToLong(parsed) : null;

            case JsonTokenType.StartObject:
            case JsonTokenType.StartArray:
                reader.Skip();
                return null;

            default:
                return null;
        }
    }

    public static double? ReadDouble(ref Utf8JsonReader reader)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Number:
                return reader.TryGetDouble(out var number) && double.IsFinite(number)
                    ? number
                    : null;

            case JsonTokenType.String:
                var stringValue = reader.GetString();
                if (string.IsNullOrWhiteSpace(stringValue))
                    return null;

                return TryParseDouble(stringValue, out var parsed) && double.IsFinite(parsed)
                    ? parsed
                    : null;

            case JsonTokenType.StartObject:
            case JsonTokenType.StartArray:
                reader.Skip();
                return null;

            default:
                return null;
        }
    }

    public static decimal? ReadDecimal(ref Utf8JsonReader reader)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Number:
                return reader.TryGetDecimal(out var number) ? number : null;

            case JsonTokenType.String:
                var stringValue = reader.GetString();
                if (string.IsNullOrWhiteSpace(stringValue))
                    return null;

                return decimal.TryParse(
                    stringValue,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var parsed
                )
                    ? parsed
                    : null;

            case JsonTokenType.StartObject:
            case JsonTokenType.StartArray:
                reader.Skip();
                return null;

            default:
                return null;
        }
    }

    private static bool TryParseDouble(string value, out double result) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result);

    private static int? RoundToInt(double value)
    {
        var rounded = Math.Round(value);
        return IsInt32Range(rounded) ? (int)rounded : null;
    }

    private static long? RoundToLong(double value)
    {
        var rounded = Math.Round(value);
        return IsInt64Range(rounded) ? (long)rounded : null;
    }

    /// <summary>
    /// The cast is defined and saturates, so an out-of-range value would arrive as
    /// <see cref="int.MaxValue"/> or <see cref="int.MinValue"/> — indistinguishable from a real
    /// reading. The field is reported missing instead.
    /// </summary>
    private static bool IsInt32Range(double value) => value is >= int.MinValue and <= int.MaxValue;

    /// <summary>
    /// As <see cref="IsInt32Range"/>. The upper bound is exclusive because
    /// <see cref="long.MaxValue"/> has no exact double representation — the nearest double is 2^63,
    /// which is one past the range.
    /// </summary>
    private static bool IsInt64Range(double value) =>
        value is >= -9223372036854775808.0 and < 9223372036854775808.0;
}

/// <summary>
/// JSON converter that handles flexible int (Int32) serialization for Nightscout compatibility.
/// Nightscout may send numeric values as either numbers or strings depending on the context.
/// Values that are not representable as an Int32 read as 0.
/// </summary>
/// <seealso cref="FlexibleNullableIntConverter"/>
/// <seealso cref="FlexibleDoubleConverter"/>
public class FlexibleIntConverter : JsonConverter<int>
{
    public override int Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options
    ) => FlexibleNumberReader.ReadInt(ref reader) ?? 0;

    public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options)
    {
        writer.WriteNumberValue(value);
    }
}

/// <summary>
/// JSON converter that handles flexible nullable int (Int32?) serialization for Nightscout compatibility.
/// Values that are not representable as an Int32 read as null.
/// </summary>
/// <seealso cref="FlexibleIntConverter"/>
public class FlexibleNullableIntConverter : JsonConverter<int?>
{
    public override int? Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options
    ) => FlexibleNumberReader.ReadInt(ref reader);

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
/// JSON converter that handles flexible long (Int64) serialization for Nightscout compatibility.
/// Nightscout may send numeric values as either numbers or strings depending on the context.
/// Values that are not representable as an Int64 read as 0.
/// </summary>
/// <seealso cref="FlexibleNullableLongConverter"/>
/// <seealso cref="FlexibleIntConverter"/>
public class FlexibleLongConverter : JsonConverter<long>
{
    public override long Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options
    ) => FlexibleNumberReader.ReadLong(ref reader) ?? 0;

    public override void Write(Utf8JsonWriter writer, long value, JsonSerializerOptions options)
    {
        writer.WriteNumberValue(value);
    }
}

/// <summary>
/// JSON converter that handles flexible nullable long (Int64?) serialization for Nightscout compatibility.
/// Values that are not representable as an Int64 read as null.
/// </summary>
/// <seealso cref="FlexibleLongConverter"/>
public class FlexibleNullableLongConverter : JsonConverter<long?>
{
    public override long? Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options
    ) => FlexibleNumberReader.ReadLong(ref reader);

    public override void Write(Utf8JsonWriter writer, long? value, JsonSerializerOptions options)
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
/// A value that is not a finite double reads as 0.
/// </summary>
/// <seealso cref="FlexibleNullableDoubleConverter"/>
/// <seealso cref="FlexibleIntConverter"/>
public class FlexibleDoubleConverter : JsonConverter<double>
{
    public override double Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options
    ) => FlexibleNumberReader.ReadDouble(ref reader) ?? 0;

    public override void Write(Utf8JsonWriter writer, double value, JsonSerializerOptions options)
    {
        writer.WriteNumberValue(value);
    }
}

/// <summary>
/// JSON converter that handles flexible nullable double serialization for Nightscout compatibility.
/// A value that is not a finite double reads as null.
/// </summary>
/// <seealso cref="FlexibleDoubleConverter"/>
public class FlexibleNullableDoubleConverter : JsonConverter<double?>
{
    public override double? Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options
    ) => FlexibleNumberReader.ReadDouble(ref reader);

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
/// Reads a nullable double flexibly (number or string, like <see cref="FlexibleNullableDoubleConverter"/>)
/// but writes it rounded to a whole number. Nightscout's <c>duration</c> is integer minutes on the wire;
/// AAPS parses it as a Long and throws <c>NumberFormatException</c> on a fractional value. Rounding lives
/// here — at serialization — rather than in the model getter, so in-memory calculations that rely on
/// sub-minute precision (e.g. temp-basal duration cutting) keep the exact value.
/// </summary>
/// <seealso cref="FlexibleNullableDoubleConverter"/>
public class RoundedNullableDoubleConverter : JsonConverter<double?>
{
    public override double? Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options
    ) => FlexibleNumberReader.ReadDouble(ref reader);

    public override void Write(Utf8JsonWriter writer, double? value, JsonSerializerOptions options)
    {
        if (value.HasValue)
        {
            writer.WriteNumberValue(Math.Round(value.Value));
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
/// Values that are not representable as a decimal read as 0.
/// </summary>
/// <seealso cref="FlexibleNullableDecimalConverter"/>
/// <seealso cref="FlexibleDoubleConverter"/>
public class FlexibleDecimalConverter : JsonConverter<decimal>
{
    public override decimal Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options
    ) => FlexibleNumberReader.ReadDecimal(ref reader) ?? 0;

    public override void Write(Utf8JsonWriter writer, decimal value, JsonSerializerOptions options)
    {
        writer.WriteNumberValue(value);
    }
}

/// <summary>
/// JSON converter that handles flexible nullable decimal serialization for Nightscout compatibility.
/// Values that are not representable as a decimal read as null.
/// </summary>
/// <seealso cref="FlexibleDecimalConverter"/>
public class FlexibleNullableDecimalConverter : JsonConverter<decimal?>
{
    public override decimal? Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options
    ) => FlexibleNumberReader.ReadDecimal(ref reader);

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
