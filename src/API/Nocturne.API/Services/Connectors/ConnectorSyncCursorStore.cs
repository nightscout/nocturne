using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Infrastructure.Data;

namespace Nocturne.API.Services.Connectors;

/// <summary>
///     Persists incremental-sync cursors in the <c>sync_cursors</c> JSON column of the current tenant's
///     <c>connector_configurations</c> row (RLS-scoped via the injected <see cref="NocturneDbContext"/>).
///     Cursors are keyed by resource name within that object, so one row holds every resource's cursor
///     for a connector.
/// </summary>
public class ConnectorSyncCursorStore : IConnectorSyncCursorStore
{
    private readonly NocturneDbContext _context;
    private readonly ILogger<ConnectorSyncCursorStore> _logger;

    public ConnectorSyncCursorStore(NocturneDbContext context, ILogger<ConnectorSyncCursorStore> logger)
    {
        _context = context;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<ConnectorSyncCursor?> GetAsync(
        string connectorName, string resource, CancellationToken cancellationToken = default)
    {
        var canonicalName = ConnectorNames.Canonical(connectorName);
        var json = await _context.ConnectorConfigurations
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
        var canonicalName = ConnectorNames.Canonical(connectorName);
        if (_context.Database.IsNpgsql())
        {
            var json = JsonSerializer.Serialize(cursor);
            await _context.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE connector_configurations
                SET sync_cursors = jsonb_set(CASE WHEN jsonb_typeof(sync_cursors) = 'object'
                    THEN sync_cursors ELSE jsonb_build_object() END, ARRAY[{resource}], {json}::jsonb),
                    sys_updated_at = {DateTime.UtcNow}
                WHERE tenant_id = {_context.TenantId} AND connector_name = {canonicalName}
                """, cancellationToken);
            return;
        }

        var config = await _context.ConnectorConfigurations
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

        await _context.SaveChangesAsync(cancellationToken);
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
