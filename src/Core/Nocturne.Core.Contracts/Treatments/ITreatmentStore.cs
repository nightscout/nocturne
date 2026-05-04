using Nocturne.Core.Models;

namespace Nocturne.Core.Contracts.Treatments;

/// <summary>
/// Driven port for treatment persistence backed by V4 granular tables.
/// Reads are projected from V4 repositories into the legacy Treatment shape.
/// Writes are routed through the decomposition pipeline.
/// </summary>
/// <seealso cref="ITreatmentCache"/>
/// <seealso cref="TreatmentQuery"/>
public interface ITreatmentStore
{
    /// <summary>
    /// Queries treatments using the specified <see cref="TreatmentQuery"/> parameters,
    /// projecting V4 records into the legacy Treatment shape.
    /// </summary>
    /// <param name="query">The <see cref="TreatmentQuery"/> filter and pagination parameters.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A read-only list of <see cref="Treatment"/> records matching the query.</returns>
    Task<IReadOnlyList<Treatment>> QueryAsync(TreatmentQuery query, CancellationToken ct = default);

    /// <summary>
    /// Returns a single treatment by its identifier.
    /// </summary>
    /// <param name="id">The treatment identifier (GUID or legacy MongoDB ObjectId).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The <see cref="Treatment"/> if found, or <c>null</c>.</returns>
    Task<Treatment?> GetByIdAsync(string id, CancellationToken ct = default);

    /// <summary>
    /// Returns treatments whose <see cref="Treatment.Mills"/> falls within
    /// <c>[fromMills, toMills]</c>, for the current tenant. Stable window read used by
    /// replay/point-in-time evaluation; not bounded by an arbitrary "newest N" page size.
    /// </summary>
    /// <param name="fromMills">Inclusive lower bound of the time window (Unix ms).</param>
    /// <param name="toMills">Inclusive upper bound of the time window (Unix ms).</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<Treatment>> GetByRangeAsync(long fromMills, long toMills, CancellationToken ct = default);

    /// <summary>
    /// Returns treatments modified after the given timestamp, for incremental sync (v3 API).
    /// </summary>
    /// <param name="lastModifiedMills">The Unix-millisecond cutoff; only treatments modified after this are returned.</param>
    /// <param name="limit">Maximum number of records to return.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A read-only list of recently modified <see cref="Treatment"/> records.</returns>
    Task<IReadOnlyList<Treatment>> GetModifiedSinceAsync(long lastModifiedMills, int limit, CancellationToken ct = default);

    /// <summary>
    /// Creates one or more treatments, routing writes to both the legacy table and V4
    /// granular tables via the decomposition pipeline.
    /// </summary>
    /// <param name="treatments">The treatments to create.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A read-only list of the created <see cref="Treatment"/> records.</returns>
    Task<IReadOnlyList<Treatment>> CreateAsync(IReadOnlyList<Treatment> treatments, CancellationToken ct = default);

    /// <summary>
    /// Updates an existing treatment by ID, propagating changes to V4 tables.
    /// </summary>
    /// <param name="id">The treatment identifier.</param>
    /// <param name="treatment">The updated <see cref="Treatment"/> data.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The updated <see cref="Treatment"/>, or <c>null</c> if not found.</returns>
    Task<Treatment?> UpdateAsync(string id, Treatment treatment, CancellationToken ct = default);

    /// <summary>
    /// Deletes a treatment by ID, removing corresponding V4 decomposed records.
    /// </summary>
    /// <param name="id">The treatment identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><c>true</c> if the treatment was deleted; <c>false</c> if not found.</returns>
    Task<bool> DeleteAsync(string id, CancellationToken ct = default);

    /// <summary>
    /// Counts treatments matching the optional find filter, summing across all V4 treatment repositories.
    /// </summary>
    /// <param name="find">Optional Nightscout-compatible find query for time range filtering.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<long> CountAsync(string? find = null, CancellationToken ct = default);
}
