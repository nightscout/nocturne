using Nocturne.Core.Models.V4;

namespace Nocturne.Core.Contracts.V4.Repositories;

/// <summary>
/// Repository for <see cref="Calibration"/> records representing manual CGM calibration events.
/// </summary>
/// <remarks>
/// Calibration records store the fingerstick glucose value and timestamp used to calibrate a
/// continuous glucose sensor. They are distinct from <see cref="BGCheck"/> readings in that
/// they have a direct effect on the sensor's glucose output.
/// </remarks>
/// <seealso cref="Calibration"/>
/// <seealso cref="IBGCheckRepository"/>
/// <seealso cref="IV4Repository{T}"/>
public interface ICalibrationRepository : IV4Repository<Calibration>
{
    /// <summary>Retrieve a page of <see cref="Calibration"/> records filtered by time range, device, and source.</summary>
    /// <param name="from">Inclusive start of the time window, or <c>null</c> for no lower bound.</param>
    /// <param name="to">Exclusive end of the time window, or <c>null</c> for no upper bound.</param>
    /// <param name="device">Optional device identifier filter.</param>
    /// <param name="source">Optional data source filter.</param>
    /// <param name="limit">Maximum number of records to return (default 100).</param>
    /// <param name="offset">Number of records to skip for pagination (default 0).</param>
    /// <param name="descending">When <c>true</c>, results are ordered newest-first (default).</param>
    /// <param name="ct">Cancellation token.</param>
    new Task<IEnumerable<Calibration>> GetAsync(DateTime? from, DateTime? to, string? device, string? source, int limit = 100, int offset = 0, bool descending = true, CancellationToken ct = default);

    /// <summary>Returns a single <see cref="Calibration"/> by its UUID v7, or <c>null</c> if not found.</summary>
    /// <param name="id">UUID v7 record identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    new Task<Calibration?> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>Retrieve a <see cref="Calibration"/> by its original MongoDB ObjectId.</summary>
    /// <param name="legacyId">Original MongoDB ObjectId string.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The matching record, or <c>null</c> if not found.</returns>
    Task<Calibration?> GetByLegacyIdAsync(string legacyId, CancellationToken ct = default);

    /// <summary>Persist a new <see cref="Calibration"/> and return the saved entity.</summary>
    /// <param name="model">Record to create.</param>
    /// <param name="ct">Cancellation token.</param>
    new Task<Calibration> CreateAsync(Calibration model, CancellationToken ct = default);

    /// <summary>Replace an existing <see cref="Calibration"/> identified by <paramref name="id"/>.</summary>
    /// <param name="id">UUID v7 identifier of the record to update.</param>
    /// <param name="model">Updated record data.</param>
    /// <param name="ct">Cancellation token.</param>
    new Task<Calibration> UpdateAsync(Guid id, Calibration model, CancellationToken ct = default);

    /// <summary>Delete a <see cref="Calibration"/> by its UUID v7.</summary>
    /// <param name="id">UUID v7 identifier of the record to delete.</param>
    /// <param name="ct">Cancellation token.</param>
    new Task DeleteAsync(Guid id, CancellationToken ct = default);

    /// <summary>Delete the <see cref="Calibration"/> with the given legacy MongoDB ObjectId.</summary>
    /// <param name="legacyId">Original MongoDB ObjectId string.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Number of records deleted (0 or 1).</returns>
    Task<int> DeleteByLegacyIdAsync(string legacyId, CancellationToken ct = default);

    /// <summary>Count <see cref="Calibration"/> records within an optional time range.</summary>
    /// <param name="from">Inclusive start, or <c>null</c> for no lower bound.</param>
    /// <param name="to">Exclusive end, or <c>null</c> for no upper bound.</param>
    /// <param name="ct">Cancellation token.</param>
    new Task<int> CountAsync(DateTime? from, DateTime? to, CancellationToken ct = default);

    /// <summary>Retrieve all <see cref="Calibration"/> records sharing the same correlation identifier.</summary>
    /// <param name="correlationId">Correlation ID linking related records.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IEnumerable<Calibration>> GetByCorrelationIdAsync(Guid correlationId, CancellationToken ct = default);

    /// <summary>
    /// Retrieve the timestamp of the most recently stored <see cref="Calibration"/>, optionally scoped to a data source.
    /// </summary>
    /// <param name="source">Optional data source filter. Pass <c>null</c> to search across all sources.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The latest timestamp, or <c>null</c> if no records exist.</returns>
    Task<DateTime?> GetLatestTimestampAsync(string? source = null, CancellationToken ct = default);

    /// <summary>
    /// Retrieve the timestamp of the oldest stored <see cref="Calibration"/>, optionally scoped to a data source.
    /// </summary>
    /// <param name="source">Optional data source filter. Pass <c>null</c> to search across all sources.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The oldest timestamp, or <c>null</c> if no records exist.</returns>
    Task<DateTime?> GetOldestTimestampAsync(string? source = null, CancellationToken ct = default);

    /// <summary>
    /// Delete all <see cref="Calibration"/> records matching the given data source.
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
