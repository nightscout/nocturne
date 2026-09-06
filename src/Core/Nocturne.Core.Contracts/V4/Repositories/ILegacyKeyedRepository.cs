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
public interface ILegacyKeyedRepository<TRecord> : IV4Repository<TRecord>, IBulkCreateRepository<TRecord>
    where TRecord : class, IV4Record
{
    Task<TRecord?> GetByLegacyIdAsync(string legacyId, CancellationToken ct = default);

    /// <returns>Number of records deleted.</returns>
    Task<int> DeleteByLegacyIdAsync(string legacyId, WriteOrigin origin, CancellationToken ct = default);
}
