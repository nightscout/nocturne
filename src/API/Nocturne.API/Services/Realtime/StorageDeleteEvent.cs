namespace Nocturne.API.Services.Realtime;

/// <summary>
/// Builds the payloads passed to <see cref="ISignalRBroadcastService.BroadcastStorageDeleteAsync"/>.
/// </summary>
/// <remarks>
/// NS v3 socket clients read <c>colName</c> and <c>identifier</c> from the top level of the event and
/// never look inside <c>doc</c>; the reference Nightscout server emits exactly those two keys
/// (<c>lib/api3/generic/delete/operation.js</c>). Clients match the identifier against the id they
/// already hold for the record, so it has to be byte-identical to the one the same collection's
/// create broadcast emits — for the legacy collections that means the coerced form
/// (<see cref="Nocturne.Core.Models.MongoObjectId"/>), for the Nocturne-native ones the uuid verbatim.
/// Deciding that belongs to the producer, which knows its own collection.
/// </remarks>
internal static class StorageDeleteEvent
{
    /// <summary>Builds the delete event for a single removed record.</summary>
    /// <param name="identifier">The record id as this collection puts it on the wire.</param>
    /// <param name="doc">The removed record. Carried for clients that need the body; NS v3 clients ignore it.</param>
    internal static object ForRecord(string collectionName, string? identifier, object? doc = null) =>
        new
        {
            colName = collectionName,
            identifier,
            doc,
        };

    /// <summary>Builds the delete event for a range or predicate delete.</summary>
    /// <remarks>
    /// No identifier is sent. The v3 socket contract has no bulk form — v3 DELETE addresses one
    /// identifier at a time — and the callers that do hold the removed ids hold an unbounded set of
    /// them, so fanning out would trade one event for a broadcast storm. Clients reconcile a bulk
    /// delete on their next catch-up load.
    /// </remarks>
    internal static object ForBulk(string collectionName, long deletedCount) =>
        new { colName = collectionName, deletedCount };
}
