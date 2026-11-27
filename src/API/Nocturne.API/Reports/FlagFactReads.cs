using System.Collections.Concurrent;
using Nocturne.API.Services.Caching;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Services;
using static Nocturne.API.Helpers.ExecutionFlow;

namespace Nocturne.API.Reports;

/// <summary>
/// The background reads that warm the keeps <see cref="FlagFactEvaluator"/> serves, for every request in the process,
/// under one limit.
/// </summary>
/// <remarks>
/// <para>
/// A request reads no flag fact itself, so the limit, sized to the core count, holds only these reads: many staff
/// opening the People page at once cannot hold more of the pool than that between them, and no request waits behind
/// them.
/// </para>
/// <para>
/// A background read outlives its request, so it starts without the request's execution context and runs in a scope
/// of its own from the root (<see cref="TenantJobScope.OpenFor(IServiceScopeFactory, TenantReader, string)"/>), for the
/// reader captured from the overview's scope for the person: it reads what that scope reads, its keep lands in that
/// reader's partition, and it never touches the request's scope. Two reads of one keep merge through the person's cache
/// (<see cref="ITenantCache.GetOrCreateLocalAsync{T}(string, Func{Task{T}}, TimeSpan, Func{T, bool}, CancellationToken)"/>),
/// before either waits on the limit.
/// </para>
/// </remarks>
internal sealed class FlagFactReads
{
    private readonly IServiceScopeFactory _scopes;
    private readonly TimeProvider _time;
    private readonly ILogger<FlagFactReads> _logger;
    private readonly SemaphoreSlim _limit;
    private readonly ConcurrentDictionary<Task, byte> _running = new();

    public FlagFactReads(IServiceScopeFactory scopes, TimeProvider time, ILogger<FlagFactReads> logger)
        : this(scopes, time, logger, Environment.ProcessorCount)
    {
    }

    internal FlagFactReads(IServiceScopeFactory scopes, TimeProvider time, ILogger<FlagFactReads> logger, int size)
    {
        _scopes = scopes;
        _time = time;
        _logger = logger;
        Size = size;
        _limit = new SemaphoreSlim(size, size);
    }

    /// <summary>The most reads that run at once.</summary>
    public int Size { get; }

    /// <summary>A read to keep for a person, and the reader and key it is kept under.</summary>
    internal sealed record Warm(
        Guid TenantId,
        TenantReader Reader,
        FlagFactCondition Condition,
        ReportReadShape Shape,
        IReadOnlySet<string> Granted,
        string Key,
        TimeSpan Life);

    /// <summary>Starts <paramref name="warms"/> in the background and returns at once.</summary>
    public void Start(IReadOnlyList<Warm> warms)
    {
        if (warms.Count == 0)
            return;

        var run = WithoutFlow(() => Task.Run(() => Task.WhenAll(warms.Select(WarmAsync))));
        _running.TryAdd(run, 0);
        WithoutFlow(() => run.ContinueWith(
            finished =>
            {
                _running.TryRemove(finished, out _);
                if (finished.Exception is { } defect)
                    _logger.LogError(defect, "A background flag fact read failed");
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default));
    }

    /// <summary>Completes when every background read started so far has finished.</summary>
    internal async Task WhenIdleAsync()
    {
        while (!_running.IsEmpty)
            await Task.WhenAll(_running.Keys).ContinueWith(_ => { }, TaskScheduler.Default);
    }

    private async Task WarmAsync(Warm warm)
    {
        await using var scope = TenantJobScope.OpenFor(_scopes, warm.Reader, FlagFactEvaluator.AuditEndpoint);
        var services = scope.ServiceProvider;
        var cache = services.GetRequiredService<ITenantCache>();
        var refresh = warm.Life * FlagFactEvaluator.RefreshAfter;
        await cache.GetOrCreateLocalAsync(
            warm.Key,
            () => LimitedAsync(() => KeepAsync(warm, services)),
            warm.Life,
            keep => _time.GetUtcNow() - keep.ReadAt < refresh,
            CancellationToken.None);
    }

    private async Task<T> LimitedAsync<T>(Func<Task<T>> read)
    {
        await _limit.WaitAsync();
        try
        {
            return await read();
        }
        finally
        {
            _limit.Release();
        }
    }

    /// <summary>The person's read, or a failed keep when it fails, logged once for the keep it makes.</summary>
    private async Task<FlagFactKeep> KeepAsync(Warm warm, IServiceProvider services)
    {
        var now = _time.GetUtcNow();
        try
        {
            return new FlagFactKeep(
                await FlagFactEvaluator.ReadAsync(warm.Condition, warm.Shape, warm.Granted, services, now, CancellationToken.None),
                now);
        }
        // The one person's read is the boundary: whatever fails it, the store or a defect, is kept as a failure for the
        // keep's life, so the person is judged failed rather than read again, and pending, on every call.
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Flag facts could not be read for tenant {TenantId}; its rules wait for the next read", warm.TenantId);
            return new FlagFactKeep(null, now);
        }
    }
}
