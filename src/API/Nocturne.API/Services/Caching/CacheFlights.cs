using System.Collections.Concurrent;
using Nocturne.Infrastructure.Cache.Abstractions;

namespace Nocturne.API.Services.Caching;

/// <summary>
/// The loads of cold cache keys in progress, so concurrent readers of one full key run one load
/// between them. A singleton: a <see cref="TenantCache"/> key already names tenant, partition and
/// demo segment, so two partitions never share a run.
/// </summary>
/// <remarks>
/// A load's factory captures its own caller's request scope and token, so the caller that starts
/// a run awaits its own factory, never anyone else's. A joiner that sees the run cancelled by a
/// token that is not its own starts again; any other failure reaches every joiner.
/// </remarks>
public sealed class CacheFlights
{
    private readonly ConcurrentDictionary<(string Key, Type Type), Task> _flights = new();

    /// <summary>
    /// Reads <paramref name="key"/> from <paramref name="cache"/>, and on a miss runs one
    /// <paramref name="load"/> between concurrent readers and caches its value for the TTL it returns.
    /// </summary>
    public async Task<T?> GetOrSetAsync<T>(
        ICacheService cache, string key, Func<Task<CacheResult<T>>> load, CancellationToken ct) where T : class =>
        await cache.GetAsync<T>(key, ct)
            ?? await RunAsync(key, async () =>
            {
                if (await cache.GetAsync<T>(key, ct) is { } again)
                    return again;

                var (value, ttl) = await load();
                if (value is not null && ttl is { } expiry)
                    await cache.SetAsync(key, value, expiry, ct);
                return value;
            }, ct);

    public async Task<T> RunAsync<T>(string key, Func<Task<T>> load, CancellationToken ct)
    {
        var slot = (key, typeof(T));
        while (true)
        {
            var flight = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            var running = _flights.GetOrAdd(slot, flight.Task);
            if (running != flight.Task)
            {
                try
                {
                    return await ((Task<T>)running).WaitAsync(ct);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    continue;
                }
            }

            T value;
            try
            {
                value = await load();
            }
            catch (Exception ex)
            {
                _flights.TryRemove(KeyValuePair.Create(slot, (Task)flight.Task));
                flight.SetException(ex);
                return await flight.Task;
            }

            _flights.TryRemove(KeyValuePair.Create(slot, (Task)flight.Task));
            flight.SetResult(value);
            return value;
        }
    }
}
