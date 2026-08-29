using Nocturne.Core.Models;

namespace Nocturne.API.Services.Realtime;

/// <summary>
/// Builds the payloads passed to <see cref="ISignalRBroadcastService.BroadcastStorageDeleteAsync"/>.
/// </summary>
/// <remarks>
/// NS v3 socket clients read <c>colName</c> and <c>identifier</c> from the top level of the event and
/// never look inside <c>doc</c>; the reference Nightscout server emits exactly those two keys
/// (<c>lib/api3/generic/delete/operation.js</c>). <c>identifier</c> is matched against the record id
/// the client stored, so it is coerced to the 24-hex form the legacy wire uses everywhere else —
/// see <see cref="MongoObjectId.Coerce"/>, which is idempotent for real ObjectIds and reversible for
/// Nocturne UUIDs.
/// </remarks>
internal static class StorageDeleteEvent
{
    /// <summary>Builds the delete event for a single removed record.</summary>
    /// <param name="collectionName">The collection the record belonged to.</param>
    /// <param name="id">The record id, in whatever form the producer holds it.</param>
    /// <param name="doc">The removed record. Carried for clients that need the body; NS v3 clients ignore it.</param>
    internal static object ForRecord(string collectionName, string? id, object? doc = null) =>
        new
        {
            colName = collectionName,
            identifier = MongoObjectId.Coerce(id),
            doc,
        };

    /// <summary>Builds the delete event for a range or predicate delete.</summary>
    /// <remarks>
    /// There is no identifier to send: the removed ids are never materialised by any caller, and the
    /// v3 socket contract has no bulk form because v3 DELETE addresses one identifier at a time.
    /// Clients reconcile a bulk delete on their next catch-up load.
    /// </remarks>
    /// <param name="collectionName">The collection the records belonged to.</param>
    /// <param name="deletedCount">How many records were removed.</param>
    internal static object ForBulk(string collectionName, long deletedCount) =>
        new { colName = collectionName, deletedCount };
}
