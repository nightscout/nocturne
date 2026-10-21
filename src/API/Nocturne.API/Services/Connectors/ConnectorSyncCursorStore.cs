using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Services.Connectors;

/// <summary>
///     Persists incremental-sync cursors in the <c>sync_cursors</c> JSON column of the current tenant's
///     <c>connector_configurations</c> row (RLS-scoped via the injected <see cref="NocturneDbContext"/>).
///     Cursors are keyed by resource name within that object, so one row holds every resource's cursor
///     for a connector.
/// </summary>
public class ConnectorSyncCursorStore : IConnectorSyncCursorStore
{
    private readonly INocturneDbContextFactory _contexts;
    private readonly ILogger<ConnectorSyncCursorStore> _logger;

    public ConnectorSyncCursorStore(INocturneDbContextFactory contexts, ILogger<ConnectorSyncCursorStore> logger)
    {
        _contexts = contexts ?? throw new ArgumentNullException(nameof(contexts));
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<ConnectorSyncCursor?> GetAsync(
        string connectorName, string resource, CancellationToken cancellationToken = default)
    {
        await using var db = await _contexts.CreateAsync(cancellationToken);
        var canonicalName = ConnectorNames.Canonical(connectorName);
        var json = await db.ConnectorConfigurations
            .Where(c => c.ConnectorName == canonicalName)
            .Select(c => c.SyncCursorsJson)
            .FirstOrDefaultAsync(cancellationToken);

        var cursors = ReadObject(json);
        try { return cursors[resource]?.Deserialize<ConnectorSyncCursor>(); }
        catch (JsonException) { return null; }
    }

    /// <inheritdoc />
    public async Task SetAsync(
        string connectorName, string resource, ConnectorSyncCursor cursor, CancellationToken cancellationToken = default)
    {
        await using var db = await _contexts.CreateAsync(cancellationToken);
        var canonicalName = ConnectorNames.Canonical(connectorName);
        if (db.Database.IsNpgsql())
        {
            var json = JsonSerializer.Serialize(cursor);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE connector_configurations
                SET sync_cursors = jsonb_set(CASE WHEN jsonb_typeof(sync_cursors) = 'object'
                    THEN sync_cursors ELSE jsonb_build_object() END, ARRAY[{resource}], {json}::jsonb),
                    sys_updated_at = {DateTime.UtcNow}
                WHERE tenant_id = {db.TenantId} AND connector_name = {canonicalName}
                """, cancellationToken);
            return;
        }

        var config = await db.ConnectorConfigurations
            .FirstOrDefaultAsync(c => c.ConnectorName == canonicalName, cancellationToken);

        if (config == null)
        {
            _logger.LogWarning(
                "Cannot persist sync cursor for connector {ConnectorName}: configuration not found", connectorName);
            return;
        }

        var cursors = ReadObject(config.SyncCursorsJson);
        cursors[resource] = JsonSerializer.SerializeToNode(cursor);
        config.SyncCursorsJson = cursors.ToJsonString();
        config.SysUpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(cancellationToken);
    }

    private static JsonObject ReadObject(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new JsonObject();

        try
        {
            // Shared keys include progress snapshots, which are not ConnectorSyncCursor values.
            return JsonNode.Parse(json) as JsonObject ?? new JsonObject();
        }
        catch (JsonException)
        {
            // Corrupt/legacy payload: treat as empty so a bad cursor blob can never wedge a sync.
            return new JsonObject();
        }
    }
}
