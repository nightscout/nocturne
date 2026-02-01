using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Nocturne.API.Models;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts;
using Nocturne.Infrastructure.Data.Abstractions;

namespace Nocturne.API.Services;

public class ConnectorHealthService : IConnectorHealthService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly IPostgreSqlService _postgreSqlService;
    private readonly IConnectorConfigurationService _connectorConfigService;
    private readonly ILogger<ConnectorHealthService> _logger;

    public const string HttpClientName = "ConnectorHealth";

    private record ConnectorDefinition(
        string Id, // Matches AvailableConnector.Id and health check name
        string ServiceName,
        string ConfigKey,
        string DataSourceId
    ); // Maps to DataSources constant (e.g., "glooko-connector")

    private static readonly ConnectorDefinition[] AllConnectors = new[]
    {
        new ConnectorDefinition(
            "nightscout",
            ServiceNames.NightscoutConnector,
            "Nightscout",
            DataSources.NightscoutConnector
        ),
        new ConnectorDefinition(
            "dexcom",
            ServiceNames.DexcomConnector,
            "Dexcom",
            DataSources.DexcomConnector
        ),
        new ConnectorDefinition(
            "libre",
            ServiceNames.LibreConnector,
            "LibreLinkUp",
            DataSources.LibreConnector
        ),
        new ConnectorDefinition(
            "glooko",
            ServiceNames.GlookoConnector,
            "Glooko",
            DataSources.GlookoConnector
        ),
        new ConnectorDefinition(
            "carelink",
            ServiceNames.MiniMedConnector,
            "CareLink",
            DataSources.MiniMedConnector
        ),
        new ConnectorDefinition(
            "myfitnesspal",
            ServiceNames.MyFitnessPalConnector,
            "MyFitnessPal",
            DataSources.MyFitnessPalConnector
        ),
        new ConnectorDefinition(
            "mylife",
            ServiceNames.MyLifeConnector,
            "MyLife",
            DataSources.MyLifeConnector
        ),
    };

    public ConnectorHealthService(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        IPostgreSqlService postgreSqlService,
        IConnectorConfigurationService connectorConfigService,
        ILogger<ConnectorHealthService> logger
    )
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _postgreSqlService = postgreSqlService;
        _connectorConfigService = connectorConfigService;
        _logger = logger;
    }

    public async Task<IEnumerable<ConnectorStatusDto>> GetConnectorStatusesAsync(
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogDebug("Getting status for all {Count} connectors", AllConnectors.Length);

        var results = new List<ConnectorStatusDto>();
        foreach (var connector in AllConnectors)
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
        var dbStats = await _postgreSqlService.GetEntryStatsBySourceAsync(
            connector.DataSourceId,
            cancellationToken
        );

        _logger.LogInformation(
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

        // Connector is enabled or not explicitly configured - try to get live health status
        var liveStatus = await CheckConnectorStatusAsync(connector, cancellationToken);

        // If connector is not configured (enabledConfig == null) and unreachable,
        // mark it as "Not Configured" instead of "Unreachable"
        if (enabledConfig == null && liveStatus.Status == "Unreachable")
        {
            liveStatus.Status = "Not Configured";
            liveStatus.State = "Not Configured";
        }

        // ALWAYS use database stats for entry counts - sidecar stats may be stale/cached
        // DB is the single source of truth for how much data exists
        liveStatus.TotalEntries = dbStats.TotalItems;
        liveStatus.LastEntryTime = dbStats.LastItemTime;
        liveStatus.EntriesLast24Hours = dbStats.ItemsLast24Hours;

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
        var envEnabled = _configuration.GetValue<bool?>($"Parameters:Connectors:{configKey}:Enabled");

        // If environment config explicitly disables the connector, it's not running in Aspire
        // so we shouldn't try to reach it regardless of DB config
        if (envEnabled == false)
        {
            return false;
        }

        // Now check database-stored runtime configuration
        // This is where the UI stores the enabled state
        var dbConfig = await _connectorConfigService.GetConfigurationAsync(connectorId, cancellationToken);
        if (dbConfig != null)
        {
            // If we have a database config, use its IsActive state
            // But only if the connector is available in the environment (envEnabled != false)
            return dbConfig.IsActive;
        }

        // Fall back to environment configuration
        return envEnabled;
    }

    private async Task<ConnectorStatusDto> CheckConnectorStatusAsync(
        ConnectorDefinition connector,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var client = _httpClientFactory.CreateClient(HttpClientName);
            // Aspire service discovery handles the hostname resolution
            var url = $"http://{connector.ServiceName}/health";

            _logger.LogDebug("Checking health for {Connector} at {Url}", connector.Id, url);

            var response = await client.GetAsync(url, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                return new ConnectorStatusDto
                {
                    Id = connector.Id,
                    Name = connector.Id,
                    Status = "Unhealthy",
                    IsHealthy = false,
                };
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(json);

            // Navigate to results -> {checkName}
            if (
                doc.RootElement.TryGetProperty("results", out var results)
                && results.TryGetProperty(connector.Id, out var checkResult)
            )
            {
                var status = checkResult.GetProperty("status").GetString() ?? "Unknown";
                var description = checkResult.TryGetProperty("description", out var desc)
                    ? desc.GetString()
                    : null;

                long totalEntries = 0;
                DateTime? lastEntryTime = null;
                int entriesLast24h = 0;

                string state = "Idle";
                string? stateMessage = null;
                Dictionary<string, long>? totalItemsBreakdown = null;
                Dictionary<string, int>? itemsLast24HoursBreakdown = null;

                if (checkResult.TryGetProperty("data", out var data))
                {
                    if (
                        data.TryGetProperty("TotalEntries", out var msgEl)
                        && msgEl.ValueKind == JsonValueKind.Number
                    )
                    {
                        totalEntries = msgEl.GetInt64();
                    }

                    if (data.TryGetProperty("LastEntryTime", out var timeEl))
                    {
                        if (
                            timeEl.ValueKind == JsonValueKind.String
                            && DateTime.TryParse(timeEl.GetString(), out var dt)
                        )
                        {
                            lastEntryTime = dt;
                        }
                    }

                    if (
                        data.TryGetProperty("EntriesLast24Hours", out var countEl)
                        && countEl.ValueKind == JsonValueKind.Number
                    )
                    {
                        entriesLast24h = countEl.GetInt32();
                    }

                    if (
                        data.TryGetProperty("State", out var stateEl)
                        && stateEl.ValueKind == JsonValueKind.String
                    )
                    {
                        state = stateEl.GetString() ?? "Idle";
                    }

                    if (
                        data.TryGetProperty("StateMessage", out var stateMsgEl)
                        && stateMsgEl.ValueKind == JsonValueKind.String
                    )
                    {
                        stateMessage = stateMsgEl.GetString();
                    }

                    // Parse per-type breakdowns
                    if (
                        data.TryGetProperty("TotalItemsBreakdown", out var totalBreakdownEl)
                        && totalBreakdownEl.ValueKind == JsonValueKind.Object
                    )
                    {
                        totalItemsBreakdown = new Dictionary<string, long>();
                        foreach (var prop in totalBreakdownEl.EnumerateObject())
                        {
                            if (prop.Value.ValueKind == JsonValueKind.Number)
                            {
                                totalItemsBreakdown[prop.Name] = prop.Value.GetInt64();
                            }
                        }
                    }

                    if (
                        data.TryGetProperty("ItemsLast24HoursBreakdown", out var last24hBreakdownEl)
                        && last24hBreakdownEl.ValueKind == JsonValueKind.Object
                    )
                    {
                        itemsLast24HoursBreakdown = new Dictionary<string, int>();
                        foreach (var prop in last24hBreakdownEl.EnumerateObject())
                        {
                            if (prop.Value.ValueKind == JsonValueKind.Number)
                            {
                                itemsLast24HoursBreakdown[prop.Name] = prop.Value.GetInt32();
                            }
                        }
                    }
                }

                return new ConnectorStatusDto
                {
                    Id = connector.Id,
                    Name = connector.Id,
                    Status = status,
                    TotalEntries = totalEntries,
                    LastEntryTime = lastEntryTime,
                    EntriesLast24Hours = entriesLast24h,
                    State = state,
                    StateMessage = stateMessage,
                    IsHealthy = status == "Healthy",
                    TotalItemsBreakdown = totalItemsBreakdown,
                    ItemsLast24HoursBreakdown = itemsLast24HoursBreakdown,
                };
            }

            // If we have a healthy root status but missing specific check results
            var rootStatus = doc.RootElement.GetProperty("status").GetString();
            return new ConnectorStatusDto
            {
                Id = connector.Id,
                Name = connector.Id,
                Status = rootStatus ?? "Unknown",
                IsHealthy = rootStatus == "Healthy",
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to check status for {Connector}", connector.Id);
            return new ConnectorStatusDto
            {
                Id = connector.Id,
                Name = connector.Id,
                Status = "Unreachable",
                IsHealthy = false,
            };
        }
    }
}
