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
    Task<DecompositionResult> DecomposeAsync(Profile profile, WriteOrigin origin, CancellationToken ct = default);

    /// <summary>
    /// Decomposes a batch of legacy Profiles with one create-or-update round per V4 table, so a
    /// connector re-publishing its profile set costs five table round trips rather than ten per named
    /// profile. Same records, same idempotency, as <see cref="DecomposeAsync"/> per profile.
    /// </summary>
    /// <param name="profiles">The legacy Profiles to decompose</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>
    /// A single <see cref="DecompositionResult"/> containing every created or updated V4 record; its
    /// <see cref="DecompositionResult.CorrelationId"/> is the id minted for the first profile that has
    /// a store entry.
    /// </returns>
    Task<DecompositionResult> DecomposeBatchAsync(
        IReadOnlyList<Profile> profiles, WriteOrigin origin, CancellationToken ct = default);

    /// <summary>
    /// Decomposes the profile a Profile Switch treatment carried inline, as <see cref="DecomposeAsync"/>
    /// does, except that it never touches the tenant's default profile: its rows are written with
    /// <see cref="TherapySettings.IsDefault"/> false and it neither claims nor clears the default.
    /// </summary>
    /// <remarks>
    /// In Nightscout a profile switch never changes the profile collection's <c>defaultProfile</c>.
    /// </remarks>
    /// <param name="profile">The synthetic profile built from the switch.</param>
    /// <param name="ct">Cancellation token</param>
    Task<DecompositionResult> DecomposeProfileSwitchAsync(
        Profile profile, WriteOrigin origin, CancellationToken ct = default);

    /// <summary>
    /// Deletes all V4 records that were decomposed from a legacy Profile with the given ID.
    /// Uses prefix matching since one legacy Profile fans out to multiple composite LegacyIds.
    /// </summary>
    /// <param name="legacyId">The legacy Profile._id</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Total number of V4 records deleted across all tables</returns>
    Task<int> DeleteByLegacyIdAsync(string legacyId, WriteOrigin origin, CancellationToken ct = default);

    /// <summary>
    /// Replaces the stored profile document <paramref name="profile"/> names by its <c>_id</c> with
    /// the stores it carries, inserting it when none is stored, and deletes the stored stores it no
    /// longer carries.
    /// </summary>
    /// <returns>
    /// <c>false</c>, deleting nothing, when the document carries no store or a store was not written
    /// because a user deleted that identity.
    /// </returns>
    Task<bool> ReplaceDocumentAsync(Profile profile, WriteOrigin origin, CancellationToken ct = default);

    /// <summary>
    /// Deletes the profile document that answers to <paramref name="documentId"/>, as
    /// <see cref="TherapySettings.DocumentIdOf"/> resolves it, including one served under a row id.
    /// </summary>
    /// <returns>Total number of V4 records deleted across all tables</returns>
    Task<int> DeleteDocumentAsync(string documentId, WriteOrigin origin, CancellationToken ct = default);
}
