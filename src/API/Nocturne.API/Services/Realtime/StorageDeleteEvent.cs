using System.Text.Json;

namespace Nocturne.API.Services.Realtime;

/// <summary>
/// Builds the payloads passed to <see cref="ISignalRBroadcastService.BroadcastStorageDeleteAsync"/>.
/// </summary>
/// <remarks>
/// NS v3 socket clients read <c>colName</c> and <c>identifier</c> from the top level of the event and
/// never look inside <c>doc</c>; the reference Nightscout server emits exactly those two keys
/// (<c>lib/api3/generic/delete/operation.js</c>). Clients match the identifier against the id their
/// create event delivered, and the models disagree on what that is — <see cref="Nocturne.Core.Models.Treatment"/>
/// coerces its id to a Mongo ObjectId on the wire, <see cref="Nocturne.Core.Models.Entry"/> and
/// <see cref="Nocturne.Core.Models.DeviceStatus"/> emit the uuid. Reading the identifier back out of the
/// serialized document keeps delete and create in step for any model rather than restating each model's
/// choice at the call site.
/// </remarks>
internal static class StorageDeleteEvent
{
    /// <summary>Builds the delete event for a single removed record.</summary>
    /// <param name="collectionName">The collection the record belonged to.</param>
    /// <param name="id">The record id. Used only when there is no <paramref name="doc"/> to read it from.</param>
    /// <param name="doc">The removed record. Carried for clients that need the body; NS v3 clients ignore it.</param>
    internal static object ForRecord(string collectionName, string? id, object? doc = null) =>
        new
        {
            colName = collectionName,
            identifier = doc is null ? id : WireIdentifier(doc) ?? id,
            doc,
        };

    /// <summary>Builds the delete event for a range or predicate delete.</summary>
    /// <remarks>
    /// No identifier is sent. The v3 socket contract has no bulk form — v3 DELETE addresses one
    /// identifier at a time — and the callers that do hold the removed ids hold an unbounded set of
    /// them, so fanning out would trade one event for a broadcast storm. Clients reconcile a bulk
    /// delete on their next catch-up load.
    /// </remarks>
    /// <param name="collectionName">The collection the records belonged to.</param>
    /// <param name="deletedCount">How many records were removed.</param>
    internal static object ForBulk(string collectionName, long deletedCount) =>
        new { colName = collectionName, deletedCount };

    /// <summary>
    /// The id as <paramref name="doc"/> itself puts it on the wire, preferring the v3 <c>identifier</c>
    /// over the legacy <c>_id</c>. Returns null for a document that carries neither.
    /// </summary>
    private static string? WireIdentifier(object doc)
    {
        var root = JsonSerializer.SerializeToElement(doc);

        return Read(root, "identifier") ?? Read(root, "_id");

        static string? Read(JsonElement root, string property) =>
            root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
    }
}
