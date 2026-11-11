using System.Text.Json;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.Core.Contracts.Treatments;

/// <summary>
/// Domain service for treatment operations with WebSocket broadcasting
/// </summary>
public interface ITreatmentService
{
    /// <summary>
    /// Get treatments with optional filtering and pagination
    /// </summary>
    /// <param name="find">Optional MongoDB query filter</param>
    /// <param name="count">Maximum number of treatments to return</param>
    /// <param name="skip">Number of treatments to skip for pagination</param>
    Task<IEnumerable<Treatment>> GetTreatmentsAsync(
        string? find = null,
        int? count = null,
        int? skip = null,
        CancellationToken cancellationToken = default
    );

    /// <param name="count">Maximum number of treatments to return</param>
    /// <param name="skip">Number of treatments to skip for pagination</param>
    Task<IEnumerable<Treatment>> GetTreatmentsAsync(
        int count,
        int skip = 0,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// The newest treatment of one legacy event type within the window, bounds inclusive, or
    /// <c>null</c>. Only the types Nightscout's dataloader looks up singly are served: a device event
    /// type, and a Profile Switch of zero duration.
    /// </summary>
    Task<Treatment?> GetLatestTreatmentOfTypeAsync(
        string eventType,
        long fromMills,
        long toMills,
        CancellationToken cancellationToken = default
    );

    /// <returns>Treatment if found, null otherwise</returns>
    Task<Treatment?> GetTreatmentByIdAsync(
        string id,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Whether <paramref name="id"/> names a treatment the user deleted, so a save under it must not
    /// bring the treatment back
    /// </summary>
    /// <param name="id">Treatment ID as the client sent it</param>
    Task<bool> IsTreatmentDeletedByUserAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Treatments matching <paramref name="query"/>, in the order it asks for.</summary>
    Task<IEnumerable<Treatment>> QueryTreatmentsAsync(TreatmentQuery query, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns treatments whose <see cref="Treatment.Mills"/> falls within
    /// <c>[fromMills, toMills]</c>, for the current tenant. Useful for replay or other
    /// point-in-time evaluation needing a stable window that is independent of how many
    /// newer treatments exist for the tenant.
    /// </summary>
    /// <param name="fromMills">Inclusive lower bound of the time window (Unix ms).</param>
    /// <param name="toMills">Inclusive upper bound of the time window (Unix ms).</param>
    /// <returns>Treatments within the range, newest-first.</returns>
    Task<IReadOnlyList<Treatment>> GetTreatmentsByRangeAsync(
        long fromMills,
        long toMills,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// The id of the stored treatment at <paramref name="treatment"/>'s time with its event type, or
    /// <see langword="null"/> when none is stored: Nightscout's upsert key for a document with neither
    /// <c>identifier</c> nor <c>_id</c>.
    /// </summary>
    Task<string?> FindSameEventIdAsync(Treatment treatment, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get treatments modified since a given timestamp, including StateSpan-derived temp basals.
    /// Used by the v3 incremental sync endpoint.
    /// </summary>
    /// <param name="lastModifiedMills">Unix millisecond cutoff; only treatments modified after this are returned.</param>
    /// <param name="limit">Maximum number of treatments to return.</param>
    /// <returns>Collection of recently modified treatments.</returns>
    Task<IEnumerable<Treatment>> GetTreatmentsModifiedSinceAsync(
        long lastModifiedMills,
        int limit = 500,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Create new treatments with WebSocket broadcasting
    /// </summary>
    /// <remarks>
    /// Each treatment that created or changed a record is announced as reads serve it, under the id
    /// reads serve. One already stored as sent is returned as served but not announced, and one the
    /// user had deleted, which is not written again, is not announced.
    /// </remarks>
    /// <param name="origin">
    /// Write origin for the decomposed records. A <see cref="WriteOrigin.Backfill"/> announces no
    /// create: no real-time broadcast and no write-back.
    /// </param>
    /// <returns>
    /// Created treatments with assigned IDs, carrying how many of their records were not written
    /// because the user had deleted them.
    /// </returns>
    /// <exception cref="TreatmentBatchFailedException">
    /// The batch could not be stored. <see cref="TreatmentBatchFailedException.Written"/> names the
    /// treatments that stored a record before the failure; those that created or changed one are announced.
    /// </exception>
    Task<BulkWrite<Treatment>> CreateTreatmentsAsync(
        IEnumerable<Treatment> treatments,
        WriteOrigin origin = WriteOrigin.Live,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// <see cref="CreateTreatmentsAsync"/> for treatments posted to the v1 API, where a meal with a
    /// <c>preBolus</c> is stored as a bolus and its carbs at the preBolus offset, as Nightscout's
    /// POST stores it. Both halves are returned, the bolus first.
    /// </summary>
    /// <exception cref="TreatmentBatchFailedException">
    /// The batch could not be stored. <see cref="TreatmentBatchFailedException.Written"/> names the
    /// treatments that stored a record before the failure; those that created or changed one are announced.
    /// </exception>
    Task<BulkWrite<Treatment>> CreateV1TreatmentsAsync(
        IEnumerable<Treatment> treatments,
        WriteOrigin origin = WriteOrigin.Live,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Writes a page of treatments imported from another Nightscout, or generated as sample data, as a
    /// backfill with no real-time broadcast. Bolus and basal treatments are given the patient's insulin
    /// as a created treatment is.
    /// </summary>
    /// <returns>What the page stored, and what it passed over as deleted or unsupported.</returns>
    Task<DecompositionResult> ImportTreatmentsAsync(
        IReadOnlyList<Treatment> treatments,
        CancellationToken cancellationToken = default
    );

    /// <summary>Counts treatments matching the optional filter.</summary>
    /// <param name="find">Optional Nightscout-compatible find query.</param>
    Task<long> CountTreatmentsAsync(string? find = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// The legacy ids of <paramref name="source"/>'s live records, including the state spans its
    /// Profile Switches, overrides and temporary targets were stored as, whose event time falls in
    /// [<paramref name="from"/>, <paramref name="to"/>], each with that event time.
    /// </summary>
    Task<IReadOnlyDictionary<string, DateTime>> GetStoredIdsFromSourceAsync(
        string source, DateTime from, DateTime to, CancellationToken cancellationToken = default);

    /// <summary>
    /// Soft-deletes <paramref name="source"/>'s records decomposed from <paramref name="legacyIds"/>,
    /// including the state spans those treatments served as and the profile rows of a Profile Switch,
    /// leaving every other source's rows under those ids alone. A deleted row that was a dedup
    /// group's primary hands the flag to a surviving member, so another source's copy of the
    /// event stays visible.
    /// </summary>
    /// <returns>Total number of v4 records deleted across all tables.</returns>
    Task<int> DeleteFromSourceAsync(
        string source, IReadOnlySet<string> legacyIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves <paramref name="source"/>'s records stored under a treatment's client id
    /// (<see cref="TreatmentClientId"/>) onto the treatment's own id, when nothing is stored under that
    /// id yet. Of treatments sharing a client id, the one at the records' time takes them (the first when
    /// none is). Records the user deleted stay under the client id and are copied onto every one of
    /// them, so they keep blocking equivalents delivered in a later call too.
    /// </summary>
    /// <returns>Total number of v4 records moved.</returns>
    Task<int> RekeyClientIdRecordsAsync(
        string source, IReadOnlyList<Treatment> treatments, CancellationToken cancellationToken = default);

    /// <summary>Of treatments <paramref name="source"/> delivered again, the ones to decompose again.</summary>
    Task<IReadOnlyList<Treatment>> SelectForRepublishAsync(
        string source, IReadOnlyList<Treatment> treatments, CancellationToken cancellationToken = default);

    /// <summary>
    /// Update an existing treatment with WebSocket broadcasting
    /// </summary>
    /// <param name="treatment">Updated treatment data</param>
    /// <returns>Updated treatment if successful, null otherwise</returns>
    Task<Treatment?> UpdateTreatmentAsync(
        string id,
        Treatment treatment,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Partially update a treatment via JSON merge-patch with StateSpan awareness.
    /// If the treatment is a temp basal backed by a StateSpan, the StateSpan is also updated.
    /// </summary>
    /// <param name="patchData">JSON merge-patch data</param>
    /// <returns>Patched treatment if successful, null otherwise</returns>
    Task<Treatment?> PatchTreatmentAsync(
        string id,
        JsonElement patchData,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Delete a treatment with WebSocket broadcasting
    /// </summary>
    /// <returns>True if deleted successfully, false otherwise</returns>
    Task<bool> DeleteTreatmentAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Delete multiple treatments with optional filtering
    /// </summary>
    /// <param name="find">Optional MongoDB query filter</param>
    /// <returns>Number of treatments deleted</returns>
    Task<long> DeleteTreatmentsAsync(
        string? find = null,
        CancellationToken cancellationToken = default
    );
}
