namespace Nocturne.Infrastructure.Cache.Abstractions;

/// <summary>
/// Cache service interface for Nocturne data caching
/// </summary>
public interface ICacheService
{
    /// <summary>
    /// Gets a cached item by key
    /// </summary>
    /// <typeparam name="T">Type of cached data</typeparam>
    /// <param name="key">Cache key</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Cached item or null if not found</returns>
    Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default)
        where T : class;

    /// <summary>
    /// Sets a cached item with expiration
    /// </summary>
    /// <typeparam name="T">Type of data to cache</typeparam>
    /// <param name="key">Cache key</param>
    /// <param name="value">Value to cache</param>
    /// <param name="expiration">Cache expiration time</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task SetAsync<T>(
        string key,
        T value,
        TimeSpan? expiration = null,
        CancellationToken cancellationToken = default
    )
        where T : class;

    /// <summary>
    /// Removes multiple cached items by pattern
    /// </summary>
    /// <param name="pattern">Key pattern (supports wildcards)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task RemoveByPatternAsync(string pattern, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if a key exists in cache
    /// </summary>
    /// <param name="key">Cache key</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default);
}
