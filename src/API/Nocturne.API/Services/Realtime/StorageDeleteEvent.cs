using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nocturne.API.Services.Realtime;

/// <summary>
/// The storage <c>delete</c> event for a single removed record, as
/// <see cref="ISignalRBroadcastService.BroadcastStorageDeleteAsync"/> sends it. Five of the six
/// delete producers use it; <see cref="SignalRV4RecordBroadcaster{TModel}"/> sends its own shape, which the
/// socket.io bridge skips for want of a <c>colName</c>.
/// </summary>
/// <remarks>
/// NS v3 socket clients route on the top-level <c>colName</c> and look the removed record up by the
/// top-level <c>identifier</c>; they never look inside <c>doc</c>. The reference Nightscout server
/// emits exactly those two keys (<c>lib/api3/generic/delete/operation.js</c>), so they are a wire
/// contract rather than a naming convention and are spelled out here instead of being left to the
/// payload serializer's naming policy.
/// <para>
/// A client only ever holds the identifier its create event delivered, and the models disagree on
/// what that is — <see cref="Nocturne.Core.Models.Treatment"/> coerces its id to a Mongo ObjectId on
/// the wire, <see cref="Nocturne.Core.Models.Entry"/> and <see cref="Nocturne.Core.Models.DeviceStatus"/>
/// emit the uuid. <see cref="OnWire"/> reads the identifier back out of the serialized document so the
/// two stay in step for any model rather than restating each model's choice at the call site.
/// </para>
/// </remarks>
/// <param name="ColName">The collection the record belonged to.</param>
/// <param name="Identifier">The record id. Superseded by the document's own id where it has one.</param>
/// <param name="Doc">The removed record. Carried for clients that need the body; NS v3 clients ignore it.</param>
internal sealed record StorageDeleteEvent(
    [property: JsonPropertyName("colName")] string ColName,
    [property: JsonPropertyName("identifier")] string? Identifier,
    [property: JsonPropertyName("doc")] object? Doc = null
)
{
    /// <summary>
    /// The event resolved against the serializer the hub sends payloads with, so the identifier is
    /// the id the document itself puts on the wire. Prefers the v3 <c>identifier</c> over the legacy
    /// <c>_id</c>, and keeps the supplied <see cref="Identifier"/> for a document carrying neither.
    /// Serializing the document here rather than at the call site also lets the hub write the result
    /// verbatim instead of reflecting over it a second time.
    /// <para>
    /// One serializer this cannot follow: <see cref="System.Text.Json.Serialization.ReferenceHandler.Preserve"/>
    /// gives the pre-serialized document its own <c>$id</c> namespace, so the envelope emits <c>$id</c>
    /// twice and a sibling <c>$ref</c> would dangle. Nothing configures it today.
    /// </para>
    /// </summary>
    /// <param name="payloadOptions">
    /// <see cref="Microsoft.AspNetCore.SignalR.JsonHubProtocolOptions.PayloadSerializerOptions"/>.
    /// </param>
    internal StorageDeleteEvent OnWire(JsonSerializerOptions payloadOptions)
    {
        if (Doc is null)
            return this;

        var doc = JsonSerializer.SerializeToElement(Doc, payloadOptions);

        return this with { Identifier = Read(doc, "identifier") ?? Read(doc, "_id") ?? Identifier, Doc = doc };
    }

    private static string? Read(JsonElement doc, string property) =>
        doc.ValueKind == JsonValueKind.Object && doc.TryGetProperty(property, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.GetRawText(),
                _ => null,
            }
            : null;
}

/// <summary>
/// The storage <c>delete</c> event for a range or predicate delete. See <see cref="StorageDeleteEvent"/>
/// for the wire contract these keys belong to.
/// </summary>
/// <remarks>
/// No identifier is sent. The v3 socket contract has no bulk form — v3 DELETE addresses one identifier
/// at a time — and the callers that do hold the removed ids hold an unbounded set of them, so fanning
/// out would trade one event for a broadcast storm. An AAPS client reads the missing key as <c>""</c>
/// (<c>JSONObject.optString</c>) and enqueues it; the lookup it then runs
/// (<c>SELECT … WHERE nightscoutId = ''</c>, <c>GlucoseValueDao.findByNSId</c>) matches no record we
/// delivered, so the event neither removes anything nor errors.
/// </remarks>
/// <param name="ColName">The collection the records belonged to.</param>
/// <param name="DeletedCount">How many records were removed.</param>
internal sealed record StorageBulkDeleteEvent(
    [property: JsonPropertyName("colName")] string ColName,
    [property: JsonPropertyName("deletedCount")] long DeletedCount
);
