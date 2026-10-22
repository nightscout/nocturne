using Nocturne.Core.Models;

namespace Nocturne.Core.Contracts.Treatments;

/// <summary>
/// Driven port for treatment-specific caching. The adapter owns cache key
/// construction, TTL policy, and demo-mode isolation.
/// </summary>
/// <seealso cref="ITreatmentStore"/>
/// <seealso cref="TreatmentQuery"/>
public interface ITreatmentCache
{
    /// <summary>
    /// Get cached treatments for the given query, or compute and cache them.
    /// Returns <c>null</c> if the query is not cacheable, signaling the caller to
    /// go directly to the store.
    /// </summary>
    /// <param name="query">The <see cref="TreatmentQuery"/> that determines the cache key and filter parameters.</param>
    /// <param name="compute">Factory invoked on cache miss to produce the result that will be stored.</param>
    /// <returns>
    /// The cached or freshly computed list of <see cref="Treatment"/> records, or <c>null</c> if
    /// the query cannot be served from cache.
    /// </returns>
    Task<IReadOnlyList<Treatment>?> GetOrComputeAsync(
        TreatmentQuery query,
        Func<Task<IReadOnlyList<Treatment>>> compute,
        CancellationToken ct = default);

    /// <summary>
    /// Invalidate all cached treatment data for the current tenant.
    /// </summary>
    Task InvalidateAsync(CancellationToken ct = default);
}
