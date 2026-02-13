using Nocturne.API.Models;
using Nocturne.Connectors.Core.Services;
using Nocturne.Core.Contracts;
using Nocturne.Infrastructure.Data.Abstractions;

namespace Nocturne.API.Services;

public class ConnectorHealthService(
    IConfiguration configuration,
    IPostgreSqlService postgreSqlService,
    IConnectorConfigurationService connectorConfigService,
    ILogger<ConnectorHealthService> logger)
    : IConnectorHealthService
{
    private record ConnectorDefinition(
        string Id, // Matches AvailableConnector.Id and health check name
        string ConfigKey,
        string DataSourceId
    ); // Maps to DataSources constant (e.g., "glooko-connector")

    private static IReadOnlyList<ConnectorDefinition> GetConnectorDefinitions()
    {
        return ConnectorMetadataService.GetAll()
            .Select(connector => new ConnectorDefinition(
                connector.ConnectorName.ToLowerInvariant(),
                connector.ConnectorName,
                connector.DataSourceId
            ))
            .ToList();
    }

    public async Task<IEnumerable<ConnectorStatusDto>> GetConnectorStatusesAsync(
        CancellationToken cancellationToken = default
    )
    {
        var connectors = GetConnectorDefinitions();
        logger.LogDebug("Getting status for all {Count} connectors", connectors.Count);

        var results = new List<ConnectorStatusDto>();
        foreach (var connector in connectors)
        {
            var status = await GetConnectorStatusWithDbStatsAsync(connector, cancellationToken);
            results.Add(status);
        }

        // Filter out connectors that have no data and are not enabled (truly unused)
        return results
            .Where(r =>
                r.IsHealthy
                || // Running and healthy
                r.Status == "Disabled" && r.TotalEntries > 0
                || // Disabled but has historical data
                r.Status != "Disabled" // Any other status (unhealthy, unreachable, etc.)
            )
            .ToList();
    }

    private async Task<ConnectorStatusDto> GetConnectorStatusWithDbStatsAsync(
        ConnectorDefinition connector,
        CancellationToken cancellationToken
    )
    {
        var enabledConfig = await GetConnectorEnabledConfigAsync(connector.Id, connector.ConfigKey, cancellationToken);

        // Always get database stats for historical data (entries + treatments)
        var dbStats = await postgreSqlService.GetEntryStatsBySourceAsync(
            connector.DataSourceId,
            cancellationToken
        );

        logger.LogInformation(
            "Connector {Id}: EnabledConfig={EnabledConfig}, DataSourceId={DataSourceId}, TotalEntries={TotalEntries}, TotalTreatments={TotalTreatments}",
            connector.Id,
            enabledConfig?.ToString() ?? "not configured",
            connector.DataSourceId,
            dbStats.TotalEntries,
            dbStats.TotalTreatments
        );

        // If explicitly disabled, return disabled status without checking health
        if (enabledConfig == false)
        {
            // Connector is explicitly disabled - return database-only stats
            // Use TotalItems which combines entries + treatments
            return new ConnectorStatusDto
            {
                Id = connector.Id,
                Name = connector.Id,
                Status = "Disabled",
                TotalEntries = dbStats.TotalItems,
                LastEntryTime = dbStats.LastItemTime,
                EntriesLast24Hours = dbStats.ItemsLast24Hours,
                State = "Disabled",
                IsHealthy = false,
            };
        }

        var liveStatus = new ConnectorStatusDto
        {
            Id = connector.Id,
            Name = connector.Id,
            Status = enabledConfig == true ? "Running" : "Not Configured",
            IsHealthy = enabledConfig == true,
            State = enabledConfig == true ? "Running" : "Not Configured",
            // ALWAYS use database stats for entry counts - sidecar stats may be stale/cached
            // DB is the single source of truth for how much data exists
            TotalEntries = dbStats.TotalItems,
            LastEntryTime = dbStats.LastItemTime,
            EntriesLast24Hours = dbStats.ItemsLast24Hours
        };

        return liveStatus;
    }

    /// <summary>
    /// Gets the connector enabled configuration.
    /// Checks both environment configuration and database-stored runtime configuration.
    /// Environment config (appsettings) is checked first as the source of truth for whether a connector
    /// should be running at all. Database config can only enable a connector that is available in the environment.
    /// Returns true if explicitly enabled, false if explicitly disabled, null if not configured.
    /// </summary>
    private async Task<bool?> GetConnectorEnabledConfigAsync(string connectorId, string configKey, CancellationToken cancellationToken)
    {
        // First check environment configuration: Parameters:Connectors:{ConfigKey}:Enabled
        // This determines if the connector is even available/running in Aspire
        var envEnabled = configuration.GetValue<bool?>($"Parameters:Connectors:{configKey}:Enabled");

        // If environment config explicitly disables the connector, it's not running in Aspire
        // so we shouldn't try to reach it regardless of DB config
        if (envEnabled == false)
        {
            return false;
        }

        // Now check database-stored runtime configuration
        // This is where the UI stores the enabled state
        var dbConfig = await connectorConfigService.GetConfigurationAsync(connectorId, cancellationToken);
        return dbConfig?.IsActive ?? envEnabled;
    }

}
