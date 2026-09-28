using System.Text.Json;

namespace Nocturne.Core.Models.Serializers;

/// <summary>
/// Reads <c>trend</c> from a number, a numeric string or a direction name. Older
/// share2nightscout-bridge versions stored the direction name, such as <c>"Flat"</c>. A name is
/// normalised to its trend number on purpose, so every stored trend is numeric.
/// </summary>
/// <seealso cref="Entry.Trend"/>
public class FlexibleTrendConverter : FlexibleNullableIntConverter
{
    public override int? Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options
    )
    {
        var number = FlexibleNumberReader.ReadInt(ref reader);
        if (number.HasValue || reader.TokenType != JsonTokenType.String)
            return number;

        return DirectionExtensions.TryParse(reader.GetString(), out var direction)
            ? direction.ToTrendNumber()
            : null;
    }
}
