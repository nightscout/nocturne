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
public class MemoryCacheService : ICacheService, IDisposable
{
    private readonly IMemoryCache _memoryCache;
    private readonly CacheConfiguration _config;
    private readonly ILogger<MemoryCacheService> _logger;
    private readonly ConcurrentDictionary<string, byte> _trackedKeys;
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
        _trackedKeys = new ConcurrentDictionary<string, byte>();
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

            var options = new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = ttl };

            // Add eviction callback to remove from tracking
            options.RegisterPostEvictionCallback(
                (key, value, reason, state) =>
                {
                    if (key is string keyStr)
                    {
                        _trackedKeys.TryRemove(keyStr, out _);
                    }
                }
            );

            _memoryCache.Set(fullKey, value, options);
            _trackedKeys.TryAdd(fullKey, 0);

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

            var options = new MemoryCacheEntryOptions { AbsoluteExpiration = expiresAt };

            // Add eviction callback to remove from tracking
            options.RegisterPostEvictionCallback(
                (key, value, reason, state) =>
                {
                    if (key is string keyStr)
                    {
                        _trackedKeys.TryRemove(keyStr, out _);
                    }
                }
            );

            _memoryCache.Set(fullKey, value, options);
            _trackedKeys.TryAdd(fullKey, 0);

            _logger.LogDebug("Cached value for key: {Key} expires at: {ExpiresAt}", key, expiresAt);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error caching value for key: {Key}", key);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        try
        {
            var fullKey = GetFullKey(key);
            _memoryCache.Remove(fullKey);
            _trackedKeys.TryRemove(fullKey, out _);
            _logger.LogDebug("Removed cache entry for key: {Key}", key);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error removing cache entry for key: {Key}", key);
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
            var keysToRemove = new List<string>();

            // Find all keys that match the pattern
            foreach (var kvp in _trackedKeys)
            {
                if (regex.IsMatch(kvp.Key))
                {
                    keysToRemove.Add(kvp.Key);
                }
            }

            // Remove matching keys
            foreach (var key in keysToRemove)
            {
                _memoryCache.Remove(key);
                _trackedKeys.TryRemove(key, out _);
            }

            _logger.LogDebug(
                "Removed {Count} cache entries matching pattern: {Pattern}",
                keysToRemove.Count,
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

    /// <summary>
    /// Creates a regex pattern from a cache key pattern with wildcards
    /// </summary>
    private static Regex CreateRegexFromPattern(string pattern)
    {
        // Escape regex special characters except * and ?
        var escaped = Regex.Escape(pattern);
        // Replace escaped wildcards with regex equivalents
        escaped = escaped.Replace("\\*", ".*").Replace("\\?", ".");
        return new Regex($"^{escaped}$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    }

    public void Dispose()
    {
        // Nothing to dispose - IMemoryCache is managed by DI container
    }
}
