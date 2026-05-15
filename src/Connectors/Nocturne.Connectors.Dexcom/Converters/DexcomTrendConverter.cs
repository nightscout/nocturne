using System.Text.Json;
using System.Text.Json.Serialization;
using Nocturne.Core.Models.V4;

namespace Nocturne.Connectors.Dexcom.Converters;

/// Dexcom Share returns Trend as either a numeric code (0-9) or a string name
/// ("Flat", "FortyFiveDown", ...). Both shapes have been observed in the wild;
/// reject neither.
internal sealed class DexcomTrendConverter : JsonConverter<GlucoseDirection>
{
    public override GlucoseDirection Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options
    )
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Number
                when reader.TryGetInt32(out var ordinal)
                    && Enum.IsDefined(typeof(GlucoseDirection), ordinal):
                return (GlucoseDirection)ordinal;

            case JsonTokenType.String
                when Enum.TryParse<GlucoseDirection>(
                    reader.GetString(),
                    ignoreCase: true,
                    out var named
                ):
                return named;

            default:
                return GlucoseDirection.NotComputable;
        }
    }

    public override void Write(
        Utf8JsonWriter writer,
        GlucoseDirection value,
        JsonSerializerOptions options
    ) => writer.WriteStringValue(value.ToString());
}
