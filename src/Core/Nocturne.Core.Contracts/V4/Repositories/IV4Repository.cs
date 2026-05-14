using Nocturne.Core.Models.V4;

namespace Nocturne.Core.Contracts.V4.Repositories;

/// <summary>
/// Generic CRUD repository contract for all V4 <see cref="IV4Record"/> entity types.
/// </summary>
/// <typeparam name="T">The V4 record type stored by this repository. Must implement <see cref="IV4Record"/>.</typeparam>
/// <remarks>
/// All V4 repositories extend this interface. Each concrete repository may add domain-specific
/// query methods (e.g., <c>GetByLegacyIdAsync</c>, <c>BulkCreateAsync</c>) beyond the standard CRUD set.
/// </remarks>
/// <seealso cref="IV4Record"/>
public interface IV4Repository<T> where T : class, IV4Record
{
    /// <summary>
    /// Retrieve a page of records filtered by time range, device, and data source.
    /// </summary>
    /// <param name="from">Inclusive start of the time window, or <c>null</c> for no lower bound.</param>
    /// <param name="to">Exclusive end of the time window, or <c>null</c> for no upper bound.</param>
    /// <param name="device">Optional device identifier filter.</param>
    /// <param name="source">Optional data source filter (e.g., connector name).</param>
    /// <param name="limit">Maximum number of records to return.</param>
    /// <param name="offset">Number of records to skip for pagination.</param>
    /// <param name="descending">When <c>true</c>, results are ordered newest-first; otherwise oldest-first.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Matching records in the requested order.</returns>
    Task<IEnumerable<T>> GetAsync(DateTime? from, DateTime? to, string? device, string? source,
        int limit, int offset, bool descending, CancellationToken ct = default);

    /// <summary>
    /// Retrieve a single record by its UUID v7 identifier.
    /// </summary>
    /// <param name="id">UUID v7 record identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The record, or <c>null</c> if not found.</returns>
    Task<T?> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Persist a new record and return the saved entity with any server-assigned fields populated.
    /// </summary>
    /// <param name="model">Record to create.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The created record as persisted.</returns>
    Task<T> CreateAsync(T model, CancellationToken ct = default);

    /// <summary>
    /// Replace an existing record identified by <paramref name="id"/> with the supplied data.
    /// </summary>
    /// <param name="id">UUID v7 identifier of the record to update.</param>
    /// <param name="model">Updated record data.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The updated record as persisted.</returns>
    Task<T> UpdateAsync(Guid id, T model, CancellationToken ct = default);

    /// <summary>
    /// Delete the record with the given identifier.
    /// </summary>
    /// <param name="id">UUID v7 identifier of the record to delete.</param>
    /// <param name="ct">Cancellation token.</param>
    Task DeleteAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Restore a soft-deleted record by clearing its DeletedAt timestamp.
    /// </summary>
    /// <param name="id">UUID v7 identifier of the soft-deleted record.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The restored record.</returns>
    /// <exception cref="KeyNotFoundException">If no soft-deleted record with the given ID exists.</exception>
    Task<T> RestoreAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Restore multiple soft-deleted records by clearing their DeletedAt timestamps.
    /// </summary>
    /// <param name="ids">UUID v7 identifiers of the soft-deleted records.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The restored records (only those that were actually soft-deleted).</returns>
    Task<IEnumerable<T>> BulkRestoreAsync(IEnumerable<Guid> ids, CancellationToken ct = default);

    /// <summary>
    /// Retrieve soft-deleted records for the "trash" view, ordered by deletion date (newest first).
    /// </summary>
    /// <param name="limit">Maximum number of records to return.</param>
    /// <param name="offset">Number of records to skip for pagination.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Soft-deleted records ordered by DeletedAt descending.</returns>
    Task<IEnumerable<T>> GetDeletedAsync(int limit, int offset, CancellationToken ct = default);

    /// <summary>
    /// Count soft-deleted records.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Number of soft-deleted records.</returns>
    Task<int> CountDeletedAsync(CancellationToken ct = default);

    /// <summary>
    /// Count records within an optional time range.
    /// </summary>
    /// <param name="from">Inclusive start of the time window, or <c>null</c> for no lower bound.</param>
    /// <param name="to">Exclusive end of the time window, or <c>null</c> for no upper bound.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Number of records in the range.</returns>
    Task<int> CountAsync(DateTime? from, DateTime? to, CancellationToken ct = default);
}
