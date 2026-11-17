using Microsoft.Extensions.Caching.Memory;
using Nocturne.API.Services.Platform;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Infrastructure.Cache.Abstractions;

namespace Nocturne.API.Services.Caching;

/// <inheritdoc cref="ITenantCache"/>
public sealed class TenantCache(
    ICacheService distributed,
    IMemoryCache local,
    CacheFlights flights,
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

    public Task<T> GetOrSetAsync<T>(string key, Func<Task<T>> factory, TimeSpan ttl, CancellationToken ct = default)
        where T : class =>
        GetOrSetAsync(key, async () => new CacheResult<T>(await factory(), ttl), ct);

    public async Task<T> GetOrSetAsync<T>(string key, Func<Task<CacheResult<T>>> factory, CancellationToken ct = default)
        where T : class =>
        await GetOrSetResultAsync(key, factory, ct)
            ?? throw new InvalidOperationException($"The factory for cache key {key} returned null.");

    public Task<T?> GetOrSetUnlessNullAsync<T>(
        string key, Func<Task<T?>> factory, TimeSpan ttl, CancellationToken ct = default) where T : class =>
        GetOrSetResultAsync(key, async () => new CacheResult<T>(await factory(), ttl), ct);

    private async Task<T?> GetOrSetResultAsync<T>(
        string key, Func<Task<CacheResult<T>>> factory, CancellationToken ct) where T : class =>
        KeyFor(key) is { } full
            ? await flights.GetOrSetAsync(distributed, full, factory, ct)
            : (await factory()).Value;

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

    public async Task<T> GetOrCreateLocalAsync<T>(
        string key, Func<Task<T>> factory, TimeSpan ttl, CancellationToken ct = default)
    {
        if (KeyFor(key) is not { } full)
            return await factory();

        if (local.TryGetValue(full, out T? cached))
            return cached!;

        return await flights.RunAsync($"local:{full}", async () =>
        {
            if (local.TryGetValue(full, out T? again))
                return again!;

            var value = await factory();
            local.Set(full, value, ttl);
            return value;
        }, ct);
    }
}
