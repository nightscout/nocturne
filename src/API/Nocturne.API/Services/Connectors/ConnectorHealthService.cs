using Nocturne.API.Models;
using Nocturne.Connectors.Core.Services;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Core.Models.Configuration;

namespace Nocturne.API.Services.Connectors;

/// <summary>
/// Aggregates connector health information by combining ASP.NET Core health check results with
/// per-connector configuration presence and last-reading timestamps. Used by the admin dashboard
/// to display live connector status.
/// </summary>
/// <seealso cref="IConnectorHealthService"/>
public class ConnectorHealthService(
    IConfiguration configuration,
    IDataSourceService dataSourceService,
    IConnectorConfigurationService connectorConfigService,
    ILogger<ConnectorHealthService> logger
) : IConnectorHealthService
{
    private record ConnectorDefinition(
        string Id, // Matches AvailableConnector.Id and health check name
        string ConfigKey,
        string DataSourceId
    ); // Maps to DataSources constant (e.g., "glooko-connector")

    private static IReadOnlyList<ConnectorDefinition> GetConnectorDefinitions()
    {
        return ConnectorMetadataService
            .GetAll()
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

        // Filter out connectors that are not configured and have no data (truly unused)
        return results
            .Where(r =>
                r.State != "Not Configured"
                || r.TotalEntries > 0
            )
            .ToList();
    }

    private async Task<ConnectorStatusDto> GetConnectorStatusWithDbStatsAsync(
        ConnectorDefinition connector,
        CancellationToken cancellationToken
    )
    {
        // Query configuration once for both enabled state and health state
        var dbConfig = await connectorConfigService.GetConfigurationAsync(
            connector.Id,
            cancellationToken
        );

        // Determine enabled state (check environment config first)
        var envEnabled = configuration.GetValue<bool?>(
            $"Parameters:Connectors:{connector.ConfigKey}:Enabled"
        );
        var enabledConfig = envEnabled == false ? false : (dbConfig?.IsActive ?? envEnabled);

        // Extract health state from the same config
        ConnectorHealthStateDto? healthState = null;
        if (dbConfig != null)
        {
            healthState = new ConnectorHealthStateDto
            {
                LastSyncAttempt = dbConfig.LastSyncAttempt,
                LastSuccessfulSync = dbConfig.LastSuccessfulSync,
                LastErrorMessage = dbConfig.LastErrorMessage,
                LastErrorAt = dbConfig.LastErrorAt,
                IsHealthy = dbConfig.IsHealthy
            };
        }

        // Always get database stats for historical data (glucose + treatments + state spans)
        var dbStats = await dataSourceService.GetDataSourceStatsAsync(
            connector.DataSourceId,
            cancellationToken
        );

        logger.LogInformation(
            "Connector {Id}: EnabledConfig={EnabledConfig}, DataSourceId={DataSourceId}, TotalEntries={TotalEntries}, TotalTreatments={TotalTreatments}, TotalStateSpans={TotalStateSpans}",
            connector.Id,
            enabledConfig?.ToString() ?? "not configured",
            connector.DataSourceId,
            dbStats.TotalEntries,
            dbStats.TotalTreatments,
            dbStats.TotalStateSpans
        );

        // Use per-type breakdown dictionaries from database stats
        var totalBreakdown = dbStats.TypeBreakdown;
        var last24HBreakdown = dbStats.TypeBreakdownLast24Hours;

        // If explicitly disabled, return disabled status without checking health
        if (enabledConfig == false)
        {
            // Connector is explicitly disabled - return database-only stats
            // Use TotalItems which combines entries + treatments + state spans
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
                StateMessage = healthState?.LastErrorMessage,
                LastSyncAttempt = healthState?.LastSyncAttempt,
                LastSuccessfulSync = healthState?.LastSuccessfulSync,
                LastErrorAt = healthState?.LastErrorAt,
                TotalItemsBreakdown = totalBreakdown.Count > 0 ? totalBreakdown : null,
                ItemsLast24HoursBreakdown = last24HBreakdown.Count > 0 ? last24HBreakdown : null,
            };
        }

        // Derive state from actual health and sync history, not just enabled flag
        var isEnabled = enabledConfig == true;
        // Consider a connector as having synced if it has a recorded successful sync
        // OR if it has data in the database (legacy connectors that synced before health tracking)
        var hasEverSynced = healthState?.LastSuccessfulSync != null || dbStats.TotalItems > 0;
        var isHealthy = healthState?.IsHealthy ?? false;
        var hasError = healthState != null && !healthState.IsHealthy && healthState.LastErrorAt != null;

        string state;
        bool healthy;

        if (!isEnabled)
        {
            state = "Not Configured";
            healthy = false;
        }
        else if (hasError)
        {
            state = "Error";
            healthy = false;
        }
        else if (!hasEverSynced)
        {
            // Enabled but never successfully synced — waiting for first sync
            state = "Configured";
            healthy = false;
        }
        else
        {
            state = "Running";
            healthy = true;
        }

        var liveStatus = new ConnectorStatusDto
        {
            Id = connector.Id,
            Name = connector.Id,
            Status = state,
            IsHealthy = healthy,
            State = state,
            StateMessage = healthState?.LastErrorMessage,
            LastSyncAttempt = healthState?.LastSyncAttempt,
            LastSuccessfulSync = healthState?.LastSuccessfulSync,
            LastErrorAt = healthState?.LastErrorAt,
            // ALWAYS use database stats for entry counts - sidecar stats may be stale/cached
            // DB is the single source of truth for how much data exists
            TotalEntries = dbStats.TotalItems,
            LastEntryTime = dbStats.LastItemTime,
            EntriesLast24Hours = dbStats.ItemsLast24Hours,
            TotalItemsBreakdown = totalBreakdown.Count > 0 ? totalBreakdown : null,
            ItemsLast24HoursBreakdown = last24HBreakdown.Count > 0 ? last24HBreakdown : null,
        };

        return liveStatus;
    }
}
