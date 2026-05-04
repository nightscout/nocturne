using Nocturne.Core.Models.V4;

namespace Nocturne.Core.Contracts.V4.Repositories;

/// <summary>
/// Repository for <see cref="MeterGlucose"/> records representing blood glucose readings from a glucose meter
/// as reported by the pump or uploader device (distinct from fingerstick <see cref="BGCheck"/> records entered manually).
/// </summary>
/// <remarks>
/// Meter glucose readings embedded in pump data exports are stored here; they are not the same as
/// standalone BG checks submitted through the V1/V4 API. See <see cref="IBGCheckRepository"/> for those.
/// </remarks>
/// <seealso cref="MeterGlucose"/>
/// <seealso cref="IBGCheckRepository"/>
/// <seealso cref="IV4Repository{T}"/>
public interface IMeterGlucoseRepository : IV4Repository<MeterGlucose>
{
    /// <summary>Retrieve a page of <see cref="MeterGlucose"/> records filtered by time range, device, and source.</summary>
    /// <param name="from">Inclusive start of the time window, or <c>null</c> for no lower bound.</param>
    /// <param name="to">Exclusive end of the time window, or <c>null</c> for no upper bound.</param>
    /// <param name="device">Optional device identifier filter.</param>
    /// <param name="source">Optional data source filter.</param>
    /// <param name="limit">Maximum number of records to return (default 100).</param>
    /// <param name="offset">Number of records to skip for pagination (default 0).</param>
    /// <param name="descending">When <c>true</c>, results are ordered newest-first (default).</param>
    /// <param name="ct">Cancellation token.</param>
    new Task<IEnumerable<MeterGlucose>> GetAsync(DateTime? from, DateTime? to, string? device, string? source, int limit = 100, int offset = 0, bool descending = true, CancellationToken ct = default);

    /// <summary>Returns a single <see cref="MeterGlucose"/> by its UUID v7, or <c>null</c> if not found.</summary>
    /// <param name="id">UUID v7 record identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    new Task<MeterGlucose?> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>Retrieve a <see cref="MeterGlucose"/> record by its original MongoDB ObjectId.</summary>
    /// <param name="legacyId">Original MongoDB ObjectId string.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The matching record, or <c>null</c> if not found.</returns>
    Task<MeterGlucose?> GetByLegacyIdAsync(string legacyId, CancellationToken ct = default);

    /// <summary>Persist a new <see cref="MeterGlucose"/> record and return the saved entity.</summary>
    /// <param name="model">Record to create.</param>
    /// <param name="ct">Cancellation token.</param>
    new Task<MeterGlucose> CreateAsync(MeterGlucose model, CancellationToken ct = default);

    /// <summary>Replace an existing <see cref="MeterGlucose"/> identified by <paramref name="id"/>.</summary>
    /// <param name="id">UUID v7 identifier of the record to update.</param>
    /// <param name="model">Updated record data.</param>
    /// <param name="ct">Cancellation token.</param>
    new Task<MeterGlucose> UpdateAsync(Guid id, MeterGlucose model, CancellationToken ct = default);

    /// <summary>Delete a <see cref="MeterGlucose"/> record by its UUID v7.</summary>
    /// <param name="id">UUID v7 identifier of the record to delete.</param>
    /// <param name="ct">Cancellation token.</param>
    new Task DeleteAsync(Guid id, CancellationToken ct = default);

    /// <summary>Delete the <see cref="MeterGlucose"/> record with the given legacy MongoDB ObjectId.</summary>
    /// <param name="legacyId">Original MongoDB ObjectId string.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Number of records deleted (0 or 1).</returns>
    Task<int> DeleteByLegacyIdAsync(string legacyId, CancellationToken ct = default);

    /// <summary>Count <see cref="MeterGlucose"/> records within an optional time range.</summary>
    /// <param name="from">Inclusive start, or <c>null</c> for no lower bound.</param>
    /// <param name="to">Exclusive end, or <c>null</c> for no upper bound.</param>
    /// <param name="ct">Cancellation token.</param>
    new Task<int> CountAsync(DateTime? from, DateTime? to, CancellationToken ct = default);

    /// <summary>Retrieve all <see cref="MeterGlucose"/> records sharing the same correlation identifier.</summary>
    /// <param name="correlationId">Correlation ID linking related records.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IEnumerable<MeterGlucose>> GetByCorrelationIdAsync(Guid correlationId, CancellationToken ct = default);

    /// <summary>
    /// Retrieve the timestamp of the most recently stored <see cref="MeterGlucose"/> reading, optionally scoped to a data source.
    /// </summary>
    /// <param name="source">Optional data source filter. Pass <c>null</c> to search across all sources.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The latest reading timestamp, or <c>null</c> if no records exist.</returns>
    Task<DateTime?> GetLatestTimestampAsync(string? source = null, CancellationToken ct = default);

    /// <summary>
    /// Retrieve the timestamp of the oldest stored <see cref="MeterGlucose"/> reading, optionally scoped to a data source.
    /// </summary>
    /// <param name="source">Optional data source filter. Pass <c>null</c> to search across all sources.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The oldest reading timestamp, or <c>null</c> if no records exist.</returns>
    Task<DateTime?> GetOldestTimestampAsync(string? source = null, CancellationToken ct = default);

    /// <summary>
    /// Delete all <see cref="MeterGlucose"/> records matching the given data source.
    /// </summary>
    /// <param name="source">Data source identifier (e.g., connector name).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Number of records deleted.</returns>
    Task<int> DeleteBySourceAsync(string source, CancellationToken ct = default);

    /// <summary>
    /// Delete all records within the given time range.
    /// </summary>
    /// <param name="from">Inclusive start, or <c>null</c> for no lower bound.</param>
    /// <param name="to">Exclusive end, or <c>null</c> for no upper bound.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Number of records deleted.</returns>
    Task<int> DeleteByTimeRangeAsync(DateTime? from, DateTime? to, CancellationToken ct = default);
}
