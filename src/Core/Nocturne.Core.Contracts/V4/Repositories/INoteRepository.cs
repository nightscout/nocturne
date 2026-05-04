using Nocturne.Core.Models.V4;

namespace Nocturne.Core.Contracts.V4.Repositories;

/// <summary>
/// Repository for <see cref="Note"/> records representing free-text annotations
/// entered by the user at a point in time.
/// </summary>
/// <remarks>
/// Notes are projected from legacy Nightscout treatment records of type <c>Note</c> as well as
/// created natively through the V4 API. The <paramref name="nativeOnly"/> flag restricts results
/// to records created natively, excluding projected legacy entries.
/// </remarks>
/// <seealso cref="Note"/>
/// <seealso cref="IV4Repository{T}"/>
public interface INoteRepository : IV4Repository<Note>
{
    /// <summary>
    /// Retrieve a page of <see cref="Note"/> records filtered by time range, device, source, and origin.
    /// </summary>
    /// <param name="from">Inclusive start of the time window, or <c>null</c> for no lower bound.</param>
    /// <param name="to">Exclusive end of the time window, or <c>null</c> for no upper bound.</param>
    /// <param name="device">Optional device identifier filter.</param>
    /// <param name="source">Optional data source filter.</param>
    /// <param name="limit">Maximum number of records to return (default 100).</param>
    /// <param name="offset">Number of records to skip for pagination (default 0).</param>
    /// <param name="descending">When <c>true</c>, results are ordered newest-first (default).</param>
    /// <param name="nativeOnly">When <c>true</c>, excludes records projected from legacy V1/V2/V3 treatments.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IEnumerable<Note>> GetAsync(
        DateTime? from,
        DateTime? to,
        string? device,
        string? source,
        int limit = 100,
        int offset = 0,
        bool descending = true,
        bool nativeOnly = false,
        CancellationToken ct = default
    );

    // Explicit base-interface bridge — delegates to the extended overload
    Task<IEnumerable<Note>> IV4Repository<Note>.GetAsync(
        DateTime? from, DateTime? to, string? device, string? source,
        int limit, int offset, bool descending, CancellationToken ct)
        => GetAsync(from, to, device, source, limit, offset, descending, false, ct);

    /// <summary>Returns a single <see cref="Note"/> by its UUID v7, or <c>null</c> if not found.</summary>
    /// <param name="id">UUID v7 record identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    new Task<Note?> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>Retrieve a <see cref="Note"/> by its original MongoDB ObjectId.</summary>
    /// <param name="legacyId">Original MongoDB ObjectId string.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The matching record, or <c>null</c> if not found.</returns>
    Task<Note?> GetByLegacyIdAsync(string legacyId, CancellationToken ct = default);

    /// <summary>Persist a new <see cref="Note"/> and return the saved entity.</summary>
    /// <param name="model">Record to create.</param>
    /// <param name="ct">Cancellation token.</param>
    new Task<Note> CreateAsync(Note model, CancellationToken ct = default);

    /// <summary>Replace an existing <see cref="Note"/> identified by <paramref name="id"/>.</summary>
    /// <param name="id">UUID v7 identifier of the record to update.</param>
    /// <param name="model">Updated record data.</param>
    /// <param name="ct">Cancellation token.</param>
    new Task<Note> UpdateAsync(Guid id, Note model, CancellationToken ct = default);

    /// <summary>Delete a <see cref="Note"/> by its UUID v7.</summary>
    /// <param name="id">UUID v7 identifier of the record to delete.</param>
    /// <param name="ct">Cancellation token.</param>
    new Task DeleteAsync(Guid id, CancellationToken ct = default);

    /// <summary>Delete the <see cref="Note"/> with the given legacy MongoDB ObjectId.</summary>
    /// <param name="legacyId">Original MongoDB ObjectId string.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Number of records deleted (0 or 1).</returns>
    Task<int> DeleteByLegacyIdAsync(string legacyId, CancellationToken ct = default);

    /// <summary>Delete <see cref="Note"/> records matching the given data source and sync identifier.</summary>
    /// <param name="dataSource">The external data source name.</param>
    /// <param name="syncIdentifier">The external sync identifier (e.g., UUID from the uploading system).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Number of records deleted.</returns>
    Task<int> DeleteBySyncIdentifierAsync(string dataSource, string syncIdentifier, CancellationToken ct = default);

    /// <summary>Count <see cref="Note"/> records within an optional time range.</summary>
    /// <param name="from">Inclusive start, or <c>null</c> for no lower bound.</param>
    /// <param name="to">Exclusive end, or <c>null</c> for no upper bound.</param>
    /// <param name="ct">Cancellation token.</param>
    new Task<int> CountAsync(DateTime? from, DateTime? to, CancellationToken ct = default);

    /// <summary>Retrieve all <see cref="Note"/> records sharing the same correlation identifier.</summary>
    /// <param name="correlationId">Correlation ID linking related records.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IEnumerable<Note>> GetByCorrelationIdAsync(
        Guid correlationId,
        CancellationToken ct = default
    );

    /// <summary>Insert multiple <see cref="Note"/> records in a single batch operation.</summary>
    /// <param name="records">Records to insert.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The inserted records with server-assigned fields populated.</returns>
    Task<IEnumerable<Note>> BulkCreateAsync(
        IEnumerable<Note> records,
        CancellationToken ct = default
    );
}
