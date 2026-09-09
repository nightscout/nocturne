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
    /// <returns>The inserted records with server-assigned fields populated.</returns>
    Task<IEnumerable<TRecord>> BulkCreateAsync(
        IEnumerable<TRecord> records, WriteOrigin origin, CancellationToken ct = default);
}

/// <summary>
/// A V4 repository addressable by the legacy MongoDB <c>_id</c> its records were decomposed from.
/// This is the surface the decomposers upsert through, so their create-or-update body can live in
/// one generic place (<c>DecomposerBase.UpsertByLegacyIdAsync</c>).
/// </summary>
/// <typeparam name="TRecord">The V4 record type stored by this repository.</typeparam>
/// <summary>
///     One record's outcome from <see cref="ILegacyKeyedRepository{TRecord}.BulkUpsertByLegacyIdAsync"/>:
///     the persisted record and whether it was inserted rather than updated in place.
/// </summary>
public sealed record LegacyUpsert<TRecord>(TRecord Record, bool Created);

public interface ILegacyKeyedRepository<TRecord> : IV4Repository<TRecord>, IBulkCreateRepository<TRecord>
    where TRecord : class, IV4Record
{
    /// <summary>
    ///     Create-or-update every record under its <see cref="IV4Record.LegacyId"/> in one context: one
    ///     query for the stored rows, one for the identities that block re-creation, one save. A stored
    ///     row is updated in place (its <see cref="IV4Record.Id"/> is written back onto the record); a
    ///     record with no stored row is inserted; a record whose identity is already held is dropped and
    ///     absent from the result, the outcome <see cref="IV4Repository{T}.CreateAsync"/> reports as
    ///     <see cref="RecreationBlockedException"/>. Records without a legacy id are ignored, and a legacy
    ///     id repeated in the batch keeps its last record.
    /// </summary>
    /// <param name="preserveStoredCorrelationId">
    ///     Whether a stored, non-empty correlation id outlives the record's own. Only an anchor record
    ///     whose group is then stamped from what it reads back may ask for this.
    /// </param>
    /// <returns>The outcomes keyed by legacy id.</returns>
    Task<IReadOnlyDictionary<string, LegacyUpsert<TRecord>>> BulkUpsertByLegacyIdAsync(
        IReadOnlyList<TRecord> records,
        WriteOrigin origin,
        bool preserveStoredCorrelationId = false,
        CancellationToken ct = default);

    Task<TRecord?> GetByLegacyIdAsync(string legacyId, CancellationToken ct = default);

    /// <returns>Number of records deleted.</returns>
    Task<int> DeleteByLegacyIdAsync(string legacyId, WriteOrigin origin, CancellationToken ct = default);
}
