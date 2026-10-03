using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Core.Contracts.V4;

namespace Nocturne.Core.Contracts.V4.Repositories;

/// <summary>
/// Batch insert for one record type, deduplicated by the repository's own key.
/// Separate from <see cref="ILegacyKeyedRepository{TRecord}"/> because
/// <see cref="Nocturne.Core.Models.V4.DeviceStatusExtras"/>,
/// <see cref="Nocturne.Core.Models.V4.BasalInjection"/> and
/// <see cref="Nocturne.Core.Models.V4.TempBasal"/> take bulk writes without carrying the full
/// legacy-keyed surface.
/// </summary>
/// <typeparam name="TRecord">The record type stored by this repository.</typeparam>
public interface IBulkCreateRepository<TRecord>
{
    /// <returns>
    /// The written records with server-assigned fields populated, as a <see cref="BulkWrite{TRecord}"/>
    /// carrying how many records were withheld because the user had deleted them.
    /// </returns>
    Task<BulkWrite<TRecord>> BulkCreateAsync(
        IEnumerable<TRecord> records, WriteOrigin origin, CancellationToken ct = default);
}

/// <summary>
/// Batch create-or-update for one record type: the batch twin of looking a record up by its
/// <see cref="IV4Record.LegacyId"/> and then updating the stored row or creating a new one, which
/// is what the decomposers' single-record path does.
/// </summary>
/// <typeparam name="TRecord">The record type stored by this repository.</typeparam>
public interface IBulkUpsertRepository<TRecord>
{
    /// <summary>
    /// Updates in place every record whose legacy id a live stored row carries, and writes the rest
    /// as <see cref="IBulkCreateRepository{TRecord}.BulkCreateAsync"/> would, in one transaction. A
    /// legacy id repeated in the batch keeps its last record. A stored device attribution survives
    /// an update whose record carries none.
    /// </summary>
    /// <returns>
    /// The written records, with <see cref="BulkWrite{TRecord}.Updated"/> naming those that updated
    /// a stored row, and how many were withheld because the user had deleted them.
    /// </returns>
    Task<BulkWrite<TRecord>> BulkUpsertAsync(
        IEnumerable<TRecord> records, WriteOrigin origin, CancellationToken ct = default);
}

/// <summary>
/// One record's outcome from <see cref="ILegacyKeyedRepository{TRecord}.BulkUpsertByLegacyIdAsync"/>:
/// the persisted record and whether it was inserted rather than updated in place.
/// </summary>
/// <typeparam name="TRecord">The V4 record type.</typeparam>
public sealed record LegacyUpsert<TRecord>(TRecord Record, bool Created);

/// <summary>
/// A stored row's legacy id and the correlation id it carries, from
/// <see cref="ILegacyKeyedRepository{TRecord}.GetCorrelationIdsByLegacyIdAsync"/>.
/// </summary>
public sealed record LegacyCorrelation(string LegacyId, Guid CorrelationId);

/// <summary>
/// A stored legacy id and the 24-hex id a record keyed by it goes out on the wire under, from
/// <see cref="ILegacyKeyedRepository{TRecord}.ResolveUuidLegacyIdsAsync"/>,
/// <see cref="ILegacyKeyedRepository{TRecord}.ResolveHashedLegacyIdsAsync"/> and
/// <see cref="ILegacyKeyedRepository{TRecord}.ResolveKeyedOwnIdsAsync"/>.
/// </summary>
public sealed record WireLegacyId(string WireId, string LegacyId);

/// <summary>
/// An id that names a stored record with no legacy id by its own uuid
/// (<see cref="MongoObjectId.TryGetOwnIdRange"/>), from
/// <see cref="ILegacyKeyedRepository{TRecord}.FindUnkeyedOwnIdsAsync"/>, and whether Nightscout
/// write-back may have sent that record upstream, as
/// <see cref="ILegacyKeyedRepository{TRecord}.GetLegacyIdsWriteBackMaySendAsync"/> decides it.
/// </summary>
public sealed record UnkeyedOwnId(string WireId, bool WriteBackMaySend);

/// <summary>
/// A V4 repository addressable by the legacy MongoDB <c>_id</c> its records were decomposed from.
/// This is the surface the decomposers upsert through, so their create-or-update body can live in
/// one generic place (<c>DecomposerBase.UpsertByLegacyIdAsync</c> per record,
/// <see cref="BulkUpsertByLegacyIdAsync"/> or <see cref="IBulkUpsertRepository{TRecord}.BulkUpsertAsync"/>
/// per batch).
/// </summary>
/// <typeparam name="TRecord">The V4 record type stored by this repository.</typeparam>
public interface ILegacyKeyedRepository<TRecord>
    : IV4Repository<TRecord>, IBulkCreateRepository<TRecord>, IBulkUpsertRepository<TRecord>
    where TRecord : class, IV4Record
{
    /// <summary>
    /// Create-or-update every record under its <see cref="IV4Record.LegacyId"/> in one context: one
    /// query for the stored rows, one for the identities that block re-creation, one save. A stored
    /// row is updated in place (its <see cref="IV4Record.Id"/> is written back onto the record); a
    /// record with no stored row is inserted; a record whose identity is held by a row the user
    /// deleted is dropped, absent from the outcomes and counted in
    /// <see cref="LegacyUpsertBatch{TRecord}.SkippedDeleted"/>. Records without a legacy id are ignored, and a
    /// legacy id repeated in the batch keeps its last record.
    /// </summary>
    /// <remarks>
    /// The legacy id is the only identity this method matches on. The types whose creates upsert on a
    /// sync key (sensor glucose, boluses, carb intakes, the device-status snapshots) do not support it
    /// and throw <see cref="NotSupportedException"/>; their batch path is
    /// <see cref="IBulkUpsertRepository{TRecord}.BulkUpsertAsync"/>, which matches the legacy id
    /// first and the sync key after.
    /// </remarks>
    /// <param name="preserveStoredCorrelationId">
    /// Whether a stored, non-empty correlation id outlives the record's own. Only an anchor record
    /// whose group is then stamped from what it reads back may ask for this.
    /// </param>
    /// <returns>The outcomes keyed by legacy id, and the count withheld.</returns>
    Task<LegacyUpsertBatch<TRecord>> BulkUpsertByLegacyIdAsync(
        IReadOnlyList<TRecord> records,
        WriteOrigin origin,
        bool preserveStoredCorrelationId = false,
        CancellationToken ct = default);

    Task<TRecord?> GetByLegacyIdAsync(string legacyId, CancellationToken ct = default);

    /// <summary>
    /// The record whose UUID <see cref="IV4Record.LegacyId"/> <see cref="Nocturne.Core.Models.MongoObjectId.Coerce"/>
    /// turns into <paramref name="objectId"/>, its 24-hex prefix: the id a legacy create echoed
    /// before it returned the stored record's own, which clients such as Loop cache and send back
    /// on later edits and deletes.
    /// </summary>
    /// <remarks>
    /// Index range lookups over the dashed and dashless forms, each in lower and upper case. A UUID
    /// stored in mixed case is not found.
    /// </remarks>
    Task<TRecord?> GetByLegacyIdUuidPrefixAsync(string objectId, CancellationToken ct = default);

    /// <summary>
    /// The record whose non-UUID <see cref="IV4Record.LegacyId"/> <see cref="Nocturne.Core.Models.MongoObjectId.Coerce"/>
    /// hashes into <paramref name="objectId"/>, the other shape of echoed id
    /// <see cref="GetByLegacyIdUuidPrefixAsync"/> describes.
    /// </summary>
    /// <remarks>A scan that hashes every row, so it belongs behind every other lookup.</remarks>
    Task<TRecord?> GetByLegacyIdHashAsync(string objectId, CancellationToken ct = default);

    /// <summary>
    /// Whether a record the user deleted answers to <paramref name="id"/> under any key the lookups
    /// above resolve: its own id, the 24-hex prefix of that id, its legacy id, or the echo of a
    /// legacy id (<see cref="GetByLegacyIdUuidPrefixAsync"/>, <see cref="GetByLegacyIdHashAsync"/>).
    /// A record the system swept does not count.
    /// </summary>
    Task<bool> IsDeletedByUserAsync(string id, CancellationToken ct = default);

    /// <summary>
    /// Records whose server write stamp (<see cref="IV4Record.ModifiedAt"/>, reported as
    /// <c>srvModified</c>) falls after <paramref name="cursorMills"/>, oldest first, as one history
    /// page that ends on a millisecond boundary and so may exceed <paramref name="limit"/>.
    /// </summary>
    Task<IReadOnlyList<TRecord>> GetModifiedSinceAsync(long cursorMills, int limit, CancellationToken ct = default);

    /// <summary>
    /// The stored, non-empty correlation id of each live row carrying one of
    /// <paramref name="legacyIds"/>, under the same soft-delete visibility as
    /// <see cref="GetByLegacyIdAsync"/>. A legacy id with no such row is absent.
    /// </summary>
    Task<IEnumerable<LegacyCorrelation>> GetCorrelationIdsByLegacyIdAsync(
        IEnumerable<string> legacyIds, CancellationToken ct = default);

    /// <summary>
    /// The ids among <paramref name="legacyIds"/> a re-upload would find held, from any source: by a
    /// live record, or by one the user deleted.
    /// </summary>
    Task<IReadOnlySet<string>> GetHeldLegacyIdsAsync(
        IReadOnlyCollection<string> legacyIds, CancellationToken ct = default);

    /// <summary>
    /// The ids among <paramref name="legacyIds"/> held, as <see cref="GetHeldLegacyIdsAsync"/> holds
    /// them, by a record Nightscout write-back may have sent upstream: one written by a live write at
    /// least once, and whose <see cref="IV4Record.DataSource"/> is not <paramref name="skippedSource"/>,
    /// the source write-back skips. A record only ever written by an import (a Nightscout migration,
    /// a connector's initial backfill) was never offered to write-back. A live record governs over
    /// the user's deletion of the same id, and the latest of several deletions governs over the rest.
    /// </summary>
    Task<IEnumerable<string>> GetLegacyIdsWriteBackMaySendAsync(
        IReadOnlyCollection<string> legacyIds, string skippedSource, CancellationToken ct = default);

    /// <summary>
    /// The ids among <paramref name="ids"/> that name a stored record with no legacy id, live or
    /// deleted, by <see cref="MongoObjectId.TryGetOwnIdRange"/>: the read-only half of
    /// <see cref="AdoptOwnIdsAsync"/>, which may then give each record that id. An id a live record
    /// already carries as its legacy id is left out, as <see cref="AdoptOwnIdsAsync"/> leaves it.
    /// </summary>
    /// <param name="skippedSource">The source write-back skips, as in <see cref="GetLegacyIdsWriteBackMaySendAsync"/>.</param>
    Task<IEnumerable<UnkeyedOwnId>> FindUnkeyedOwnIdsAsync(
        IReadOnlyCollection<string> ids, string skippedSource, CancellationToken ct = default);

    /// <summary>
    /// Gives each stored record that has no legacy id, live or deleted, the id among
    /// <paramref name="ids"/> that names it by <see cref="MongoObjectId.TryGetOwnIdRange"/>, as its
    /// legacy id.
    /// </summary>
    /// <remarks>
    /// Such a record goes out on the wire, and to an upstream Nightscout through write-back, under
    /// its own uuid or that uuid's 24-hex prefix. Every ingest path matches a returning record by
    /// legacy id alone, so without this the copy a pull brings back is stored a second time, and the
    /// copy of a record the user deleted is stored again. Once adopted, the legacy id updates the live
    /// record in place and the user's deletion holds it off. An id a live record already carries as
    /// its legacy id is left alone: that record is the match.
    /// </remarks>
    /// <returns>The records that took an id, carrying it.</returns>
    Task<IEnumerable<TRecord>> AdoptOwnIdsAsync(IReadOnlyCollection<string> ids, CancellationToken ct = default);

    /// <summary>
    /// Gives each stored record that has no legacy id, live or deleted, and carries one of the
    /// correlation ids in <paramref name="legacyIdByCorrelation"/>, the legacy id that correlation id
    /// maps to: the siblings of a group whose anchor took its id through <see cref="AdoptOwnIdsAsync"/>.
    /// </summary>
    /// <returns>How many records took an id.</returns>
    Task<int> AdoptLegacyIdsByCorrelationAsync(
        IReadOnlyDictionary<Guid, string> legacyIdByCorrelation, CancellationToken ct = default);

    /// <summary>
    /// The stored legacy ids, live or deleted, that are uuids whose 24-hex prefix
    /// (<see cref="MongoObjectId.FromGuid"/>) is one of <paramref name="ids"/>, each paired with that
    /// prefix.
    /// </summary>
    /// <remarks>
    /// Write-back sends a uuid-shaped legacy id upstream as that prefix, the only id form an AAPS
    /// client reading the upstream instance accepts, so the copy a pull brings back names the record
    /// by the prefix rather than by its legacy id. A caller rewrites the incoming id to the legacy id
    /// before its legacy-id upsert, which then matches the record, or finds the user's deletion.
    /// </remarks>
    Task<IEnumerable<WireLegacyId>> ResolveUuidLegacyIdsAsync(
        IReadOnlyCollection<string> ids, CancellationToken ct = default);

    /// <summary>
    /// The stored legacy ids, live or deleted, that are neither ObjectIds nor uuids and whose hash
    /// (<see cref="MongoObjectId.Coerce"/>) is one of <paramref name="ids"/>, each paired with it.
    /// </summary>
    /// <remarks>
    /// Treatment write-back sends such a legacy id upstream as that hash, as <c>_id</c> and
    /// <c>identifier</c> alike, so the copy a pull brings back names the record by the hash alone.
    /// </remarks>
    Task<IEnumerable<WireLegacyId>> ResolveHashedLegacyIdsAsync(
        IReadOnlyCollection<string> ids, CancellationToken ct = default);

    /// <summary>
    /// The stored legacy ids, live or deleted, of the records that carry one and whose own uuid, in
    /// canonical form or as its 24-hex prefix (<see cref="MongoObjectId.TryGetOwnIdRange"/>), is one
    /// of <paramref name="ids"/>, each paired with that id.
    /// </summary>
    /// <remarks>
    /// The v1 and v3 reads serve a treatment under its uuid's prefix whatever its legacy id, and
    /// treatment write-back sent edits under the id they were served by: the full uuid up to v0.2.3,
    /// its prefix from v0.2.4. A copy upstream may name the record by either.
    /// </remarks>
    Task<IEnumerable<WireLegacyId>> ResolveKeyedOwnIdsAsync(
        IReadOnlyCollection<string> ids, CancellationToken ct = default);

    /// <returns>Number of records deleted.</returns>
    Task<int> DeleteByLegacyIdAsync(string legacyId, WriteOrigin origin, CancellationToken ct = default);
}
