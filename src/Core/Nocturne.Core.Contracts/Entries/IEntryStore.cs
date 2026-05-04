using Nocturne.Core.Models;

namespace Nocturne.Core.Contracts.Entries;

/// <summary>
/// Driven port for entry reads. Abstracts dual-path storage
/// (legacy entries table + V4 projected entries) behind a single interface.
/// The adapter handles read-time merging, projection, and deduplication.
/// </summary>
/// <seealso cref="IEntryCache"/>
/// <seealso cref="EntryQuery"/>
public interface IEntryStore
{
    /// <summary>
    /// Queries entries using the specified <see cref="EntryQuery"/> parameters,
    /// merging legacy and V4-projected entries behind the scenes.
    /// </summary>
    /// <param name="query">The <see cref="EntryQuery"/> filter and pagination parameters.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A read-only list of <see cref="Entry"/> records matching the query.</returns>
    Task<IReadOnlyList<Entry>> QueryAsync(EntryQuery query, CancellationToken ct = default);

    /// <summary>
    /// Returns the most recent entry for the current tenant.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The current <see cref="Entry"/>, or <c>null</c> if no entries exist.</returns>
    Task<Entry?> GetCurrentAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns a single entry by its identifier.
    /// </summary>
    /// <param name="id">The entry identifier (GUID or legacy MongoDB ObjectId).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The <see cref="Entry"/> if found, or <c>null</c>.</returns>
    Task<Entry?> GetByIdAsync(string id, CancellationToken ct = default);

    /// <summary>
    /// Checks for a duplicate entry within a time window to prevent re-ingestion
    /// of the same reading from a data source.
    /// </summary>
    /// <param name="device">Device identifier to match.</param>
    /// <param name="type">Entry type (e.g., "sgv", "mbg", "cal").</param>
    /// <param name="sgv">Sensor glucose value in mg/dL.</param>
    /// <param name="mills">Timestamp in Unix milliseconds.</param>
    /// <param name="windowMinutes">Time window in minutes to check for duplicates. Defaults to 5.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The existing <see cref="Entry"/> if a duplicate is found, or <c>null</c>.</returns>
    Task<Entry?> CheckDuplicateAsync(string? device, string type, double? sgv, long mills,
        int windowMinutes = 5, CancellationToken ct = default);

    /// <summary>
    /// Counts entries matching the optional filter and type.
    /// </summary>
    /// <param name="find">Optional MongoDB-style find query for time-range extraction.</param>
    /// <param name="type">Optional entry type filter ("sgv", "mbg", "cal").</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Total count of matching entries across all V4 repositories.</returns>
    Task<long> CountAsync(string? find = null, string? type = null, CancellationToken ct = default);
}
