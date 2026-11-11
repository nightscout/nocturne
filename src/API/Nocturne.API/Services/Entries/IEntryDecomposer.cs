using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.Entries;

/// <summary>
/// Decomposes legacy Entry records into v4 granular models (SensorGlucose, MeterGlucose, Calibration):
/// an upload creates or updates by legacy id, an edit writes onto the stored reading its id names.
/// </summary>
internal interface IEntryDecomposer
{
    /// <summary>
    /// Writes a user's edit of the stored reading <paramref name="entry"/>'s id names, as a batch of
    /// one: by its legacy id, or, for a reading served under its own id, onto that reading.
    /// </summary>
    /// <param name="entry">The edited legacy Entry.</param>
    /// <param name="origin">Write classification of the written records.</param>
    /// <param name="ct">Cancels the operation.</param>
    /// <returns>
    /// A <see cref="DecompositionResult"/> containing the created or updated v4 record.
    /// Returns an empty result if the entry type is unrecognized.
    /// </returns>
    Task<DecompositionResult> DecomposeAsync(Entry entry, WriteOrigin origin, CancellationToken ct = default);

    /// <summary>
    /// Deletes all v4 records that were decomposed from a legacy Entry with the given ID.
    /// </summary>
    /// <param name="legacyId">The legacy Entry ID</param>
    /// <param name="origin">Write classification, as for <see cref="DecomposeAsync"/>.</param>
    /// <param name="ct">Cancels the operation.</param>
    /// <returns>Total number of v4 records deleted across all tables</returns>
    Task<int> DeleteByLegacyIdAsync(string legacyId, WriteOrigin origin, CancellationToken ct = default);

    /// <summary>
    /// Deletes the stored entry <paramref name="stored"/>: every v4 record sharing its legacy id, or
    /// only <paramref name="stored"/> itself when it has none.
    /// </summary>
    /// <param name="stored">A stored <see cref="SensorGlucose"/>, <see cref="MeterGlucose"/> or
    /// <see cref="Calibration"/>.</param>
    /// <param name="origin">Write classification, as for <see cref="DecomposeAsync"/>.</param>
    /// <param name="ct">Cancels the operation.</param>
    /// <returns>Total number of v4 records deleted; 0 when none was still stored.</returns>
    Task<int> DeleteStoredAsync(IV4Record stored, WriteOrigin origin, CancellationToken ct = default);

    /// <summary>
    /// Bulk-deletes V4 records matching the given MongoDB-style find query.
    /// Parses time bounds from the find JSON and deletes across all glucose repositories.
    /// </summary>
    /// <param name="find">Optional MongoDB-style find query for time-range extraction.</param>
    /// <param name="origin">Write classification, as for <see cref="DecomposeAsync"/>.</param>
    /// <param name="ct">Cancels the operation.</param>
    /// <returns>Total number of records deleted across all V4 tables.</returns>
    Task<long> BulkDeleteAsync(string? find, WriteOrigin origin, CancellationToken ct = default);

    /// <summary>
    /// Decomposes a batch of uploaded legacy entries into v4 records with a fixed number of queries
    /// whatever its size, except the read of the rows its served-shaped ids name, one statement per 50
    /// such ids per table. Each legacy id keeps the correlation id its stored rows carry, else is given
    /// a fresh one shared by its re-sends in the batch.
    /// </summary>
    /// <param name="entries">The legacy entries to decompose.</param>
    /// <param name="origin">Write classification, as for <see cref="DecomposeAsync"/>.</param>
    /// <param name="ct">Cancels the operation.</param>
    /// <returns>
    /// A <see cref="DecompositionResult"/> with the records created, updated and left unchanged, whose
    /// <see cref="DecompositionResult.CorrelationId"/> is the first entry's. Entries with unrecognised
    /// types are skipped.
    /// </returns>
    Task<DecompositionResult> DecomposeBatchAsync(
        IReadOnlyList<Entry> entries, WriteOrigin origin, CancellationToken ct = default);
}
