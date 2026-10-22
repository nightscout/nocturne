using Microsoft.Extensions.Caching.Memory;
using Nocturne.API.Services.Platform;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Infrastructure.Cache.Abstractions;

namespace Nocturne.API.Services.Caching;

/// <inheritdoc cref="ITenantCache"/>
public sealed class TenantCache(
    ICacheService distributed,
    IMemoryCache local,
    ITenantAccessor tenantAccessor,
    IReaderCachePartition partition,
    IDemoModeService demoMode) : ITenantCache
{
    private string DemoSegment => demoMode.IsEnabled ? "demo" : "live";

    /// <summary>The full key, or <c>null</c> when there is no tenant or the reader has no partition.</summary>
    private string? KeyFor(string key) =>
        tenantAccessor.Context is { } tenant && partition.Current is { } current
            ? $"{tenant.TenantId:D}:{current}:{DemoSegment}:{key}"
            : null;

    public async Task<T?> GetAsync<T>(string key, CancellationToken ct = default) where T : class =>
        KeyFor(key) is { } full ? await distributed.GetAsync<T>(full, ct) : null;

    public Task SetAsync<T>(string key, T value, TimeSpan ttl, CancellationToken ct = default) where T : class =>
        KeyFor(key) is { } full ? distributed.SetAsync(full, value, ttl, ct) : Task.CompletedTask;

    public Task SetAsync<T>(string key, T value, DateTimeOffset expiresAt, CancellationToken ct = default)
        where T : class =>
        KeyFor(key) is { } full ? distributed.SetAsync(full, value, expiresAt, ct) : Task.CompletedTask;

    public async Task<T> GetOrSetAsync<T>(string key, Func<Task<T>> factory, TimeSpan ttl, CancellationToken ct = default)
        where T : class =>
        KeyFor(key) is { } full ? await distributed.GetOrSetAsync(full, factory, ttl, ct) : await factory();

    public Task RemoveFamilyAsync(string family, CancellationToken ct = default) =>
        tenantAccessor.Context is { } tenant
            ? distributed.RemoveByPatternAsync($"{tenant.TenantId:D}:*:*:{family}:*", ct)
            : Task.CompletedTask;

    public Task RemoveTenantAsync(Guid tenantId, CancellationToken ct = default) =>
        distributed.RemoveByPatternAsync($"{tenantId:D}:*", ct);

    public bool TryGetLocal<T>(string key, out T? value)
    {
        if (KeyFor(key) is { } full)
            return local.TryGetValue(full, out value);

        value = default;
        return false;
    }

    public void SetLocal<T>(string key, T value, TimeSpan ttl)
    {
        if (KeyFor(key) is { } full)
            local.Set(full, value, ttl);
    }

    public async Task<T> GetOrCreateLocalAsync<T>(string key, Func<Task<T>> factory, TimeSpan ttl)
    {
        if (TryGetLocal<T>(key, out var cached))
            return cached!;

        var value = await factory();
        SetLocal(key, value, ttl);
        return value;
    }
}
