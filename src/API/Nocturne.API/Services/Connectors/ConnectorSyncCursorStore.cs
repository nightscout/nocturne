using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nocturne.Connectors.Core.Interfaces;
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
        var connectorNameLower = connectorName.ToLowerInvariant();
        var json = await _context.ConnectorConfigurations
            .Where(c => c.ConnectorName.ToLower() == connectorNameLower)
            .Select(c => c.SyncCursorsJson)
            .FirstOrDefaultAsync(cancellationToken);

        var cursors = Deserialize(json);
        return cursors.TryGetValue(resource, out var cursor) ? cursor : null;
    }

    /// <inheritdoc />
    public async Task SetAsync(
        string connectorName, string resource, ConnectorSyncCursor cursor, CancellationToken cancellationToken = default)
    {
        var connectorNameLower = connectorName.ToLowerInvariant();
        var config = await _context.ConnectorConfigurations
            .FirstOrDefaultAsync(c => c.ConnectorName.ToLower() == connectorNameLower, cancellationToken);

        if (config == null)
        {
            _logger.LogWarning(
                "Cannot persist sync cursor for connector {ConnectorName}: configuration not found", connectorName);
            return;
        }

        var cursors = Deserialize(config.SyncCursorsJson);
        cursors[resource] = cursor;
        config.SyncCursorsJson = JsonSerializer.Serialize(cursors);
        config.SysUpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync(cancellationToken);
    }

    private static Dictionary<string, ConnectorSyncCursor> Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new Dictionary<string, ConnectorSyncCursor>(StringComparer.Ordinal);

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, ConnectorSyncCursor>>(json)
                   ?? new Dictionary<string, ConnectorSyncCursor>(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            // Corrupt/legacy payload: treat as empty so a bad cursor blob can never wedge a sync.
            return new Dictionary<string, ConnectorSyncCursor>(StringComparer.Ordinal);
        }
    }
}
