using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nocturne.Infrastructure.Cache.Abstractions;
using Nocturne.Infrastructure.Cache.Configuration;

namespace Nocturne.Infrastructure.Cache.Services;

/// <summary>
/// In-memory cache service implementation for single-user deployments
/// </summary>
/// <remarks>
/// <para>
/// Pattern removal can only reach keys it tracks, so each tracked key holds the token of the entry
/// that tracked it, and an eviction or a pattern removal untracks the key only while the token is
/// still the one it saw: a write between the two steps tracks the key afresh. A replaced entry
/// never untracks, since its key now belongs to the entry that replaced it, and its eviction runs
/// on a background thread that may come after either writer has tracked the key.
/// </para>
/// <para>
/// Keys are tracked in buckets by their first <c>:</c>-separated segment, so a pattern whose first
/// segment is literal, such as a tenant id, scans only that bucket.
/// </para>
/// </remarks>
public class MemoryCacheService : ICacheService, IDisposable
{
    private readonly IMemoryCache _memoryCache;
    private readonly CacheConfiguration _config;
    private readonly ILogger<MemoryCacheService> _logger;
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, object>> _trackedKeys;
    private readonly string _keyPrefix;

    public MemoryCacheService(
        IMemoryCache memoryCache,
        IOptions<CacheConfiguration> config,
        ILogger<MemoryCacheService> logger
    )
    {
        _memoryCache = memoryCache;
        _config = config.Value;
        _logger = logger;
        _trackedKeys = new ConcurrentDictionary<string, ConcurrentDictionary<string, object>>(
            StringComparer.OrdinalIgnoreCase);
        _keyPrefix = $"{_config.KeyPrefix}:";
    }

    /// <inheritdoc />
    public Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default)
        where T : class
    {
        try
        {
            var fullKey = GetFullKey(key);
            if (_memoryCache.TryGetValue<T>(fullKey, out var cachedValue))
            {
                _logger.LogDebug("Cache hit for key: {Key}", key);
                return Task.FromResult<T?>(cachedValue);
            }

            _logger.LogDebug("Cache miss for key: {Key}", key);
            return Task.FromResult<T?>(null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving cache value for key: {Key}", key);
            return Task.FromResult<T?>(null);
        }
    }

    /// <inheritdoc />
    public Task SetAsync<T>(
        string key,
        T value,
        TimeSpan? expiration = null,
        CancellationToken cancellationToken = default
    )
        where T : class
    {
        try
        {
            var fullKey = GetFullKey(key);
            var ttl = expiration ?? TimeSpan.FromSeconds(_config.DefaultExpirationSeconds);

            SetTracked(key, fullKey, value, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = ttl });

            _logger.LogDebug("Cached value for key: {Key} with TTL: {TTL}", key, ttl);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error caching value for key: {Key}", key);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task SetAsync<T>(
        string key,
        T value,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken = default
    )
        where T : class
    {
        try
        {
            var fullKey = GetFullKey(key);

            SetTracked(key, fullKey, value, new MemoryCacheEntryOptions { AbsoluteExpiration = expiresAt });

            _logger.LogDebug("Cached value for key: {Key} expires at: {ExpiresAt}", key, expiresAt);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error caching value for key: {Key}", key);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task RemoveByPatternAsync(string pattern, CancellationToken cancellationToken = default)
    {
        try
        {
            var fullPattern = GetFullKey(pattern);
            var regex = CreateRegexFromPattern(fullPattern);
            var bucketName = Bucket(pattern);
            IEnumerable<ConcurrentDictionary<string, object>> buckets;
            if (bucketName.IndexOfAny(['*', '?']) >= 0)
                buckets = _trackedKeys.Values;
            else
                buckets = _trackedKeys.TryGetValue(bucketName, out var only) ? [only] : [];
            var removed = 0;

            foreach (var bucket in buckets)
            {
                foreach (var tracked in bucket)
                {
                    if (!regex.IsMatch(tracked.Key))
                        continue;

                    _memoryCache.Remove(tracked.Key);
                    bucket.TryRemove(tracked);
                    removed++;
                }
            }

            _logger.LogDebug(
                "Removed {Count} cache entries matching pattern: {Pattern}",
                removed,
                pattern
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error removing cache entries by pattern: {Pattern}", pattern);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default)
    {
        try
        {
            var fullKey = GetFullKey(key);
            var exists = _memoryCache.TryGetValue(fullKey, out _);
            return Task.FromResult(exists);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking cache key existence: {Key}", key);
            return Task.FromResult(false);
        }
    }

    /// <inheritdoc />
    public async Task<T> GetOrSetAsync<T>(
        string key,
        Func<Task<T>> factory,
        TimeSpan? expiration = null,
        CancellationToken cancellationToken = default
    )
        where T : class
    {
        var cachedValue = await GetAsync<T>(key, cancellationToken);
        if (cachedValue != null)
        {
            return cachedValue;
        }

        _logger.LogDebug("Cache miss for key: {Key}, executing factory function", key);
        var value = await factory();

        if (value != null)
        {
            await SetAsync(key, value, expiration, cancellationToken);
        }

        return value ?? throw new InvalidOperationException("Factory function returned null value");
    }

    private string GetFullKey(string key) => $"{_keyPrefix}{key}";

    private static string Bucket(string key)
    {
        var separator = key.IndexOf(':');
        return separator < 0 ? key : key[..separator];
    }

    private void SetTracked<T>(string key, string fullKey, T value, MemoryCacheEntryOptions options)
    {
        var bucket = _trackedKeys.GetOrAdd(Bucket(key), _ => new ConcurrentDictionary<string, object>());
        var token = new object();
        options.RegisterPostEvictionCallback(
            static (evictedKey, _, reason, state) =>
            {
                if (reason == EvictionReason.Replaced)
                    return;
                var (bucket, token) = ((ConcurrentDictionary<string, object>, object))state!;
                bucket.TryRemove(KeyValuePair.Create((string)evictedKey, token));
            },
            (bucket, token));

        _memoryCache.Set(fullKey, value, options);
        bucket[fullKey] = token;
    }

    /// <summary>
    /// Creates a regex pattern from a cache key pattern with wildcards
    /// </summary>
    private static Regex CreateRegexFromPattern(string pattern)
    {
        // Escape regex special characters except * and ?
        var escaped = Regex.Escape(pattern);
        // Replace escaped wildcards with regex equivalents
        escaped = escaped.Replace("\\*", ".*").Replace("\\?", ".");
        return new Regex($"^{escaped}$", RegexOptions.IgnoreCase);
    }

    public void Dispose()
    {
        // Nothing to dispose - IMemoryCache is managed by DI container
    }
}
