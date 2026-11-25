using Nocturne.Core.Models;
using Nocturne.Core.Contracts.Entries;
using Nocturne.Core.Contracts.Treatments;

namespace Nocturne.API.Services.Treatments;

/// <summary>
/// Projects V4 granular records (Bolus, CarbIntake, BGCheck, Note, DeviceEvent)
/// back into the legacy Treatment shape required by the v1/v2/v3 API endpoints.
/// </summary>
/// <seealso cref="IEntryService"/>
/// <seealso cref="ITreatmentService"/>
internal interface IV4ToLegacyProjectionService
{
    /// <summary>
    /// Returns a set of legacy <see cref="Treatment"/> objects synthesised from V4 treatment records
    /// (Bolus, CarbIntake, BGCheck, Note, DeviceEvent).
    /// These supplement the treatments already stored in the legacy treatments table for connectors
    /// that write V4 directly. The newest <see cref="LegacyTreatmentRange.Limit"/> treatments in the
    /// window newest first, or, when <see cref="LegacyTreatmentRange.Descending"/> is <c>false</c>, the
    /// oldest oldest first. With <see cref="LegacyTreatmentRange.IncludeDeleted"/> the rows stored
    /// deleted are read as well as the live ones, a deleted one projected with <c>isValid: false</c>
    /// and its delete as its modification stamp; the window, the limit and the order apply to live and
    /// deleted rows together.
    /// </summary>
    Task<IEnumerable<Treatment>> GetProjectedTreatmentsAsync(
        LegacyTreatmentRange range,
        CancellationToken ct = default
    );

    /// <summary>
    /// The treatments that start exactly at one of <paramref name="instants"/>, each served as a read
    /// of its instant alone serves it: a bolus and a carb intake at different instants are not paired.
    /// Each table is read once however many instants there are, and nothing between them is read.
    /// </summary>
    Task<IEnumerable<Treatment>> GetProjectedTreatmentsAtAsync(
        IReadOnlyCollection<long> instants,
        CancellationToken ct = default
    );

    /// <inheritdoc cref="ITreatmentService.GetLatestTreatmentOfTypeAsync"/>
    /// <remarks>Served by an index seek, not a scan of the window.</remarks>
    Task<Treatment?> GetLatestProjectedTreatmentAsync(
        string eventType,
        long fromMills,
        long toMills,
        CancellationToken ct = default
    );

    /// <summary>
    /// Returns legacy <see cref="Treatment"/> objects synthesised from V4 records whose system
    /// update stamp is strictly after the given threshold, oldest first. Used for v3 incremental
    /// sync, and shaped exactly as <see cref="GetProjectedTreatmentsAsync"/> shapes a time range:
    /// records of both provenances, meals paired into one treatment, carb foods populated.
    /// </summary>
    Task<IEnumerable<Treatment>> GetProjectedTreatmentsModifiedSinceAsync(
        long lastModifiedMills,
        int limit,
        CancellationToken ct = default
    );

    /// <summary>
    /// The legacy <see cref="Treatment"/> a state span decomposed from a treatment projects into, found
    /// by any id a client may hold for it, or <c>null</c> when no such span has that id. Its
    /// <see cref="Treatment.Id"/> is the span's primary key.
    /// </summary>
    Task<Treatment?> GetProjectedStateSpanTreatmentAsync(string id, CancellationToken ct = default);

    /// <summary>
    /// The treatment id a state span decomposed from a treatment was written under, which the
    /// decomposer upserts that span on, or <c>null</c> when no such span has that id.
    /// </summary>
    Task<string?> GetStateSpanTreatmentIdAsync(string id, CancellationToken ct = default);

    /// <summary>
    /// <see cref="GetStateSpanTreatmentIdAsync"/> for each of <paramref name="ids"/> some span has, one
    /// read per span table.
    /// </summary>
    Task<IReadOnlyDictionary<string, string>> GetStateSpanTreatmentIdsAsync(
        IReadOnlyCollection<string> ids, CancellationToken ct = default);

    /// <summary>
    /// <see cref="GetProjectedStateSpanTreatmentAsync"/> for each of <paramref name="ids"/> some span
    /// has, with the reads it takes to find them in batches and to project them together.
    /// </summary>
    Task<IReadOnlyDictionary<string, Treatment>> GetProjectedStateSpanTreatmentsAsync(
        IReadOnlyCollection<string> ids, CancellationToken ct = default);

    /// <summary>
    /// Whether a state span decomposed from a treatment that has that id, by any id a client may hold
    /// for it, was deleted by the user.
    /// </summary>
    Task<bool> IsStateSpanDeletedByUserAsync(string id, CancellationToken ct = default);

    /// <summary>
    /// The treatments served over the window, bounds inclusive: those that start inside it.
    /// </summary>
    Task<long> CountProjectedTreatmentsAsync(
        long? fromMills,
        long? toMills,
        CancellationToken ct = default
    );
}
