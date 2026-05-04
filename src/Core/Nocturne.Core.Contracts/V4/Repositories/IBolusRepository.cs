using Nocturne.Core.Models.V4;

namespace Nocturne.Core.Contracts.V4.Repositories;

/// <summary>
/// Repository for <see cref="Bolus"/> records representing delivered insulin doses.
/// </summary>
/// <remarks>
/// Extends <see cref="IV4Repository{T}"/> with bolus-specific filtering by <see cref="BolusKind"/>
/// and a <paramref name="nativeOnly"/> flag to distinguish between boluses entered natively in
/// Nocturne v4 versus those projected from legacy V1/V2/V3 treatment records.
/// </remarks>
/// <seealso cref="Bolus"/>
/// <seealso cref="BolusKind"/>
/// <seealso cref="IBolusCalculationRepository"/>
/// <seealso cref="IV4Repository{T}"/>
public interface IBolusRepository : IV4Repository<Bolus>
{
    /// <summary>
    /// Retrieve a page of <see cref="Bolus"/> records filtered by time range, device, source, origin, and kind.
    /// </summary>
    /// <param name="from">Inclusive start of the time window, or <c>null</c> for no lower bound.</param>
    /// <param name="to">Exclusive end of the time window, or <c>null</c> for no upper bound.</param>
    /// <param name="device">Optional device identifier filter.</param>
    /// <param name="source">Optional data source filter.</param>
    /// <param name="limit">Maximum number of records to return (default 100).</param>
    /// <param name="offset">Number of records to skip for pagination (default 0).</param>
    /// <param name="descending">When <c>true</c>, results are ordered newest-first (default).</param>
    /// <param name="nativeOnly">When <c>true</c>, excludes boluses projected from legacy V1/V2/V3 treatments.</param>
    /// <param name="kind">Optional <see cref="BolusKind"/> filter (e.g., Manual, SMB, Extended).</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IEnumerable<Bolus>> GetAsync(
        DateTime? from,
        DateTime? to,
        string? device,
        string? source,
        int limit = 100,
        int offset = 0,
        bool descending = true,
        bool nativeOnly = false,
        BolusKind? kind = null,
        CancellationToken ct = default
    );

    // Explicit base-interface bridge — delegates to the extended overload
    Task<IEnumerable<Bolus>> IV4Repository<Bolus>.GetAsync(
        DateTime? from, DateTime? to, string? device, string? source,
        int limit, int offset, bool descending, CancellationToken ct)
        => GetAsync(from, to, device, source, limit, offset, descending, false, null, ct);

    /// <summary>Returns a single <see cref="Bolus"/> by its UUID v7, or <c>null</c> if not found.</summary>
    /// <param name="id">UUID v7 record identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    new Task<Bolus?> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>Retrieve a <see cref="Bolus"/> by its original MongoDB ObjectId.</summary>
    /// <param name="legacyId">Original MongoDB ObjectId string.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The matching record, or <c>null</c> if not found.</returns>
    Task<Bolus?> GetByLegacyIdAsync(string legacyId, CancellationToken ct = default);

    /// <summary>Persist a new <see cref="Bolus"/> and return the saved entity.</summary>
    /// <param name="model">Record to create.</param>
    /// <param name="ct">Cancellation token.</param>
    new Task<Bolus> CreateAsync(Bolus model, CancellationToken ct = default);

    /// <summary>Replace an existing <see cref="Bolus"/> identified by <paramref name="id"/>.</summary>
    /// <param name="id">UUID v7 identifier of the record to update.</param>
    /// <param name="model">Updated record data.</param>
    /// <param name="ct">Cancellation token.</param>
    new Task<Bolus> UpdateAsync(Guid id, Bolus model, CancellationToken ct = default);

    /// <summary>Delete a <see cref="Bolus"/> by its UUID v7.</summary>
    /// <param name="id">UUID v7 identifier of the record to delete.</param>
    /// <param name="ct">Cancellation token.</param>
    new Task DeleteAsync(Guid id, CancellationToken ct = default);

    /// <summary>Delete the <see cref="Bolus"/> with the given legacy MongoDB ObjectId.</summary>
    /// <param name="legacyId">Original MongoDB ObjectId string.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Number of records deleted (0 or 1).</returns>
    Task<int> DeleteByLegacyIdAsync(string legacyId, CancellationToken ct = default);

    /// <summary>Delete <see cref="Bolus"/> records matching the given data source and sync identifier.</summary>
    /// <param name="dataSource">The external data source name.</param>
    /// <param name="syncIdentifier">The external sync identifier (e.g., UUID from the uploading system).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Number of records deleted.</returns>
    Task<int> DeleteBySyncIdentifierAsync(string dataSource, string syncIdentifier, CancellationToken ct = default);

    /// <summary>Count <see cref="Bolus"/> records within an optional time range.</summary>
    /// <param name="from">Inclusive start, or <c>null</c> for no lower bound.</param>
    /// <param name="to">Exclusive end, or <c>null</c> for no upper bound.</param>
    /// <param name="ct">Cancellation token.</param>
    new Task<int> CountAsync(DateTime? from, DateTime? to, CancellationToken ct = default);

    /// <summary>Retrieve all <see cref="Bolus"/> records sharing the same correlation identifier.</summary>
    /// <param name="correlationId">Correlation ID linking a bolus to its wizard calculation or meal.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IEnumerable<Bolus>> GetByCorrelationIdAsync(
        Guid correlationId,
        CancellationToken ct = default
    );

    /// <summary>Insert multiple <see cref="Bolus"/> records in a single batch operation.</summary>
    /// <param name="records">Records to insert.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The inserted records with server-assigned fields populated.</returns>
    Task<IEnumerable<Bolus>> BulkCreateAsync(
        IEnumerable<Bolus> records,
        CancellationToken ct = default
    );
}
