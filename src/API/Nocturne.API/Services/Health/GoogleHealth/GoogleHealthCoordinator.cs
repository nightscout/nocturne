using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Health;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Health;
using Nocturne.Infrastructure.Data;
using Npgsql;

namespace Nocturne.API.Services.Health.GoogleHealth;

public sealed class GoogleHealthCoordinator : IGoogleHealthSyncCoordinator
{
    private readonly IServiceScopeFactory? scopes;
    public GoogleHealthCoordinator(IServiceScopeFactory scopes) => this.scopes = scopes;
    internal GoogleHealthCoordinator() { }
    private const int OperationLock = 0x47484f;
    private const int StateLock = 0x474853;
    private const int WorkerLock = 0x474857;
    private const string ProgressKey = "googleHealthProgress";
    private const string RequestChannel = "nocturne_google_health_requests";
    internal sealed record Flow(string State, string Verifier, Guid SubjectId, string Settings, DateTimeOffset Expires);
    internal sealed record SyncProgress(GoogleHealthSyncPhase Phase, string? DataType,
        int CompletedDataTypes, int TotalDataTypes, int PagesRead, bool WorkerOwned = false);

    private readonly ConcurrentDictionary<(Guid, int), SemaphoreSlim> gates = new();
    private readonly ConcurrentDictionary<Guid, SyncProgress> memoryProgress = new();
    private readonly Channel<Guid> requests = Channel.CreateUnbounded<Guid>();
    public SemaphoreSlim Gate(Guid tenantId) => gates.GetOrAdd((tenantId, OperationLock), _ => new(1));

    public Task<IAsyncDisposable?> AcquireAsync(Guid tenantId, CancellationToken ct, TimeSpan? timeout = null) =>
        AcquireKeyAsync(tenantId, OperationLock, ct, timeout);

    internal Task<IAsyncDisposable?> ClaimWorkerAsync(Guid tenantId, CancellationToken ct) =>
        AcquireKeyAsync(tenantId, WorkerLock, ct, TimeSpan.Zero);

    private async Task<IAsyncDisposable?> AcquireKeyAsync(Guid tenantId, int lockClass,
        CancellationToken ct, TimeSpan? timeout = null)
    {
        var gate = gates.GetOrAdd((tenantId, lockClass), _ => new(1));
        if (!await gate.WaitAsync(timeout ?? Timeout.InfiniteTimeSpan, ct)) return null;
        NpgsqlConnection? connection = null;
        try
        {
            if (scopes is not null)
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<NocturneDbContext>();
                // A dedicated, unpooled session releases the lock even if cleanup cannot reach PostgreSQL.
                var settings = new NpgsqlConnectionStringBuilder(db.Database.GetConnectionString()) { Pooling = false };
                connection = new NpgsqlConnection(settings.ConnectionString);
                await connection.OpenAsync(ct);
                var started = System.Diagnostics.Stopwatch.StartNew();
                var bytes = tenantId.ToByteArray();
                var key = BitConverter.ToInt32(bytes, 0) ^ BitConverter.ToInt32(bytes, 4) ^
                          BitConverter.ToInt32(bytes, 8) ^ BitConverter.ToInt32(bytes, 12);
                while (true)
                {
                    await using var command = new NpgsqlCommand("SELECT pg_try_advisory_lock(@class, @key)", connection);
                    command.Parameters.AddWithValue("class", lockClass);
                    command.Parameters.AddWithValue("key", key);
                    if ((bool)(await command.ExecuteScalarAsync(ct))!) break;
                    if (timeout is { } bound && started.Elapsed >= bound)
                    {
                        await connection.DisposeAsync();
                        gate.Release();
                        return null;
                    }
                    await Task.Delay(100, ct);
                }
            }
            return new Lease(connection, gate);
        }
        catch
        {
            if (connection is not null) await connection.DisposeAsync();
            gate.Release();
            throw;
        }
    }

    private sealed class Lease(NpgsqlConnection? connection, SemaphoreSlim gate) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try { if (connection is not null) await connection.DisposeAsync(); }
            finally { gate.Release(); }
        }
    }

    private IServiceScope TenantScope(Guid tenantId)
    {
        var scope = scopes!.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantAccessor>()
            .SetTenant(new(tenantId, "", "", true, false));
        scope.ServiceProvider.GetRequiredService<NocturneDbContext>().TenantId = tenantId;
        return scope;
    }

    internal async Task<SyncProgress?> ProgressAsync(Guid tenantId, CancellationToken ct)
    {
        var progress = await ReadProgressAsync(tenantId, ct);
        if (progress is null || progress.WorkerOwned) return progress;
        await using var operation = await AcquireAsync(tenantId, ct, TimeSpan.Zero);
        // A crashed scheduled run has no session lock; its persisted snapshot must not look active.
        return operation is null ? progress : null;
    }

    private async Task<SyncProgress?> ReadProgressAsync(Guid tenantId, CancellationToken ct)
    {
        if (scopes is null) return memoryProgress.GetValueOrDefault(tenantId);
        using var scope = TenantScope(tenantId);
        var db = scope.ServiceProvider.GetRequiredService<NocturneDbContext>();
        var json = await db.ConnectorConfigurations.AsNoTracking()
            .Where(row => row.TenantId == tenantId && row.ConnectorName == "googlehealth")
            .Select(row => row.SyncCursorsJson).SingleOrDefaultAsync(ct);
        if (json is null) return null;
        using var document = JsonDocument.Parse(json);
        return document.RootElement.TryGetProperty(ProgressKey, out var value)
            ? value.Deserialize<SyncProgress>() : null;
    }

    private async Task WriteProgressAsync(Guid tenantId, SyncProgress? progress, CancellationToken ct)
    {
        if (scopes is null)
        {
            if (progress is null) memoryProgress.TryRemove(tenantId, out _);
            else memoryProgress[tenantId] = progress;
            return;
        }
        using var scope = TenantScope(tenantId);
        var db = scope.ServiceProvider.GetRequiredService<NocturneDbContext>();
        if (progress is null)
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE connector_configurations SET sync_cursors = COALESCE(sync_cursors, jsonb_build_object()) - {ProgressKey}
                WHERE tenant_id = {tenantId} AND connector_name = 'googlehealth'
                """, ct);
        else
        {
            var json = JsonSerializer.Serialize(progress);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE connector_configurations
                SET sync_cursors = jsonb_set(COALESCE(sync_cursors, jsonb_build_object()), ARRAY[{ProgressKey}], {json}::jsonb)
                WHERE tenant_id = {tenantId} AND connector_name = 'googlehealth'
                """, ct);
        }
    }

    internal async Task<bool> QueueAsync(Guid tenantId, int totalDataTypes, CancellationToken ct)
    {
        await using var operation = await AcquireAsync(tenantId, ct, TimeSpan.Zero);
        if (operation is null) return false;
        await using var state = await AcquireKeyAsync(tenantId, StateLock, ct);
        if (await ReadProgressAsync(tenantId, ct) is { WorkerOwned: true }) return false;
        await WriteProgressAsync(tenantId, new(GoogleHealthSyncPhase.Queued, null, 0, totalDataTypes, 0, true), ct);
        if (scopes is null) requests.Writer.TryWrite(tenantId);
        else
        {
            using var scope = TenantScope(tenantId);
            var db = scope.ServiceProvider.GetRequiredService<NocturneDbContext>();
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_notify({RequestChannel}, {tenantId.ToString()})", ct);
        }
        return true;
    }

    internal async IAsyncEnumerable<Guid> ReadRequestsAsync([EnumeratorCancellation] CancellationToken ct)
    {
        if (scopes is null)
        {
            await foreach (var tenant in requests.Reader.ReadAllAsync(ct)) yield return tenant;
            yield break;
        }
        while (!ct.IsCancellationRequested)
        {
            NpgsqlConnection connection;
            try { connection = await OpenRequestListenerAsync(ct); }
            catch (Exception ex) when (!ct.IsCancellationRequested && ex is NpgsqlException or IOException)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
                continue;
            }
            await using (connection)
            {
                connection.Notification += (_, notification) =>
                {
                    if (Guid.TryParse(notification.Payload, out var tenant)) requests.Writer.TryWrite(tenant);
                };
                var recoverAt = DateTimeOffset.MinValue;
                while (!ct.IsCancellationRequested)
                {
                    if (DateTimeOffset.UtcNow >= recoverAt)
                    {
                        // RLS requires tenant-pinned reads. Sweep only at startup/reconnect and
                        // every 15 minutes; normal dispatch is driven by durable queue notifications.
                        using var scope = scopes.CreateScope();
                        var tenants = await scope.ServiceProvider.GetRequiredService<ITenantService>().GetAllAsync(ct);
                        foreach (var tenant in tenants.Where(tenant => tenant.IsActive))
                            if (await ReadProgressAsync(tenant.Id, ct) is { WorkerOwned: true })
                                yield return tenant.Id;
                        recoverAt = DateTimeOffset.UtcNow.AddMinutes(15);
                    }
                    while (requests.Reader.TryRead(out var tenant))
                    {
                        using var scope = TenantScope(tenant);
                        var db = scope.ServiceProvider.GetRequiredService<NocturneDbContext>();
                        if (await db.Tenants.AnyAsync(row => row.Id == tenant && row.IsActive, ct))
                            yield return tenant;
                    }
                    try
                    {
                        await connection.WaitAsync(
                            Math.Max(1, (int)(recoverAt - DateTimeOffset.UtcNow).TotalMilliseconds), ct);
                    }
                    catch (Exception ex) when (!ct.IsCancellationRequested && ex is NpgsqlException or IOException)
                    {
                        break;
                    }
                }
            }
        }
    }

    private async Task<NpgsqlConnection> OpenRequestListenerAsync(CancellationToken ct)
    {
        using var scope = scopes!.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NocturneDbContext>();
        var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(db.Database.GetConnectionString())
        {
            Pooling = false
        }.ConnectionString);
        try
        {
            await connection.OpenAsync(ct);
            await using var command = new NpgsqlCommand($"LISTEN {RequestChannel}", connection);
            await command.ExecuteNonQueryAsync(ct);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    internal async Task<bool> StartQueuedAsync(Guid tenantId, CancellationToken ct)
    {
        await using var state = await AcquireKeyAsync(tenantId, StateLock, ct);
        var progress = await ReadProgressAsync(tenantId, ct);
        if (progress is not { WorkerOwned: true }) return false;
        await WriteProgressAsync(tenantId, progress with { Phase = GoogleHealthSyncPhase.Preparing }, ct);
        return true;
    }

    public async Task ReportAsync(Guid tenantId, GoogleHealthSyncPhase phase, string? dataType = null,
        int? completedDataTypes = null, int? totalDataTypes = null, int? pagesRead = null)
    {
        await using var state = await AcquireKeyAsync(tenantId, StateLock, CancellationToken.None);
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
        await using var state = await AcquireKeyAsync(tenantId, StateLock, CancellationToken.None);
        if (scheduledOnly && await ReadProgressAsync(tenantId, CancellationToken.None) is { WorkerOwned: true }) return;
        await WriteProgressAsync(tenantId, null, CancellationToken.None);
    }
}
