using System.Text.Json.Nodes;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Services;
using Nocturne.Core.Contracts.Connectors;

namespace Nocturne.API.Services.Connectors;

/// <summary>
///     <see cref="IConnectorStateStore"/> over the scope tenant's connector configurations
///     (<see cref="IConnectorConfigurationRepository"/>). A source names its row through its
///     registration (<see cref="ConnectorMetadataService.GetByDataSourceId"/>), since a data source id
///     need not spell the connector's name (<c>tconnectsync-connector</c> is Tandem's).
/// </summary>
public class ConnectorStateStore : IConnectorStateStore
{
    private readonly IConnectorConfigurationRepository _configurations;
    private readonly ILogger<ConnectorStateStore> _logger;

    public ConnectorStateStore(IConnectorConfigurationRepository configurations, ILogger<ConnectorStateStore> logger)
    {
        _configurations = configurations ?? throw new ArgumentNullException(nameof(configurations));
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, DateTime>> ResumeAsync(
        string source, CancellationToken cancellationToken = default) =>
        ConnectorOf(source) is { } connector
            ? await _configurations.GetResumeStateAsync(connector, cancellationToken)
            : new Dictionary<string, DateTime>();

    /// <inheritdoc />
    public async Task SetResumeAsync(
        string source, IReadOnlyDictionary<string, DateTime?> changes, CancellationToken cancellationToken = default)
    {
        if (ConnectorOf(source) is { } connector
            && !await _configurations.SetResumeStateAsync(connector, changes, cancellationToken))
            LogMissing(source);
    }

    /// <inheritdoc />
    public async Task<JsonNode?> VendorAsync(string source, string key, CancellationToken cancellationToken = default) =>
        ConnectorOf(source) is { } connector
            ? await _configurations.GetVendorStateAsync(connector, key, cancellationToken)
            : null;

    /// <inheritdoc />
    public async Task SetVendorAsync(
        string source, string key, JsonNode? value, CancellationToken cancellationToken = default)
    {
        if (ConnectorOf(source) is not { } connector)
            return;

        var written = value is null
            ? await _configurations.RemoveVendorStateAsync(connector, key, cancellationToken)
            : await _configurations.SetVendorStateAsync(connector, key, value, cancellationToken);
        if (!written)
            LogMissing(source);
    }

    /// <inheritdoc />
    public async Task ClearAsync(string source, ConnectorStateSlots slots, CancellationToken cancellationToken = default)
    {
        if (ConnectorOf(source) is { } connector
            && !await _configurations.ClearStateAsync(connector, slots, cancellationToken))
            LogMissing(source);
    }

    private string? ConnectorOf(string source)
    {
        var connector = ConnectorMetadataService.GetByDataSourceId(source)?.ConnectorName;
        if (connector is null)
            _logger.LogWarning("No connector is registered for {Source}; its state is not kept", source);
        return connector;
    }

    private void LogMissing(string source) =>
        _logger.LogWarning("No connector configuration found for {Source}; its state was not written", source);
}
