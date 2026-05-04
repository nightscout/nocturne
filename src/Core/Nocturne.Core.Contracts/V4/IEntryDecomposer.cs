using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.Core.Contracts.V4;

/// <summary>
/// Decomposes legacy Entry records into v4 granular models (SensorGlucose, MeterGlucose, Calibration).
/// Handles idempotent create-or-update based on LegacyId matching.
/// </summary>
/// <seealso cref="IDecompositionPipeline"/>
/// <seealso cref="ITreatmentDecomposer"/>
/// <seealso cref="IProfileDecomposer"/>
/// <seealso cref="IDeviceStatusDecomposer"/>
public interface IEntryDecomposer
{
    /// <summary>
    /// Decomposes a single legacy Entry into the appropriate v4 record type
    /// based on <see cref="Entry.Type"/>.
    /// </summary>
    /// <param name="entry">The legacy Entry to decompose</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>
    /// A <see cref="DecompositionResult"/> containing the created or updated v4 record.
    /// Returns an empty result if the entry type is unrecognized.
    /// </returns>
    Task<DecompositionResult> DecomposeAsync(Entry entry, CancellationToken ct = default);

    /// <summary>
    /// Deletes all v4 records that were decomposed from a legacy Entry with the given ID.
    /// </summary>
    /// <param name="legacyId">The legacy Entry ID</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Total number of v4 records deleted across all tables</returns>
    Task<int> DeleteByLegacyIdAsync(string legacyId, CancellationToken ct = default);

    /// <summary>
    /// Bulk-deletes V4 records matching the given MongoDB-style find query.
    /// Parses time bounds from the find JSON and deletes across all glucose repositories.
    /// </summary>
    /// <param name="find">Optional MongoDB-style find query for time-range extraction.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Total number of records deleted across all V4 tables.</returns>
    Task<long> BulkDeleteAsync(string? find, CancellationToken ct = default);
}
