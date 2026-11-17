using Nocturne.Core.Contracts.Entries;
using Nocturne.Core.Models;

namespace Nocturne.API.Services.Entries;

/// <summary>
/// Driven port for entry-specific caching. The adapter owns cache key
/// construction, TTL policy, and demo-mode isolation.
/// </summary>
/// <seealso cref="IEntryStore"/>
/// <seealso cref="EntryQuery"/>
internal interface IEntryCache
{
    /// <summary>
    /// Get cached entries for the given query, or compute and cache them.
    /// Returns <c>null</c> if the query is not cacheable, signaling the caller to
    /// go directly to the store.
    /// </summary>
    /// <param name="query">The <see cref="EntryQuery"/> that determines the cache key and filter parameters.</param>
    /// <param name="compute">Factory invoked on cache miss to produce the result that will be stored.</param>
    /// <param name="ct">Cancels the operation.</param>
    /// <returns>
    /// The cached or freshly computed list of <see cref="Entry"/> records, or <c>null</c> if
    /// the query cannot be served from cache.
    /// </returns>
    Task<IReadOnlyList<Entry>?> GetOrComputeAsync(
        EntryQuery query,
        Func<Task<IReadOnlyList<Entry>>> compute,
        CancellationToken ct = default);

    /// <summary>
    /// Get the cached current entry, or compute and cache it.
    /// </summary>
    /// <param name="compute">Factory invoked on cache miss to fetch the latest <see cref="Entry"/>.</param>
    /// <param name="ct">Cancels the operation.</param>
    /// <returns>The current <see cref="Entry"/>, or <c>null</c> if no entries exist.</returns>
    Task<Entry?> GetOrComputeCurrentAsync(
        Func<Task<Entry?>> compute,
        CancellationToken ct = default);
}
