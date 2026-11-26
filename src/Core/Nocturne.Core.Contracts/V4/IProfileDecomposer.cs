using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.Core.Contracts.V4;

/// <summary>
/// Decomposes legacy Profile records into V4 granular models (TherapySettings, BasalSchedule,
/// CarbRatioSchedule, SensitivitySchedule, TargetRangeSchedule).
/// Iterates through the profile's Store dictionary, producing one set of V4 records per named profile.
/// Handles idempotent create-or-update based on composite LegacyId matching.
/// </summary>
public interface IProfileDecomposer
{
    /// <summary>
    /// Decomposes a batch of legacy Profiles into V4 records for each named profile in each Store, with
    /// one create-or-update round per V4 table whatever the batch's size. A single profile is a batch
    /// of one.
    /// </summary>
    /// <param name="profiles">The legacy Profiles to decompose</param>
    /// <remarks>
    /// A document sent with <c>isValid: false</c> writes nothing live: under the id of a live document
    /// it deletes that document, and otherwise it is stored deleted, so only v3 history serves it. A
    /// later decomposition of the same document without <c>isValid</c> writes it again.
    /// </remarks>
    /// <returns>
    /// A single <see cref="InvalidatingDecomposition{TRecord}"/> containing every created or updated V4 record; its
    /// <see cref="DecompositionResult.CorrelationId"/> is the id minted for the first profile that has
    /// a store entry.
    /// </returns>
    Task<InvalidatingDecomposition<Profile>> DecomposeBatchAsync(
        IReadOnlyList<Profile> profiles, WriteOrigin origin, CancellationToken ct = default);

    /// <summary>
    /// Deletes all V4 records that were decomposed from a legacy Profile with the given ID.
    /// Uses prefix matching since one legacy Profile fans out to multiple composite LegacyIds.
    /// </summary>
    /// <param name="legacyId">The legacy Profile._id</param>
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
