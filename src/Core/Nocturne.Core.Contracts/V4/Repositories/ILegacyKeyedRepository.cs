using Nocturne.Core.Models.Queries;
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
    /// page that ends on a millisecond boundary and so may exceed <paramref name="limit"/>. Deleted
    /// records are included, stamped with their delete (<see cref="HistoryRecord{T}"/>).
    /// </summary>
    Task<IReadOnlyList<HistoryRecord<TRecord>>> GetModifiedSinceAsync(long cursorMills, int limit, CancellationToken ct = default);

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

    /// <returns>Number of records deleted.</returns>
    Task<int> DeleteByLegacyIdAsync(string legacyId, WriteOrigin origin, CancellationToken ct = default);
}
