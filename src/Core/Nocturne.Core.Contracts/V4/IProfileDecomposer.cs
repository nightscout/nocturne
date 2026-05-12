using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.Core.Contracts.V4;

/// <summary>
/// Decomposes legacy Profile records into V4 granular models (TherapySettings, BasalSchedule,
/// CarbRatioSchedule, SensitivitySchedule, TargetRangeSchedule).
/// Iterates through the profile's Store dictionary, producing one set of V4 records per named profile.
/// Handles idempotent create-or-update based on composite LegacyId matching.
/// </summary>
/// <seealso cref="IDecompositionPipeline"/>
/// <seealso cref="IEntryDecomposer"/>
/// <seealso cref="ITreatmentDecomposer"/>
public interface IProfileDecomposer
{
    /// <summary>
    /// Decomposes a legacy Profile into V4 records for each named profile in its Store.
    /// </summary>
    /// <param name="profile">The legacy Profile to decompose</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>
    /// A <see cref="DecompositionResult"/> containing all created or updated V4 records.
    /// A single Profile with N named stores produces N sets of 5 records each.
    /// </returns>
    Task<DecompositionResult> DecomposeAsync(Profile profile, CancellationToken ct = default);

    /// <summary>
    /// Decomposes a batch of legacy Profiles into V4 records, using bulk insert for each schedule type.
    /// Flattens all Store entries across all profiles into per-type lists and bulk-creates them.
    /// </summary>
    /// <param name="profiles">The legacy Profiles to decompose</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>
    /// A <see cref="DecompositionResult"/> containing all created V4 records across all profiles.
    /// </returns>
    Task<DecompositionResult> DecomposeBatchAsync(
        IReadOnlyList<Profile> profiles, CancellationToken ct = default);

    /// <summary>
    /// Deletes all V4 records that were decomposed from a legacy Profile with the given ID.
    /// Uses prefix matching since one legacy Profile fans out to multiple composite LegacyIds.
    /// </summary>
    /// <param name="legacyId">The legacy Profile._id</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Total number of V4 records deleted across all tables</returns>
    Task<int> DeleteByLegacyIdAsync(string legacyId, CancellationToken ct = default);
}
