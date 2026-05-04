using System.Text.Json;
using Nocturne.Core.Models;

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
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Collection of treatments</returns>
    Task<IEnumerable<Treatment>> GetTreatmentsAsync(
        string? find = null,
        int? count = null,
        int? skip = null,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Get treatments with count and pagination
    /// </summary>
    /// <param name="count">Maximum number of treatments to return</param>
    /// <param name="skip">Number of treatments to skip for pagination</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Collection of treatments</returns>
    Task<IEnumerable<Treatment>> GetTreatmentsAsync(
        int count,
        int skip = 0,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Get a specific treatment by ID
    /// </summary>
    /// <param name="id">Treatment ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Treatment if found, null otherwise</returns>
    Task<Treatment?> GetTreatmentByIdAsync(
        string id,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Get treatments with advanced V3-style filtering, including StateSpan-derived temp basals.
    /// </summary>
    /// <param name="count">Maximum number of treatments to return.</param>
    /// <param name="skip">Number of treatments to skip for pagination.</param>
    /// <param name="findQuery">Optional MongoDB-style query filter string.</param>
    /// <param name="reverseResults">When <c>true</c>, results are returned in ascending chronological order.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Collection of treatments matching the filter criteria.</returns>
    Task<IEnumerable<Treatment>> GetTreatmentsWithAdvancedFilterAsync(
        int count,
        int skip,
        string? findQuery,
        bool reverseResults,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Returns treatments whose <see cref="Treatment.Mills"/> falls within
    /// <c>[fromMills, toMills]</c>, for the current tenant. Useful for replay or other
    /// point-in-time evaluation needing a stable window that is independent of how many
    /// newer treatments exist for the tenant.
    /// </summary>
    /// <param name="fromMills">Inclusive lower bound of the time window (Unix ms).</param>
    /// <param name="toMills">Inclusive upper bound of the time window (Unix ms).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Treatments within the range, newest-first.</returns>
    Task<IReadOnlyList<Treatment>> GetTreatmentsByRangeAsync(
        long fromMills,
        long toMills,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Get treatments modified since a given timestamp, including StateSpan-derived temp basals.
    /// Used by the v3 incremental sync endpoint.
    /// </summary>
    /// <param name="lastModifiedMills">Unix millisecond cutoff; only treatments modified after this are returned.</param>
    /// <param name="limit">Maximum number of treatments to return.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Collection of recently modified treatments.</returns>
    Task<IEnumerable<Treatment>> GetTreatmentsModifiedSinceAsync(
        long lastModifiedMills,
        int limit = 500,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Create new treatments with WebSocket broadcasting
    /// </summary>
    /// <param name="treatments">Treatments to create</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Created treatments with assigned IDs</returns>
    Task<IEnumerable<Treatment>> CreateTreatmentsAsync(
        IEnumerable<Treatment> treatments,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Update an existing treatment with WebSocket broadcasting
    /// </summary>
    /// <param name="id">Treatment ID to update</param>
    /// <param name="treatment">Updated treatment data</param>
    /// <param name="cancellationToken">Cancellation token</param>
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
    /// <param name="id">Treatment ID to patch</param>
    /// <param name="patchData">JSON merge-patch data</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Patched treatment if successful, null otherwise</returns>
    Task<Treatment?> PatchTreatmentAsync(
        string id,
        JsonElement patchData,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Delete a treatment with WebSocket broadcasting
    /// </summary>
    /// <param name="id">Treatment ID to delete</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if deleted successfully, false otherwise</returns>
    Task<bool> DeleteTreatmentAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Delete multiple treatments with optional filtering
    /// </summary>
    /// <param name="find">Optional MongoDB query filter</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Number of treatments deleted</returns>
    Task<long> DeleteTreatmentsAsync(
        string? find = null,
        CancellationToken cancellationToken = default
    );
}
