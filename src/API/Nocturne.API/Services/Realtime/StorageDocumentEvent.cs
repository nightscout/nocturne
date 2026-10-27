using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nocturne.API.Services.Realtime;

/// <summary>
/// The storage <c>create</c> or <c>update</c> event for one record of a Nightscout collection, as
/// <see cref="ISignalRBroadcastService.BroadcastStorageCreateAsync"/> and
/// <see cref="ISignalRBroadcastService.BroadcastStorageUpdateAsync"/> send it. See
/// <see cref="StorageDeleteEvent"/> for why <c>colName</c> is spelled out.
/// </summary>
/// <remarks>
/// Nightscout broadcasts the stored document, which holds only the keys its uploader sent. A model
/// property the upload left unset therefore has to stay off the wire rather than arrive as
/// <c>null</c>: AAPS reads devicestatus with Gson, which throws on a <c>null</c> where its model
/// declares an object, so an upload carrying <c>"openaps": {}</c> echoed back as
/// <c>"openaps": {"suggested": null, …}</c> crashes the uploader that sent it.
/// </remarks>
/// <param name="ColName">The collection the record belongs to.</param>
/// <param name="Doc">The record.</param>
internal sealed record StorageDocumentEvent(
    [property: JsonPropertyName("colName")] string ColName,
    [property: JsonPropertyName("doc")] object Doc
)
{
    private static readonly ConditionalWeakTable<JsonSerializerOptions, JsonSerializerOptions> OmittingNulls = [];

    /// <summary>
    /// The event with its document serialized as the hub sends payloads, less every <c>null</c>
    /// property. A property that declares its own <see cref="JsonIgnoreCondition"/> keeps it.
    /// </summary>
    /// <param name="payloadOptions">
    /// <see cref="Microsoft.AspNetCore.SignalR.JsonHubProtocolOptions.PayloadSerializerOptions"/>.
    /// </param>
    internal StorageDocumentEvent OnWire(JsonSerializerOptions payloadOptions)
    {
        var options = OmittingNulls.GetValue(
            payloadOptions,
            static payload => new JsonSerializerOptions(payload)
            {
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            }
        );

        return this with { Doc = JsonSerializer.SerializeToElement(Doc, Doc.GetType(), options) };
    }
}
