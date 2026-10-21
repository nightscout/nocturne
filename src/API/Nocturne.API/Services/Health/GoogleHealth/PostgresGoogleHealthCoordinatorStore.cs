using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Services;
using Npgsql;
using Nocturne.API.Multitenancy;

namespace Nocturne.API.Services.Health.GoogleHealth;

/// <summary>
/// Keeps coordinator state in the connector row's <c>sync_cursors</c>, takes advisory locks on a
/// dedicated session, and queues work with <c>LISTEN</c>/<c>NOTIFY</c>.
/// </summary>
public sealed class PostgresGoogleHealthCoordinatorStore(IServiceScopeFactory scopes) : IGoogleHealthCoordinatorStore
{
    private const string AuditEndpoint = "service:google-health";
    private const string ProgressKey = "googleHealthProgress";
    private const string DisconnectKey = "googleHealthDisconnect";
    private const string RequestChannel = "nocturne_google_health_requests";
    private readonly Channel<Guid> requests = Channel.CreateUnbounded<Guid>();

    public async Task<IAsyncDisposable?> LockAsync(Guid tenantId, int lockClass, TimeSpan? timeout, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        await using var db = await scope.ServiceProvider.GetRequiredService<INocturneDbContextFactory>().CreatePlatformAsync(ct);
        // A dedicated, unpooled session releases the lock even if cleanup cannot reach PostgreSQL.
        var connection = CreateDedicatedConnection(db);
        try
        {
            await connection.OpenAsync(ct);
            var started = System.Diagnostics.Stopwatch.StartNew();
            var key = BinaryPrimitives.ReadInt64BigEndian(SHA256.HashData(
                Encoding.UTF8.GetBytes($"nocturne:google-health:{lockClass}:{tenantId:D}")));
            while (true)
            {
                await using var command = new NpgsqlCommand("SELECT pg_try_advisory_lock(@key)", connection);
                command.Parameters.AddWithValue("key", key);
                if ((bool)(await command.ExecuteScalarAsync(ct))!) return connection;
                if (timeout is { } bound && started.Elapsed >= bound)
                {
                    await connection.DisposeAsync();
                    return null;
                }
                await Task.Delay(100, ct);
            }
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    internal static NpgsqlConnection CreateDedicatedConnection(NocturneDbContext db)
    {
        var settings = new NpgsqlConnectionStringBuilder(db.Database.GetConnectionString()) { Pooling = false };
        return ((NpgsqlConnection)db.Database.GetDbConnection()).CloneWith(settings.ConnectionString);
    }

    public async Task<GoogleHealthCoordinator.SyncProgress?> ReadProgressAsync(Guid tenantId, CancellationToken ct)
    {
        var json = await ReadCursorsAsync(tenantId, ct);
        if (json is null) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            var progress = document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty(ProgressKey, out var value)
                ? value.Deserialize<GoogleHealthCoordinator.SyncProgress>() : null;
            return progress is not null && Enum.IsDefined(progress.Phase) ? progress : null;
        }
        catch (JsonException)
        {
            // A corrupt progress snapshot must not block status or replacement queue requests.
            return null;
        }
    }

    public Task WriteProgressAsync(Guid tenantId, GoogleHealthCoordinator.SyncProgress? progress, CancellationToken ct) =>
        WriteCursorAsync(tenantId, ProgressKey, progress is null ? null : JsonSerializer.Serialize(progress), ct);

    public async Task<GoogleHealthCoordinator.DisconnectRequest?> ReadDisconnectAsync(Guid tenantId, CancellationToken ct)
    {
        var json = await ReadCursorsAsync(tenantId, ct);
        if (json is null) return null;
        using var document = JsonDocument.Parse(json);
        return document.RootElement.ValueKind == JsonValueKind.Object &&
            document.RootElement.TryGetProperty(DisconnectKey, out var value)
            ? value.Deserialize<GoogleHealthCoordinator.DisconnectRequest>() : null;
    }

    public Task WriteDisconnectAsync(Guid tenantId, GoogleHealthCoordinator.DisconnectRequest? request, CancellationToken ct) =>
        WriteCursorAsync(tenantId, DisconnectKey, request is null ? null : JsonSerializer.Serialize(request), ct);

    public async Task NotifyAsync(Guid tenantId, CancellationToken ct)
    {
        using var scope = TenantScope(tenantId);
        await using var db = await scope.ServiceProvider.GetRequiredService<INocturneDbContextFactory>().CreateAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_notify({RequestChannel}, {tenantId}::text)", ct);
    }

    public async IAsyncEnumerable<Guid> ReadRequestsAsync([EnumeratorCancellation] CancellationToken ct)
    {
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
                        var pending = tenants.Where(tenant => tenant.IsActive).ToAsyncEnumerable()
                            .Where(async (tenant, token) =>
                                await ReadProgressAsync(tenant.Id, token) is { WorkerOwned: true } ||
                                await ReadDisconnectAsync(tenant.Id, token) is not null);
                        await foreach (var tenant in pending.WithCancellation(ct))
                            yield return tenant.Id;
                        recoverAt = DateTimeOffset.UtcNow.AddMinutes(15);
                    }
                    while (requests.Reader.TryRead(out var tenant))
                    {
                        using var scope = TenantScope(tenant);
                        await using var db = await scope.ServiceProvider.GetRequiredService<INocturneDbContextFactory>().CreateAsync(ct);
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
        using var scope = scopes.CreateScope();
        await using var db = await scope.ServiceProvider.GetRequiredService<INocturneDbContextFactory>().CreatePlatformAsync(ct);
        var connection = CreateDedicatedConnection(db);
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

    private async Task<string?> ReadCursorsAsync(Guid tenantId, CancellationToken ct)
    {
        using var scope = TenantScope(tenantId);
        await using var db = await scope.ServiceProvider.GetRequiredService<INocturneDbContextFactory>().CreateAsync(ct);
        return await db.ConnectorConfigurations.AsNoTracking()
            .Where(row => row.TenantId == tenantId && row.ConnectorName == "googlehealth")
            .Select(row => row.SyncCursorsJson).SingleOrDefaultAsync(ct);
    }

    private async Task WriteCursorAsync(Guid tenantId, string key, string? json, CancellationToken ct)
    {
        using var scope = TenantScope(tenantId);
        await using var db = await scope.ServiceProvider.GetRequiredService<INocturneDbContextFactory>().CreateAsync(ct);
        if (json is null)
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE connector_configurations SET sync_cursors = (CASE WHEN jsonb_typeof(sync_cursors) = 'object'
                    THEN sync_cursors ELSE jsonb_build_object() END) - {key}
                WHERE tenant_id = {tenantId} AND connector_name = 'googlehealth'
                """, ct);
        else
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE connector_configurations
                SET sync_cursors = jsonb_set(CASE WHEN jsonb_typeof(sync_cursors) = 'object'
                    THEN sync_cursors ELSE jsonb_build_object() END, ARRAY[{key}], {json}::jsonb)
                WHERE tenant_id = {tenantId} AND connector_name = 'googlehealth'
                """, ct);
    }

    private IServiceScope TenantScope(Guid tenantId) =>
        TenantJobScope.Open(scopes, new TenantContext(tenantId, "", "", true, false), AuditEndpoint);
}
