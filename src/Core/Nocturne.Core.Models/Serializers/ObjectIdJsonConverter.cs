using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nocturne.Core.Models.Serializers;

/// <summary>
/// Serializes a record identifier as a 24-char hex Mongo ObjectId (<see cref="MongoObjectId.Coerce"/>)
/// so AAPS's <c>isObjectId()</c> check passes. Reading is a passthrough — the incoming id (e.g. an
/// AAPS-supplied ObjectId, or one echoed back for a PATCH/DELETE) is preserved verbatim and resolved
/// server-side. Only the wire representation changes; the in-memory value is untouched.
/// </summary>
public class ObjectIdJsonConverter : JsonConverter<string?>
{
    public override string? Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options
    ) => reader.TokenType == JsonTokenType.Null ? null : reader.GetString();

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
    {
        var coerced = MongoObjectId.Coerce(value);
        if (coerced is null)
            writer.WriteNullValue();
        else
            writer.WriteStringValue(coerced);
    }
}
