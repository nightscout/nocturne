using Nocturne.Core.Models;
using Nocturne.Core.Models.Queries;

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
    /// Finds the stored entry an upload would duplicate: one of the same type, from the same
    /// device, at the same millisecond.
    /// </summary>
    /// <remarks>
    /// A duplicate has the same type, device and millisecond, whatever its value. Nightscout keys an
    /// entry on its exact time and type, and uploaders post one reading at a time, so on a flat
    /// trace consecutive readings carry the same value and any window would discard real ones.
    /// </remarks>
    /// <param name="device">Device identifier to match, or <c>null</c> to match any device.</param>
    /// <param name="type">Entry type (e.g., "sgv", "mbg", "cal").</param>
    /// <param name="mills">Timestamp in Unix milliseconds.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The existing <see cref="Entry"/> if a duplicate is found, or <c>null</c>.</returns>
    Task<Entry?> CheckDuplicateAsync(string? device, string type, long mills, CancellationToken ct = default);

    /// <summary>
    /// Checks a whole upload batch for duplicates, returning one result per probe in the order
    /// given (<c>null</c> where nothing is stored).
    /// </summary>
    /// <remarks>
    /// Classification is identical to <see cref="CheckDuplicateAsync"/> per probe. For <c>sgv</c> —
    /// the only type that arrives in thousands-per-cycle uploads — the stored readings covering the
    /// batch are loaded in one query instead of a query per entry, so a 1,000-entry upload costs
    /// one query rather than a thousand, each of which carried its own context, connection lease
    /// and plan. Other types are probed one at a time.
    /// </remarks>
    /// <param name="probes">The entries to classify, in submission order.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Positionally aligned results: the stored <see cref="Entry"/>, or <c>null</c>.</returns>
    Task<IReadOnlyList<Entry?>> CheckDuplicatesAsync(
        IReadOnlyList<EntryDuplicateProbe> probes, CancellationToken ct = default);

    /// <summary>
    /// Counts entries matching the optional filter and type.
    /// </summary>
    /// <param name="find">Optional MongoDB-style find query for time-range extraction.</param>
    /// <param name="type">Optional entry type filter ("sgv", "mbg", "cal").</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Total count of matching entries across all V4 repositories.</returns>
    Task<long> CountAsync(string? find = null, string? type = null, CancellationToken ct = default);

    /// <summary>
    /// Entries whose <c>srvModified</c> falls after <paramref name="cursorMills"/>, oldest
    /// modification first, for the v3 <c>history/{lastModified}</c> endpoint. Readings a regular
    /// read hides (demo rows, losing canonical streams) are withheld but still advance the cursor.
    /// </summary>
    /// <param name="cursorMills">The client's cursor, in Unix milliseconds.</param>
    /// <param name="limit">The page size; a page may exceed it to finish its last millisecond.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<ModifiedSincePage<Entry>> GetModifiedSinceAsync(long cursorMills, int limit, CancellationToken ct = default);
}
