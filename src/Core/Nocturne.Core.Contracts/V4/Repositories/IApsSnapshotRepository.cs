using Nocturne.Core.Models.V4;

namespace Nocturne.Core.Contracts.V4.Repositories;

/// <summary>
/// Repository for <see cref="ApsSnapshot"/> records produced by an APS (Automated Pump System) loop algorithm.
/// </summary>
/// <remarks>
/// APS snapshots capture the decision state of a closed-loop system at a point in time.
/// Extends <see cref="IV4Repository{T}"/> with legacy-id lookups used during MongoDB migration.
/// </remarks>
/// <seealso cref="ApsSnapshot"/>
/// <seealso cref="IV4Repository{T}"/>
public interface IApsSnapshotRepository : IV4Repository<ApsSnapshot>
{
    /// <summary>
    /// Retrieve a page of <see cref="ApsSnapshot"/> records filtered by time range, device, and source.
    /// </summary>
    /// <param name="from">Inclusive start of the time window, or <c>null</c> for no lower bound.</param>
    /// <param name="to">Exclusive end of the time window, or <c>null</c> for no upper bound.</param>
    /// <param name="device">Optional device identifier filter.</param>
    /// <param name="source">Optional data source filter.</param>
    /// <param name="limit">Maximum number of records to return (default 100).</param>
    /// <param name="offset">Number of records to skip for pagination (default 0).</param>
    /// <param name="descending">When <c>true</c>, results are ordered newest-first (default).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Matching <see cref="ApsSnapshot"/> records.</returns>
    new Task<IEnumerable<ApsSnapshot>> GetAsync(DateTime? from, DateTime? to, string? device, string? source, int limit = 100, int offset = 0, bool descending = true, CancellationToken ct = default);

    /// <summary>Returns a single <see cref="ApsSnapshot"/> by its UUID v7, or <c>null</c> if not found.</summary>
    /// <param name="id">UUID v7 record identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    new Task<ApsSnapshot?> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Retrieve an <see cref="ApsSnapshot"/> by its original MongoDB ObjectId (preserved for migration compatibility).
    /// </summary>
    /// <param name="legacyId">Original MongoDB ObjectId string.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The matching record, or <c>null</c> if not found.</returns>
    Task<ApsSnapshot?> GetByLegacyIdAsync(string legacyId, CancellationToken ct = default);

    /// <summary>Persist a new <see cref="ApsSnapshot"/> and return the saved entity.</summary>
    /// <param name="model">Record to create.</param>
    /// <param name="ct">Cancellation token.</param>
    new Task<ApsSnapshot> CreateAsync(ApsSnapshot model, CancellationToken ct = default);

    /// <summary>Replace an existing <see cref="ApsSnapshot"/> identified by <paramref name="id"/>.</summary>
    /// <param name="id">UUID v7 identifier of the record to update.</param>
    /// <param name="model">Updated record data.</param>
    /// <param name="ct">Cancellation token.</param>
    new Task<ApsSnapshot> UpdateAsync(Guid id, ApsSnapshot model, CancellationToken ct = default);

    /// <summary>Delete an <see cref="ApsSnapshot"/> by its UUID v7.</summary>
    /// <param name="id">UUID v7 identifier of the record to delete.</param>
    /// <param name="ct">Cancellation token.</param>
    new Task DeleteAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Delete the <see cref="ApsSnapshot"/> with the given legacy MongoDB ObjectId.
    /// </summary>
    /// <param name="legacyId">Original MongoDB ObjectId string.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Number of records deleted (0 or 1).</returns>
    Task<int> DeleteByLegacyIdAsync(string legacyId, CancellationToken ct = default);

    /// <summary>Retrieve <see cref="ApsSnapshot"/> records matching any of the given correlation IDs.</summary>
    /// <param name="correlationIds">Correlation IDs to match.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IEnumerable<ApsSnapshot>> GetByCorrelationIdsAsync(IEnumerable<Guid> correlationIds, CancellationToken ct = default);

    /// <summary>Retrieve <see cref="ApsSnapshot"/> records modified since the given timestamp, ordered oldest-first.</summary>
    /// <param name="lastModifiedMills">Unix millisecond timestamp; records with <c>SysUpdatedAt</c> at or after this value are returned.</param>
    /// <param name="limit">Maximum number of records to return (default 1000).</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IEnumerable<ApsSnapshot>> GetModifiedSinceAsync(long lastModifiedMills, int limit = 1000, CancellationToken ct = default);

    /// <summary>Count <see cref="ApsSnapshot"/> records within an optional time range.</summary>
    /// <param name="from">Inclusive start, or <c>null</c> for no lower bound.</param>
    /// <param name="to">Exclusive end, or <c>null</c> for no upper bound.</param>
    /// <param name="ct">Cancellation token.</param>
    new Task<int> CountAsync(DateTime? from, DateTime? to, CancellationToken ct = default);
}
