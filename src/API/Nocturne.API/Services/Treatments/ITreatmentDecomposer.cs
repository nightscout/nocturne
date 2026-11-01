using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4;

namespace Nocturne.API.Services.Treatments;

/// <summary>
/// Decomposes legacy Treatment records into v4 granular models (Bolus, CarbIntake, BGCheck, Note,
/// BolusCalculation, DeviceEvent, TempBasal, ProfileActivation) and writes the state spans of overrides,
/// temporary targets and pump suspensions through <see cref="IStateSpanService"/>.
/// Handles idempotent create-or-update based on LegacyId matching.
/// </summary>
/// <seealso cref="IStateSpanService"/>
internal interface ITreatmentDecomposer
{
    /// <summary>
    /// Decomposes a batch of legacy Treatments into v4 records with a fixed number of queries whatever
    /// the batch's size.
    /// </summary>
    /// <param name="treatments">The batch of legacy Treatments to decompose, in the order they were sent.</param>
    /// <param name="into">
    /// The decomposition to fill as each write commits, so a caller still reads what committed when the
    /// batch throws part-way.
    /// </param>
    /// <param name="origin">
    /// A <see cref="WriteOrigin.Live"/> write keeps the caller's attribution; a backfill's is
    /// system-attributed (<see cref="V4.DecomposerBase.SystemAttributedBatchWrites"/>).
    /// </param>
    /// <returns>What the batch wrote, and what each treatment of it wrote.</returns>
    Task<TreatmentDecomposition> DecomposeBatchAsync(
        IReadOnlyList<Treatment> treatments, WriteOrigin origin, CancellationToken ct = default,
        TreatmentDecomposition? into = null);

    /// <summary>
    /// Deletes all v4 records that were decomposed from a legacy Treatment with the given ID, and
    /// every other source's copy deduplication linked them to: a user deleting a treatment deletes
    /// the event, not one source's report of it.
    /// </summary>
    /// <param name="legacyId">The legacy Treatment ID</param>
    /// <returns>Total number of v4 records deleted across all tables</returns>
    Task<int> DeleteByLegacyIdAsync(string legacyId, WriteOrigin origin, CancellationToken ct = default);

    /// <summary>
    /// The subset of <paramref name="legacyIds"/> held only by records the user deleted, which a
    /// re-upload under that id does not bring back.
    /// </summary>
    Task<IReadOnlySet<string>> GetDeletedByUserAsync(IReadOnlyCollection<string> legacyIds, CancellationToken ct = default);

    /// <inheritdoc cref="ITreatmentService.DeleteFromSourceAsync"/>
    Task<int> DeleteFromSourceAsync(string source, IReadOnlySet<string> legacyIds, CancellationToken ct = default);

    /// <inheritdoc cref="ITreatmentService.GetStoredIdsFromSourceAsync"/>
    Task<IReadOnlyDictionary<string, DateTime>> GetLegacyIdsFromSourceAsync(
        string source, DateTime from, DateTime to, CancellationToken ct = default);

    /// <inheritdoc cref="ITreatmentService.RekeyClientIdRecordsAsync"/>
    Task<int> RekeyClientIdRecordsAsync(
        string source, IReadOnlyList<Treatment> treatments, CancellationToken ct = default);

    /// <inheritdoc cref="ITreatmentService.SelectForRepublishAsync"/>
    Task<IReadOnlyList<Treatment>> SelectForRepublishAsync(
        string source, IReadOnlyList<Treatment> treatments, CancellationToken ct = default);

    /// <summary>
    /// Bulk-deletes V4 treatment records matching the optional find filter (time range).
    /// </summary>
    /// <param name="find">Optional Nightscout-compatible find query for time range filtering.</param>
    /// <returns>Total number of V4 records deleted across all treatment tables.</returns>
    Task<long> BulkDeleteAsync(string? find, WriteOrigin origin, CancellationToken ct = default);
}
