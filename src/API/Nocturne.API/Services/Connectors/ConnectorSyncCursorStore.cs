using System.Text.Json;
using System.Text.Json.Nodes;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Core.Contracts.Connectors;

namespace Nocturne.API.Services.Connectors;

/// <summary>
///     <see cref="IConnectorSyncCursorStore"/> over the sync cursors of the scope tenant's connector
///     configurations (<see cref="IConnectorConfigurationRepository"/>), keyed by resource name.
/// </summary>
public class ConnectorSyncCursorStore : IConnectorSyncCursorStore
{
    private readonly IConnectorConfigurationRepository _configurations;
    private readonly ILogger<ConnectorSyncCursorStore> _logger;

    public ConnectorSyncCursorStore(
        IConnectorConfigurationRepository configurations, ILogger<ConnectorSyncCursorStore> logger)
    {
        _configurations = configurations ?? throw new ArgumentNullException(nameof(configurations));
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<ConnectorSyncCursor?> GetAsync(
        string connectorName, string resource, CancellationToken cancellationToken = default)
    {
        var json = await GetJsonAsync(connectorName, resource, cancellationToken);
        try { return json?.Deserialize<ConnectorSyncCursor>(); }
        catch (JsonException) { return null; }
    }

    /// <inheritdoc />
    public Task SetAsync(
        string connectorName, string resource, ConnectorSyncCursor cursor, CancellationToken cancellationToken = default) =>
        SetJsonAsync(connectorName, resource, JsonSerializer.SerializeToNode(cursor)!, cancellationToken);

    /// <inheritdoc />
    public Task<JsonNode?> GetJsonAsync(
        string connectorName, string resource, CancellationToken cancellationToken = default) =>
        _configurations.GetSyncCursorAsync(connectorName, resource, cancellationToken);

    /// <inheritdoc />
    public async Task SetJsonAsync(
        string connectorName, string resource, JsonNode value, CancellationToken cancellationToken = default)
    {
        if (!await _configurations.SetSyncCursorAsync(connectorName, resource, value, cancellationToken))
            LogMissing(connectorName);
    }

    /// <inheritdoc />
    public async Task RemoveAsync(
        string connectorName, string resource, CancellationToken cancellationToken = default)
    {
        if (!await _configurations.RemoveSyncCursorAsync(connectorName, resource, cancellationToken))
            LogMissing(connectorName);
    }

    private void LogMissing(string connectorName) =>
        _logger.LogWarning(
            "Cannot persist sync cursor for connector {ConnectorName}: configuration not found", connectorName);
}
