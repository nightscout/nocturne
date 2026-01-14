using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Nocturne.API.Models;
using Nocturne.Core.Constants;

namespace Nocturne.API.Services;

public class ConnectorHealthService : IConnectorHealthService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ConnectorHealthService> _logger;

    public const string HttpClientName = "ConnectorHealth";

    private record ConnectorDefinition(
        string Id,           // Matches AvailableConnector.Id and health check name
        string ServiceName,
        string ConfigKey);

    private static readonly ConnectorDefinition[] AllConnectors = new[]
    {
        new ConnectorDefinition("nightscout", ServiceNames.NightscoutConnector, "Nightscout"),
        new ConnectorDefinition("dexcom", ServiceNames.DexcomConnector, "Dexcom"),
        new ConnectorDefinition("libre", ServiceNames.LibreConnector, "LibreLinkUp"),
        new ConnectorDefinition("glooko", ServiceNames.GlookoConnector, "Glooko"),
        new ConnectorDefinition("carelink", ServiceNames.MiniMedConnector, "CareLink"),
        new ConnectorDefinition("myfitnesspal", ServiceNames.MyFitnessPalConnector, "MyFitnessPal"),
        new ConnectorDefinition("mylife", ServiceNames.MyLifeConnector, "MyLife")
    };

    public ConnectorHealthService(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<ConnectorHealthService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<IEnumerable<ConnectorStatusDto>> GetConnectorStatusesAsync(CancellationToken cancellationToken = default)
    {
        // Filter to only enabled connectors
        var enabledConnectors = AllConnectors.Where(c => IsConnectorEnabled(c.ConfigKey)).ToList();

        _logger.LogDebug("Checking {Count} enabled connectors out of {Total} total",
            enabledConnectors.Count, AllConnectors.Length);

        if (enabledConnectors.Count == 0)
        {
            return Enumerable.Empty<ConnectorStatusDto>();
        }

        var tasks = enabledConnectors.Select(c => CheckConnectorStatusAsync(c, cancellationToken));
        return await Task.WhenAll(tasks);
    }

    private bool IsConnectorEnabled(string configKey)
    {
        // Check Parameters:Connectors:{ConfigKey}:Enabled
        var enabled = _configuration.GetValue<bool>($"Parameters:Connectors:{configKey}:Enabled");
        return enabled;
    }

    private async Task<ConnectorStatusDto> CheckConnectorStatusAsync(ConnectorDefinition connector, CancellationToken cancellationToken)
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
                    Description = $"HTTP {response.StatusCode}",
                    IsHealthy = false
                };
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(json);

            // Navigate to results -> {checkName}
            if (doc.RootElement.TryGetProperty("results", out var results) &&
                results.TryGetProperty(connector.Id, out var checkResult))
            {
                var status = checkResult.GetProperty("status").GetString() ?? "Unknown";
                var description = checkResult.TryGetProperty("description", out var desc) ? desc.GetString() : null;

                long totalEntries = 0;
                DateTime? lastEntryTime = null;
                int entriesLast24h = 0;

                string state = "Idle";
                string? stateMessage = null;
                Dictionary<string, long>? totalItemsBreakdown = null;
                Dictionary<string, int>? itemsLast24HoursBreakdown = null;

                if (checkResult.TryGetProperty("data", out var data))
                {
                    if (data.TryGetProperty("TotalEntries", out var msgEl) && msgEl.ValueKind == JsonValueKind.Number)
                    {
                         totalEntries = msgEl.GetInt64();
                    }

                    if (data.TryGetProperty("LastEntryTime", out var timeEl))
                    {
                         if(timeEl.ValueKind == JsonValueKind.String && DateTime.TryParse(timeEl.GetString(), out var dt))
                         {
                             lastEntryTime = dt;
                         }
                    }

                    if (data.TryGetProperty("EntriesLast24Hours", out var countEl) && countEl.ValueKind == JsonValueKind.Number)
                    {
                        entriesLast24h = countEl.GetInt32();
                    }

                    if (data.TryGetProperty("State", out var stateEl) && stateEl.ValueKind == JsonValueKind.String)
                    {
                        state = stateEl.GetString() ?? "Idle";
                    }

                    if (data.TryGetProperty("StateMessage", out var stateMsgEl) && stateMsgEl.ValueKind == JsonValueKind.String)
                    {
                        stateMessage = stateMsgEl.GetString();
                    }

                    // Parse per-type breakdowns
                    if (data.TryGetProperty("TotalItemsBreakdown", out var totalBreakdownEl) && totalBreakdownEl.ValueKind == JsonValueKind.Object)
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

                    if (data.TryGetProperty("ItemsLast24HoursBreakdown", out var last24hBreakdownEl) && last24hBreakdownEl.ValueKind == JsonValueKind.Object)
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
                    Description = description,
                    TotalEntries = totalEntries,
                    LastEntryTime = lastEntryTime,
                    EntriesLast24Hours = entriesLast24h,
                    State = state,
                    StateMessage = stateMessage,
                    IsHealthy = status == "Healthy",
                    TotalItemsBreakdown = totalItemsBreakdown,
                    ItemsLast24HoursBreakdown = itemsLast24HoursBreakdown
                };
            }


            // If we have a healthy root status but missing specific check results
            var rootStatus = doc.RootElement.GetProperty("status").GetString();
            return new ConnectorStatusDto
            {
                Id = connector.Id,
                Name = connector.Id,
                Status = rootStatus ?? "Unknown",
                Description = "Detailed metrics unavailable",
                IsHealthy = rootStatus == "Healthy"
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
                Description = ex.Message,
                IsHealthy = false
            };
        }
    }
}
