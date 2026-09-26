using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Core.Contracts.Glucose;

namespace Nocturne.Core.Contracts.V4;

/// <summary>
/// Decomposes legacy Treatment records into v4 granular models (Bolus, CarbIntake, BGCheck, Note, BolusCalculation)
/// and delegates StateSpan-backed types (TempBasal, ProfileSwitch) to <see cref="IStateSpanService"/>.
/// Handles idempotent create-or-update based on LegacyId matching.
/// </summary>
/// <seealso cref="IDecompositionPipeline"/>
/// <seealso cref="IEntryDecomposer"/>
/// <seealso cref="IStateSpanService"/>
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
    Task<DecompositionResult> DecomposeAsync(Treatment treatment, WriteOrigin origin, CancellationToken ct = default);

    /// <summary>
    /// Decomposes a batch of legacy Treatments into v4 records using bulk-insert operations,
    /// eliminating per-record DB round-trips. State span treatments (TempBasal, ProfileSwitch,
    /// Override, TemporaryTarget) are upserted individually since they require idempotent semantics.
    /// Bolus-to-BolusCalculation linking is performed in a post-insert pass.
    /// </summary>
    /// <param name="treatments">The batch of legacy Treatments to decompose.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A single <see cref="DecompositionResult"/> containing all created v4 records.</returns>
    Task<DecompositionResult> DecomposeBatchAsync(
        IReadOnlyList<Treatment> treatments, WriteOrigin origin, CancellationToken ct = default);

    /// <summary>
    /// Deletes all v4 records that were decomposed from a legacy Treatment with the given ID.
    /// </summary>
    /// <param name="legacyId">The legacy Treatment ID</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Total number of v4 records deleted across all tables</returns>
    Task<int> DeleteByLegacyIdAsync(string legacyId, WriteOrigin origin, CancellationToken ct = default);

    /// <summary>
    /// Soft-deletes <paramref name="source"/>'s records decomposed from <paramref name="legacyIds"/>,
    /// leaving every other source's rows under those ids alone. A deleted row that was a dedup
    /// group's primary hands the flag to a surviving member, so another source's copy of the
    /// event stays visible.
    /// </summary>
    /// <returns>Total number of v4 records deleted across all tables.</returns>
    Task<int> DeleteFromSourceAsync(string source, IReadOnlySet<string> legacyIds, CancellationToken ct = default);

    /// <summary>
    /// The legacy ids of <paramref name="source"/>'s live records whose event time falls in
    /// [<paramref name="from"/>, <paramref name="to"/>], each with that event time.
    /// </summary>
    Task<IReadOnlyDictionary<string, DateTime>> GetLegacyIdsFromSourceAsync(
        string source, DateTime from, DateTime to, CancellationToken ct = default);

    /// <summary>Of treatments <paramref name="source"/> delivered again, the ones to decompose again.</summary>
    Task<IReadOnlyList<Treatment>> SelectForRepublishAsync(
        string source, IReadOnlyList<Treatment> treatments, CancellationToken ct = default);

    /// <summary>
    /// Bulk-deletes V4 treatment records matching the optional find filter (time range).
    /// </summary>
    /// <param name="find">Optional Nightscout-compatible find query for time range filtering.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Total number of V4 records deleted across all treatment tables.</returns>
    Task<long> BulkDeleteAsync(string? find, WriteOrigin origin, CancellationToken ct = default);
}
