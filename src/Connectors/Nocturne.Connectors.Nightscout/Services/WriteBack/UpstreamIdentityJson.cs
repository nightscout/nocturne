using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Serializers;

namespace Nocturne.Connectors.Nightscout.Services.WriteBack;

/// <summary>
/// Serializer options for a write-back that keeps a record's identity across the round trip: the
/// upstream instance stores the record under the <c>_id</c> it is sent, and the connector's next pull
/// matches that id against the stored <c>LegacyId</c>. <see cref="ObjectIdJsonConverter"/> hashes a
/// non-ObjectId legacy id (e.g. <c>dexcom_…</c>) to a value no stored row carries, so the pull would
/// land a second record. Here a legacy id is sent verbatim; only a uuid, the id of a record with no
/// legacy id, takes its 24-hex prefix, which ingest resolves back to the record by uuid range.
/// </summary>
/// <remarks>
/// A legacy id that is itself a uuid string is indistinguishable from a record's own uuid and is
/// sent as its prefix too.
/// </remarks>
internal static class UpstreamIdentityJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver { Modifiers = { PreserveLegacyId } },
    };

    private static readonly JsonConverter Converter = new UpstreamIdConverter();

    private static void PreserveLegacyId(JsonTypeInfo info)
    {
        foreach (var property in info.Properties)
        {
            if (property.CustomConverter is not ObjectIdJsonConverter)
                continue;

            if (info.Type != typeof(Treatment))
            {
                property.CustomConverter = Converter;
                continue;
            }

            // A treatment is served by its record's uuid but carries its legacy id apart, so that id
            // goes out verbatim whatever its shape, a uuid syncIdentifier included.
            var served = property.Get!;
            property.CustomConverter = null;
            property.Get = treatment => ((Treatment)treatment).LegacyId
                ?? UpstreamIdConverter.OwnId(served(treatment) as string);
        }
    }

    private sealed class UpstreamIdConverter : JsonConverter<string?>
    {
        public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => reader.TokenType == JsonTokenType.Null ? null : reader.GetString();

        // HandleNull is false, so the serializer writes a null id itself and never calls this with one.
        public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
            => writer.WriteStringValue(OwnId(value));

        public static string? OwnId(string? value)
            => Guid.TryParse(value, out var uuid) ? MongoObjectId.FromGuid(uuid) : value;
    }
}
