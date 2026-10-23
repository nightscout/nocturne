using Nocturne.Core.Models;
using Nocturne.Core.Contracts.Glucose;

namespace Nocturne.Core.Contracts.V4;

/// <summary>
/// Projects V4 granular records (Bolus, CarbIntake, BGCheck, Note, DeviceEvent)
/// back into the legacy Treatment shape required by the v1/v2/v3 API endpoints.
/// </summary>
/// <seealso cref="IEntryService"/>
/// <seealso cref="ITreatmentService"/>
public interface IV4ToLegacyProjectionService
{
    /// <summary>
    /// Returns a set of legacy <see cref="Treatment"/> objects synthesised from V4 treatment records
    /// (Bolus, CarbIntake, BGCheck, Note, DeviceEvent).
    /// These supplement the treatments already stored in the legacy treatments table for connectors
    /// that write V4 directly. The newest <paramref name="limit"/> treatments in the window newest
    /// first, or, when <paramref name="descending"/> is <c>false</c>, the oldest oldest first.
    /// </summary>
    Task<IEnumerable<Treatment>> GetProjectedTreatmentsAsync(
        long? fromMills,
        long? toMills,
        int limit,
        bool nativeOnly = true,
        bool descending = true,
        CancellationToken ct = default
    );

    /// <summary>
    /// The newest treatment of one legacy event type within the window, bounds inclusive, or
    /// <c>null</c>. Served by an index seek, not a scan of the window. Only the types Nightscout's
    /// dataloader looks up singly are served: a device event type, and a Profile Switch of zero duration.
    /// </summary>
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
    /// Whether a state span decomposed from a treatment that has that id, by any id a client may hold
    /// for it, was deleted by the user.
    /// </summary>
    Task<bool> IsStateSpanDeletedByUserAsync(string id, CancellationToken ct = default);

    /// <summary>
    /// The treatments the state spans and profile activations decomposed from treatments are served
    /// as over the window, bounds inclusive: those that start inside it.
    /// </summary>
    Task<long> CountProjectedStateSpanTreatmentsAsync(
        long? fromMills,
        long? toMills,
        CancellationToken ct = default
    );

    /// <summary>
    /// The Notes served as treatments of their own over the window, bounds inclusive: every Note but
    /// those a treatment wrote beside another record, which serves it as its <c>notes</c>.
    /// </summary>
    Task<long> CountNotesServedApartAsync(
        long? fromMills,
        long? toMills,
        CancellationToken ct = default
    );
}
