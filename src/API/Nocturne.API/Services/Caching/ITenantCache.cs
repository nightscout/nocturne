using Nocturne.Core.Contracts.Multitenancy;

namespace Nocturne.API.Services.Caching;

/// <summary>
/// The one cache for data read under a tenant's row level security. Every key is
/// <c>tenant:partition:demo:key</c>, with the partition from <see cref="IReaderCachePartition"/>, so
/// a reader is only ever served what a reader who sees the same rows computed. Without a resolved
/// tenant or a partition, every read misses and every write is dropped.
/// </summary>
/// <remarks>
/// Owners, members, guests and tokens all land in the same partition, whatever their scopes. A
/// cached value must therefore be computed from data every reader of its partition may see, and
/// any narrowing by the caller's scopes happens after the cache read, never inside the factory
/// (see #2138).
/// <para>
/// The distributed tier sits on <see cref="Nocturne.Infrastructure.Cache.Abstractions.ICacheService"/>
/// and is what <see cref="RemoveFamilyAsync(string, CancellationToken)"/> clears. The local tier
/// sits on <see cref="Microsoft.Extensions.Caching.Memory.IMemoryCache"/>, holds any value
/// including <c>null</c> and value types, and is never invalidated, so it suits only TTLs short
/// enough to absorb a write.
/// </para>
/// <para>
/// Concurrent misses of one key in either tier run one factory between them (<see cref="CacheFlights"/>).
/// <c>ct</c> bounds only the caller's own wait and its own cache I/O; a factory honours the token it captured.
/// </para>
/// <para>How broadly each write path clears is set out on <see cref="ClearsTenantCacheAttribute"/>.</para>
/// </remarks>
public interface ITenantCache
{
    Task<T> GetOrSetAsync<T>(string key, Func<Task<T>> factory, TimeSpan ttl, CancellationToken ct = default)
        where T : class;

    /// <summary>
    /// As <see cref="GetOrSetAsync{T}(string, Func{Task{T}}, TimeSpan, CancellationToken)"/>, but the
    /// factory chooses how long its value is cached, or that it is not cached. A <c>null</c>
    /// <see cref="CacheResult{T}.Value"/> throws <see cref="InvalidOperationException"/>.
    /// </summary>
    Task<T> GetOrSetAsync<T>(string key, Func<Task<CacheResult<T>>> factory, CancellationToken ct = default)
        where T : class;

    /// <summary>
    /// As <see cref="GetOrSetAsync{T}(string, Func{Task{T}}, TimeSpan, CancellationToken)"/>, but a
    /// <c>null</c> from <paramref name="factory"/> is returned and not cached, so the next read runs a
    /// factory again.
    /// </summary>
    Task<T?> GetOrSetUnlessNullAsync<T>(string key, Func<Task<T?>> factory, TimeSpan ttl, CancellationToken ct = default)
        where T : class;

    /// <summary>
    /// Removes every distributed-tier key of the current tenant that starts with
    /// <paramref name="family"/> followed by <c>:</c>, in every partition and demo segment.
    /// </summary>
    Task RemoveFamilyAsync(string family, CancellationToken ct = default);

    /// <summary>Removes every distributed-tier key of <paramref name="tenantId"/>.</summary>
    Task RemoveTenantAsync(Guid tenantId, CancellationToken ct = default);

    bool TryGetLocal<T>(string key, out T? value);

    void SetLocal<T>(string key, T value, TimeSpan ttl);

    Task<T> GetOrCreateLocalAsync<T>(string key, Func<Task<T>> factory, TimeSpan ttl, CancellationToken ct = default);
}
