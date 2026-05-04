using Nocturne.Core.Models.V4;
using Nocturne.Core.Contracts.Glucose;

namespace Nocturne.Core.Contracts.V4.Repositories;

/// <summary>
/// Repository for <see cref="TempBasal"/> records representing temporary basal rate overrides
/// issued by a pump or AID algorithm.
/// </summary>
/// <remarks>
/// <see cref="TempBasal"/> records are also used as the underlying store for the legacy V1/V3
/// temp basal treatment projection. Unlike most V4 repositories, this interface does not extend
/// <see cref="IV4Repository{T}"/> directly because it needs a source-and-date-range delete operation
/// used during connector re-sync.
/// </remarks>
/// <seealso cref="TempBasal"/>
/// <seealso cref="IIobService"/>
/// <seealso cref="IStateSpanService"/>
public interface ITempBasalRepository
{
    /// <summary>Retrieve a page of <see cref="TempBasal"/> records filtered by time range, device, and source.</summary>
    /// <param name="from">Inclusive start of the time window, or <c>null</c> for no lower bound.</param>
    /// <param name="to">Exclusive end of the time window, or <c>null</c> for no upper bound.</param>
    /// <param name="device">Optional device identifier filter.</param>
    /// <param name="source">Optional data source filter (e.g., connector name).</param>
    /// <param name="limit">Maximum number of records to return (default 100).</param>
    /// <param name="offset">Number of records to skip for pagination (default 0).</param>
    /// <param name="descending">When <c>true</c>, results are ordered newest-first (default).</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IEnumerable<TempBasal>> GetAsync(
        DateTime? from,
        DateTime? to,
        string? device,
        string? source,
        int limit = 100,
        int offset = 0,
        bool descending = true,
        CancellationToken ct = default
    );

    /// <summary>Returns a single <see cref="TempBasal"/> by its UUID v7, or <c>null</c> if not found.</summary>
    /// <param name="id">UUID v7 record identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<TempBasal?> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>Retrieve a <see cref="TempBasal"/> by its original MongoDB ObjectId.</summary>
    /// <param name="legacyId">Original MongoDB ObjectId string.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The matching record, or <c>null</c> if not found.</returns>
    Task<TempBasal?> GetByLegacyIdAsync(string legacyId, CancellationToken ct = default);

    /// <summary>Persist a new <see cref="TempBasal"/> and return the saved entity.</summary>
    /// <param name="model">Record to create.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<TempBasal> CreateAsync(TempBasal model, CancellationToken ct = default);

    /// <summary>Replace an existing <see cref="TempBasal"/> identified by <paramref name="id"/>.</summary>
    /// <param name="id">UUID v7 identifier of the record to update.</param>
    /// <param name="model">Updated record data.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<TempBasal> UpdateAsync(Guid id, TempBasal model, CancellationToken ct = default);

    /// <summary>Delete a <see cref="TempBasal"/> by its UUID v7.</summary>
    /// <param name="id">UUID v7 identifier of the record to delete.</param>
    /// <param name="ct">Cancellation token.</param>
    Task DeleteAsync(Guid id, CancellationToken ct = default);

    /// <summary>Delete the <see cref="TempBasal"/> with the given legacy MongoDB ObjectId.</summary>
    /// <param name="legacyId">Original MongoDB ObjectId string.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Number of records deleted (0 or 1).</returns>
    Task<int> DeleteByLegacyIdAsync(string legacyId, CancellationToken ct = default);

    /// <summary>Count <see cref="TempBasal"/> records within an optional time range.</summary>
    /// <param name="from">Inclusive start, or <c>null</c> for no lower bound.</param>
    /// <param name="to">Exclusive end, or <c>null</c> for no upper bound.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<int> CountAsync(DateTime? from, DateTime? to, CancellationToken ct = default);

    /// <summary>Insert multiple <see cref="TempBasal"/> records in a single batch operation.</summary>
    /// <param name="records">Records to insert.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The inserted records with server-assigned fields populated.</returns>
    Task<IEnumerable<TempBasal>> BulkCreateAsync(
        IEnumerable<TempBasal> records,
        CancellationToken ct = default
    );

    /// <summary>
    /// Delete all <see cref="TempBasal"/> records for a given source within a date range.
    /// </summary>
    /// <remarks>
    /// Used by connector re-sync operations to clear and replace a window of temp basal data
    /// from a specific data source without affecting records from other sources.
    /// </remarks>
    /// <param name="source">Data source identifier (e.g., connector name).</param>
    /// <param name="from">Inclusive start of the range to delete.</param>
    /// <param name="to">Exclusive end of the range to delete.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Number of records deleted.</returns>
    Task<int> DeleteBySourceAndDateRangeAsync(
        string source,
        DateTime from,
        DateTime to,
        CancellationToken ct = default
    );

    /// <summary>
    /// Returns the <see cref="TempBasal"/> active at <paramref name="at"/>
    /// (<c>StartTimestamp &lt;= at &lt; EndTimestamp</c>), or <c>null</c> if no
    /// temp is active. When multiple records overlap the instant, the one with
    /// the most recent <c>StartTimestamp</c> wins.
    /// </summary>
    /// <remarks>
    /// A <c>null</c> <see cref="TempBasal.EndTimestamp"/> represents an open-ended
    /// temp basal (still running) and is treated as active for any <paramref name="at"/>
    /// at or after its start.
    /// </remarks>
    /// <param name="at">The instant to evaluate.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<TempBasal?> GetActiveAtAsync(DateTime at, CancellationToken ct = default);
}
