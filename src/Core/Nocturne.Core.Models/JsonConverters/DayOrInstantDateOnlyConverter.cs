using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nocturne.Core.Models.JsonConverters;

/// <summary>
/// Reads a calendar day from a <c>YYYY-MM-DD</c> string or from an ISO 8601 instant, whose UTC date
/// is the day. Writes <c>YYYY-MM-DD</c>.
/// </summary>
/// <remarks>
/// For a request field whose clients sent an instant at UTC midnight before the field became a day.
/// A string that is neither throws <see cref="JsonException"/>, which model binding answers with 400.
/// </remarks>
public class DayOrInstantDateOnlyConverter : JsonConverter<DateOnly>
{
    private const string DayFormat = "yyyy-MM-dd";

    public override DateOnly Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options
    )
    {
        if (reader.TokenType != JsonTokenType.String)
            throw new JsonException($"Expected a {DayFormat} day or an ISO 8601 instant, found {reader.TokenType}");

        var value = reader.GetString();
        if (DateOnly.TryParseExact(value, DayFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
            return day;
        if (
            DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var instant
            )
        )
            return DateOnly.FromDateTime(instant.UtcDateTime);

        throw new JsonException($"Expected a {DayFormat} day or an ISO 8601 instant");
    }

    public override void Write(Utf8JsonWriter writer, DateOnly value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString(DayFormat, CultureInfo.InvariantCulture));
}
