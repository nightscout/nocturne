using Nocturne.Core.Models.V4;

namespace Nocturne.Core.Contracts.V4;

/// <summary>
/// Unified generic interface for decomposing legacy records into v4 granular models.
/// Implemented by each typed decomposer alongside its specific interface.
/// </summary>
/// <typeparam name="T">The legacy domain model type (Entry, Treatment, DeviceStatus, Activity, Profile).</typeparam>
/// <seealso cref="IDecompositionPipeline"/>
/// <seealso cref="IEntryDecomposer"/>
/// <seealso cref="ITreatmentDecomposer"/>
/// <seealso cref="IDeviceStatusDecomposer"/>
/// <seealso cref="IActivityDecomposer"/>
/// <seealso cref="IProfileDecomposer"/>
public interface IDecomposer<in T> where T : class
{
    /// <summary>
    /// Decomposes a single legacy record into one or more v4 granular records,
    /// performing idempotent create-or-update based on LegacyId matching.
    /// </summary>
    /// <param name="record">The legacy record to decompose.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="DecompositionResult"/> containing the created or updated v4 records.</returns>
    Task<DecompositionResult> DecomposeAsync(T record, WriteOrigin origin, CancellationToken ct = default);

    /// <summary>
    /// Deletes all v4 records that were previously decomposed from the legacy record
    /// with the specified identifier.
    /// </summary>
    /// <param name="legacyId">The legacy record identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The total number of v4 records deleted across all target tables.</returns>
    Task<int> DeleteByLegacyIdAsync(string legacyId, WriteOrigin origin, CancellationToken ct = default);
}
