using System.Collections.Concurrent;
using Nocturne.Core.Contracts.Health;
using Nocturne.Core.Models.Health;

namespace Nocturne.API.Services.Health.GoogleHealth;

public sealed class GoogleHealthCoordinator(IGoogleHealthCoordinatorStore store) : IGoogleHealthSyncCoordinator
{
    private const int OperationLock = 0x47484f;
    private const int StateLock = 0x474853;
    private const int WorkerLock = 0x474857;
    public sealed record DisconnectRequest(Guid RequestId, Guid SubjectId);
    internal sealed record Flow(string State, string Verifier, Guid SubjectId, DateTimeOffset Expires);
    public sealed record SyncProgress(GoogleHealthSyncPhase Phase, string? DataType,
        int CompletedDataTypes, int TotalDataTypes, int PagesRead, bool WorkerOwned = false);

    private readonly ConcurrentDictionary<(Guid, int), SemaphoreSlim> gates = new();

    internal Task<DisconnectRequest?> DisconnectRequestAsync(Guid tenantId, CancellationToken ct) =>
        store.ReadDisconnectAsync(tenantId, ct);

    internal async Task<Guid> RequestDisconnectAsync(Guid tenantId, Guid subject, CancellationToken ct)
    {
        await using var state = await AcquireKeyAsync(tenantId, StateLock, ct);
        if (await store.ReadDisconnectAsync(tenantId, ct) is { } pending) return pending.RequestId;
        var request = new DisconnectRequest(Guid.NewGuid(), subject);
        await store.WriteDisconnectAsync(tenantId, request, ct);
        await store.NotifyAsync(tenantId, CancellationToken.None);
        return request.RequestId;
    }

    internal async Task FinishDisconnectAsync(Guid tenantId, Guid request)
    {
        await using var state = await AcquireKeyAsync(tenantId, StateLock, CancellationToken.None);
        if ((await store.ReadDisconnectAsync(tenantId, CancellationToken.None))?.RequestId != request) return;
        await store.WriteDisconnectAsync(tenantId, null, CancellationToken.None);
    }

    public Task<IGoogleHealthReadLease> WatchReadsAsync(Guid tenantId, CancellationToken ct) =>
        Task.FromResult<IGoogleHealthReadLease>(new ReadLease(this, tenantId, ct));

    // The durable marker also interrupts readers on other API replicas.
    private sealed class ReadLease : IGoogleHealthReadLease
    {
        private readonly CancellationTokenSource stopped = new();
        private readonly CancellationTokenSource read;
        private readonly Task watching;
        public CancellationToken Token => read.Token;
        public ReadLease(GoogleHealthCoordinator coordinator, Guid tenantId, CancellationToken ct)
        {
            read = CancellationTokenSource.CreateLinkedTokenSource(ct);
            watching = WatchAsync(coordinator, tenantId);
        }
        private async Task WatchAsync(GoogleHealthCoordinator coordinator, Guid tenantId)
        {
            try
            {
                while (!stopped.IsCancellationRequested)
                {
                    if (await coordinator.DisconnectRequestAsync(tenantId, stopped.Token) is not null)
                    {
                        read.Cancel();
                        return;
                    }
                    await Task.Delay(500, stopped.Token);
                }
            }
            catch (OperationCanceledException) when (stopped.IsCancellationRequested) { }
            catch { read.Cancel(); }
        }
        public async ValueTask DisposeAsync()
        {
            await stopped.CancelAsync();
            await watching;
            read.Dispose();
            stopped.Dispose();
        }
    }

    public Task<IAsyncDisposable?> AcquireAsync(Guid tenantId, CancellationToken ct, TimeSpan? timeout = null) =>
        AcquireKeyAsync(tenantId, OperationLock, ct, timeout);

    internal Task<IAsyncDisposable?> ClaimWorkerAsync(Guid tenantId, CancellationToken ct) =>
        AcquireKeyAsync(tenantId, WorkerLock, ct, TimeSpan.Zero);

    private async Task<IAsyncDisposable?> AcquireKeyAsync(Guid tenantId, int lockClass,
        CancellationToken ct, TimeSpan? timeout = null)
    {
        var gate = gates.GetOrAdd((tenantId, lockClass), _ => new(1));
        if (!await gate.WaitAsync(timeout ?? Timeout.InfiniteTimeSpan, ct)) return null;
        try
        {
            var held = await store.LockAsync(tenantId, lockClass, timeout, ct);
            if (held is null)
            {
                gate.Release();
                return null;
            }
            return new Lease(held, gate);
        }
        catch
        {
            gate.Release();
            throw;
        }
    }

    private sealed class Lease(IAsyncDisposable held, SemaphoreSlim gate) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try { await held.DisposeAsync(); }
            finally { gate.Release(); }
        }
    }

    internal async Task<SyncProgress?> ProgressAsync(Guid tenantId, CancellationToken ct)
    {
        var progress = await store.ReadProgressAsync(tenantId, ct);
        if (progress is null || progress.WorkerOwned) return progress;
        await using var operation = await AcquireAsync(tenantId, ct, TimeSpan.Zero);
        // A crashed scheduled run has no session lock; its persisted snapshot must not look active.
        return operation is null ? progress : null;
    }

    internal async Task<bool> QueueAsync(Guid tenantId, int totalDataTypes, CancellationToken ct)
    {
        await using var operation = await AcquireAsync(tenantId, ct, TimeSpan.Zero);
        if (operation is null) return false;
        await using var state = await AcquireKeyAsync(tenantId, StateLock, ct);
        if (await store.ReadDisconnectAsync(tenantId, ct) is not null) return false;
        if (await store.ReadProgressAsync(tenantId, ct) is { WorkerOwned: true }) return false;
        await store.WriteProgressAsync(tenantId, new(GoogleHealthSyncPhase.Queued, null, 0, totalDataTypes, 0, true), ct);
        await store.NotifyAsync(tenantId, ct);
        return true;
    }

    internal IAsyncEnumerable<Guid> ReadRequestsAsync(CancellationToken ct) => store.ReadRequestsAsync(ct);

    internal async Task<bool> StartQueuedAsync(Guid tenantId, CancellationToken ct)
    {
        await using var state = await AcquireKeyAsync(tenantId, StateLock, ct);
        var progress = await store.ReadProgressAsync(tenantId, ct);
        if (progress is not { WorkerOwned: true }) return false;
        await store.WriteProgressAsync(tenantId, progress with { Phase = GoogleHealthSyncPhase.Preparing }, ct);
        return true;
    }

    public async Task ReportAsync(Guid tenantId, GoogleHealthSyncPhase phase, string? dataType = null,
        int? completedDataTypes = null, int? totalDataTypes = null, int? pagesRead = null)
    {
        await using var state = await AcquireKeyAsync(tenantId, StateLock, CancellationToken.None);
        var current = await store.ReadProgressAsync(tenantId, CancellationToken.None) ?? new(phase, null, 0, 0, 0);
        await store.WriteProgressAsync(tenantId, current with
        {
            Phase = phase,
            DataType = dataType,
            CompletedDataTypes = completedDataTypes ?? current.CompletedDataTypes,
            TotalDataTypes = totalDataTypes ?? current.TotalDataTypes,
            PagesRead = pagesRead ?? current.PagesRead
        }, CancellationToken.None);
    }

    public Task CompleteScheduledAsync(Guid tenantId) => CompleteAsync(tenantId, true);

    internal async Task CompleteAsync(Guid tenantId, bool scheduledOnly = false)
    {
        await using var state = await AcquireKeyAsync(tenantId, StateLock, CancellationToken.None);
        if (scheduledOnly && await store.ReadProgressAsync(tenantId, CancellationToken.None) is { WorkerOwned: true }) return;
        await store.WriteProgressAsync(tenantId, null, CancellationToken.None);
    }
}
