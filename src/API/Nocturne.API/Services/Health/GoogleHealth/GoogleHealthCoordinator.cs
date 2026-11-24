using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Nocturne.Core.Contracts.Health;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Health;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Services.Health.GoogleHealth;

/// <summary>
/// Serialises each tenant's Google Health work in this process. Import progress and a pending
/// disconnect are kept in <see cref="IGoogleHealthWorkStore"/>, so the worker resumes them after a restart.
/// </summary>
public sealed class GoogleHealthCoordinator(IServiceScopeFactory scopes, TimeProvider clock) : IGoogleHealthSyncCoordinator
{
    private const string AuditEndpoint = "service:google-health";

    // Besides finding work left by a restart, the rescan retries a queued import whose run failed, and
    // one whose wake-up arrived while the worker still held that tenant's claim.
    internal static readonly TimeSpan RescanInterval = TimeSpan.FromMinutes(15);

    private enum Gate { Operation, State, Worker }

    internal sealed record Flow(string State, string Verifier, Guid SubjectId, DateTimeOffset Expires,
        string? ClientId = null, string? RedirectUri = null);

    private readonly ConcurrentDictionary<(Guid, Gate), SemaphoreSlim> gates = new();
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> interrupts = new();
    private readonly Channel<Guid> requests = Channel.CreateUnbounded<Guid>();

    internal async Task<GoogleHealthDisconnectRequest?> DisconnectRequestAsync(Guid tenantId, CancellationToken ct) =>
        (await ReadAsync(tenantId, ct)).Disconnect;

    internal async Task<Guid> RequestDisconnectAsync(Guid tenantId, Guid subject, CancellationToken ct)
    {
        await using var state = await AcquireKeyAsync(tenantId, Gate.State, ct);
        if (await DisconnectRequestAsync(tenantId, ct) is { } pending) return pending.RequestId;
        var request = new GoogleHealthDisconnectRequest(Guid.NewGuid(), subject);
        await WriteAsync(tenantId, (store, token) => store.SetDisconnectAsync(request, token), ct);
        if (interrupts.TryRemove(tenantId, out var readers)) await readers.CancelAsync();
        requests.Writer.TryWrite(tenantId);
        return request.RequestId;
    }

    internal async Task FinishDisconnectAsync(Guid tenantId, Guid request)
    {
        await using var state = await AcquireKeyAsync(tenantId, Gate.State, CancellationToken.None);
        if ((await DisconnectRequestAsync(tenantId, CancellationToken.None))?.RequestId != request) return;
        await WriteAsync(tenantId, (store, token) => store.SetDisconnectAsync(null, token), CancellationToken.None);
    }

    public async Task<IGoogleHealthReadLease> WatchReadsAsync(Guid tenantId, CancellationToken ct)
    {
        var read = CancellationTokenSource.CreateLinkedTokenSource(
            ct, interrupts.GetOrAdd(tenantId, _ => new()).Token);
        try
        {
            // Linked before the marker is read, so a disconnect requested in between still interrupts it.
            if (await DisconnectRequestAsync(tenantId, ct) is not null) await read.CancelAsync();
            return new ReadLease(read);
        }
        catch
        {
            read.Dispose();
            throw;
        }
    }

    private sealed class ReadLease(CancellationTokenSource read) : IGoogleHealthReadLease
    {
        public CancellationToken Token => read.Token;

        public ValueTask DisposeAsync()
        {
            read.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    public Task<IAsyncDisposable?> AcquireAsync(Guid tenantId, CancellationToken ct, TimeSpan? timeout = null) =>
        AcquireKeyAsync(tenantId, Gate.Operation, ct, timeout);

    internal Task<IAsyncDisposable?> ClaimWorkerAsync(Guid tenantId, CancellationToken ct) =>
        AcquireKeyAsync(tenantId, Gate.Worker, ct, TimeSpan.Zero);

    private async Task<IAsyncDisposable?> AcquireKeyAsync(Guid tenantId, Gate kind,
        CancellationToken ct, TimeSpan? timeout = null)
    {
        var gate = gates.GetOrAdd((tenantId, kind), _ => new(1));
        return await gate.WaitAsync(timeout ?? Timeout.InfiniteTimeSpan, ct) ? new Lease(gate) : null;
    }

    private sealed class Lease(SemaphoreSlim gate) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            gate.Release();
            return ValueTask.CompletedTask;
        }
    }

    internal async Task<GoogleHealthSyncProgress?> ProgressAsync(Guid tenantId, CancellationToken ct)
    {
        var progress = await ReadProgressAsync(tenantId, ct);
        if (progress is null || progress.WorkerOwned) return progress;
        await using var operation = await AcquireAsync(tenantId, ct, TimeSpan.Zero);
        // A crashed scheduled run holds no operation lock; its persisted snapshot must not look active.
        return operation is null ? progress : null;
    }

    internal async Task<bool> QueueAsync(Guid tenantId, int totalDataTypes, CancellationToken ct)
    {
        await using var operation = await AcquireAsync(tenantId, ct, TimeSpan.Zero);
        if (operation is null) return false;
        await using var state = await AcquireKeyAsync(tenantId, Gate.State, ct);
        if (await ReadAsync(tenantId, ct) is { Progress.WorkerOwned: true } or { Disconnect: not null }) return false;
        await WriteProgressAsync(tenantId, new(GoogleHealthSyncPhase.Queued, null, 0, totalDataTypes, 0, true), ct);
        requests.Writer.TryWrite(tenantId);
        return true;
    }

    /// <summary>Tenants with queued work: those pending at each rescan, and each one queued since.</summary>
    internal async IAsyncEnumerable<Guid> ReadRequestsAsync([EnumeratorCancellation] CancellationToken ct)
    {
        while (true)
        {
            await foreach (var tenant in PendingTenantsAsync(ct)) yield return tenant;
            using var rescan = new CancellationTokenSource(RescanInterval, clock);
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct, rescan.Token);
            while (true)
            {
                Guid tenant;
                try { tenant = await requests.Reader.ReadAsync(wait.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { break; }
                yield return tenant;
            }
        }
    }

    private async IAsyncEnumerable<Guid> PendingTenantsAsync([EnumeratorCancellation] CancellationToken ct)
    {
        IReadOnlyList<TenantDto> tenants;
        using (var scope = scopes.CreateScope())
            tenants = await scope.ServiceProvider.GetRequiredService<ITenantService>().GetAllAsync(ct);
        foreach (var tenant in tenants.Where(tenant => tenant.IsActive))
            if (await ReadAsync(tenant.Id, ct) is { Progress.WorkerOwned: true } or { Disconnect: not null })
                yield return tenant.Id;
    }

    internal async Task<bool> StartQueuedAsync(Guid tenantId, CancellationToken ct)
    {
        await using var state = await AcquireKeyAsync(tenantId, Gate.State, ct);
        var progress = await ReadProgressAsync(tenantId, ct);
        if (progress is not { WorkerOwned: true }) return false;
        await WriteProgressAsync(tenantId, progress with { Phase = GoogleHealthSyncPhase.Preparing }, ct);
        return true;
    }

    public async Task ReportAsync(Guid tenantId, GoogleHealthSyncPhase phase, string? dataType = null,
        int? completedDataTypes = null, int? totalDataTypes = null, int? pagesRead = null)
    {
        await using var state = await AcquireKeyAsync(tenantId, Gate.State, CancellationToken.None);
        var current = await ReadProgressAsync(tenantId, CancellationToken.None) ?? new(phase, null, 0, 0, 0);
        await WriteProgressAsync(tenantId, current with
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
        await using var state = await AcquireKeyAsync(tenantId, Gate.State, CancellationToken.None);
        if (scheduledOnly && await ReadProgressAsync(tenantId, CancellationToken.None) is { WorkerOwned: true }) return;
        await WriteProgressAsync(tenantId, null, CancellationToken.None);
    }

    private async Task<GoogleHealthSyncProgress?> ReadProgressAsync(Guid tenantId, CancellationToken ct) =>
        (await ReadAsync(tenantId, ct)).Progress;

    private Task WriteProgressAsync(Guid tenantId, GoogleHealthSyncProgress? progress, CancellationToken ct) =>
        WriteAsync(tenantId, (store, token) => store.SetProgressAsync(progress, token), ct);

    private async Task<GoogleHealthWork> ReadAsync(Guid tenantId, CancellationToken ct)
    {
        await using var scope = TenantJobScope.Open(scopes, new TenantContext(tenantId, "", "", true, false), AuditEndpoint);
        return await scope.ServiceProvider.GetRequiredService<IGoogleHealthWorkStore>().GetAsync(ct);
    }

    private async Task WriteAsync(
        Guid tenantId, Func<IGoogleHealthWorkStore, CancellationToken, Task> write, CancellationToken ct)
    {
        await using var scope = TenantJobScope.Open(scopes, new TenantContext(tenantId, "", "", true, false), AuditEndpoint);
        await write(scope.ServiceProvider.GetRequiredService<IGoogleHealthWorkStore>(), ct);
    }
}
