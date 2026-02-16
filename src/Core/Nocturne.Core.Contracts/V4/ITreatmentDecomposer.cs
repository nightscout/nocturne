using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.Core.Contracts.V4;

/// <summary>
/// Decomposes legacy Treatment records into v4 granular models (Bolus, CarbIntake, BGCheck, Note, BolusCalculation)
/// and delegates StateSpan-backed types (TempBasal, ProfileSwitch) to IStateSpanService.
/// Handles idempotent create-or-update based on LegacyId matching.
/// </summary>
public interface ITreatmentDecomposer
{
    /// <summary>
    /// Decomposes a single legacy Treatment into the appropriate v4 record types
    /// based on <see cref="Treatment.EventType"/> and presence of insulin/carb data.
    /// </summary>
    /// <param name="treatment">The legacy Treatment to decompose</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>
    /// A <see cref="DecompositionResult"/> containing the created or updated v4 records.
    /// A single Treatment may produce multiple records (e.g., Bolus + CarbIntake for a Meal Bolus).
    /// Returns an empty result if the event type is unrecognized and no insulin/carbs are present.
    /// </returns>
    Task<DecompositionResult> DecomposeAsync(Treatment treatment, CancellationToken ct = default);

    /// <summary>
    /// Deletes all v4 records that were decomposed from a legacy Treatment with the given ID.
    /// </summary>
    /// <param name="legacyId">The legacy Treatment ID</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Total number of v4 records deleted across all tables</returns>
    Task<int> DeleteByLegacyIdAsync(string legacyId, CancellationToken ct = default);
}
