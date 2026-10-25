using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.GoogleHealth.Services;
using Nocturne.Core.Contracts.Health;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Health;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Services.Health.GoogleHealth;

/// <summary>
/// Serialises each tenant's Google Health work in this process. Import progress and a pending
/// disconnect are kept in the connector's sync cursors, so the worker resumes them after a restart.
/// </summary>
public sealed class GoogleHealthCoordinator(IServiceScopeFactory scopes, TimeProvider clock) : IGoogleHealthSyncCoordinator
{
    private const string AuditEndpoint = "service:google-health";
    private const string ProgressKey = "googleHealthProgress";
    private const string DisconnectKey = "googleHealthDisconnect";

    // Besides finding work left by a restart, the rescan retries a queued import whose run failed, and
    // one whose wake-up arrived while the worker still held that tenant's claim.
    internal static readonly TimeSpan RescanInterval = TimeSpan.FromMinutes(15);

    private enum Gate { Operation, State, Worker }

    public sealed record DisconnectRequest(Guid RequestId, Guid SubjectId);
    internal sealed record Flow(string State, string Verifier, Guid SubjectId, DateTimeOffset Expires);
    public sealed record SyncProgress(GoogleHealthSyncPhase Phase, string? DataType,
        int CompletedDataTypes, int TotalDataTypes, int PagesRead, bool WorkerOwned = false);

    private readonly ConcurrentDictionary<(Guid, Gate), SemaphoreSlim> gates = new();
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> interrupts = new();
    private readonly Channel<Guid> requests = Channel.CreateUnbounded<Guid>();

    internal async Task<DisconnectRequest?> DisconnectRequestAsync(Guid tenantId, CancellationToken ct) =>
        (await ReadAsync(tenantId, DisconnectKey, ct))?.Deserialize<DisconnectRequest>();

    internal async Task<Guid> RequestDisconnectAsync(Guid tenantId, Guid subject, CancellationToken ct)
    {
        await using var state = await AcquireKeyAsync(tenantId, Gate.State, ct);
        if (await DisconnectRequestAsync(tenantId, ct) is { } pending) return pending.RequestId;
        var request = new DisconnectRequest(Guid.NewGuid(), subject);
        await WriteAsync(tenantId, DisconnectKey, JsonSerializer.SerializeToNode(request), ct);
        if (interrupts.TryRemove(tenantId, out var readers)) await readers.CancelAsync();
        requests.Writer.TryWrite(tenantId);
        return request.RequestId;
    }

    internal async Task FinishDisconnectAsync(Guid tenantId, Guid request)
    {
        await using var state = await AcquireKeyAsync(tenantId, Gate.State, CancellationToken.None);
        if ((await DisconnectRequestAsync(tenantId, CancellationToken.None))?.RequestId != request) return;
        await WriteAsync(tenantId, DisconnectKey, null, CancellationToken.None);
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

    internal async Task<SyncProgress?> ProgressAsync(Guid tenantId, CancellationToken ct)
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
        if (await DisconnectRequestAsync(tenantId, ct) is not null) return false;
        if (await ReadProgressAsync(tenantId, ct) is { WorkerOwned: true }) return false;
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
            if (await ReadProgressAsync(tenant.Id, ct) is { WorkerOwned: true } ||
                await DisconnectRequestAsync(tenant.Id, ct) is not null)
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

    private async Task<SyncProgress?> ReadProgressAsync(Guid tenantId, CancellationToken ct)
    {
        var json = await ReadAsync(tenantId, ProgressKey, ct);
        try
        {
            var progress = json?.Deserialize<SyncProgress>();
            return progress is not null && Enum.IsDefined(progress.Phase) ? progress : null;
        }
        catch (JsonException)
        {
            // A corrupt progress snapshot must not block status or replacement queue requests.
            return null;
        }
    }

    private Task WriteProgressAsync(Guid tenantId, SyncProgress? progress, CancellationToken ct) =>
        WriteAsync(tenantId, ProgressKey, progress is null ? null : JsonSerializer.SerializeToNode(progress), ct);

    private async Task<JsonNode?> ReadAsync(Guid tenantId, string key, CancellationToken ct)
    {
        await using var scope = TenantJobScope.Open(scopes, new TenantContext(tenantId, "", "", true, false), AuditEndpoint);
        return await scope.ServiceProvider.GetRequiredService<IConnectorSyncCursorStore>()
            .GetJsonAsync(GoogleHealthConnectorService.ConnectorName, key, ct);
    }

    private async Task WriteAsync(Guid tenantId, string key, JsonNode? value, CancellationToken ct)
    {
        await using var scope = TenantJobScope.Open(scopes, new TenantContext(tenantId, "", "", true, false), AuditEndpoint);
        var cursors = scope.ServiceProvider.GetRequiredService<IConnectorSyncCursorStore>();
        if (value is null) await cursors.RemoveAsync(GoogleHealthConnectorService.ConnectorName, key, ct);
        else await cursors.SetJsonAsync(GoogleHealthConnectorService.ConnectorName, key, value, ct);
    }
}
