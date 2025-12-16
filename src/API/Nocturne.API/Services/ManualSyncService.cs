using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using Nocturne.API.Configuration;
using Nocturne.Connectors.Core.Services;
using Nocturne.Core.Constants;
using Nocturne.Core.Models.Services;

namespace Nocturne.API.Services;

/// <summary>
/// Service for triggering manual data synchronization via connector sidecar services
/// </summary>
public class ManualSyncService : IManualSyncService
{
    private readonly ManualSyncSettings _settings;
    private readonly ILogger<ManualSyncService> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;

    public ManualSyncService(
        IOptions<ManualSyncSettings> settings,
        ILogger<ManualSyncService> logger,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration
    )
    {
        _settings = settings.Value;
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
    }

    /// <inheritdoc />
    public bool IsEnabled()
    {
        return _settings.IsEnabled;
    }

    /// <inheritdoc />
    public bool HasEnabledConnectors()
    {
        var connectors = ConnectorMetadataService.GetAll();
        foreach (var connector in connectors)
        {
            if (IsConnectorConfiguredInternal(connector.ConnectorName))
            {
                return true;
            }
        }
        return false;
    }

    /// <inheritdoc />
    public bool IsConnectorConfigured(string connectorId)
    {
        if (string.IsNullOrEmpty(connectorId))
        {
            return false;
        }

        return IsConnectorConfiguredInternal(connectorId);
    }

    private bool IsConnectorConfiguredInternal(string connectorName)
    {
        var section = _configuration.GetSection($"Parameters:Connectors:{connectorName}");

        if (!section.Exists())
        {
            return false;
        }

        // Check if explicitly enabled (defaults to true in BaseConnectorConfiguration)
        return section.GetValue("Enabled", true);
    }

    /// <inheritdoc />
    public async Task<ManualSyncResult> TriggerManualSyncAsync(
        CancellationToken cancellationToken = default
    )
    {
        var result = new ManualSyncResult
        {
            StartTime = DateTimeOffset.UtcNow,
            Success = true,
        };

        if (!_settings.IsEnabled)
        {
            _logger.LogWarning("Manual sync is not enabled - BackfillDays is not configured");
            result.Success = false;
            result.ErrorMessage = "Manual sync is not enabled";
            result.EndTime = DateTimeOffset.UtcNow;
            return result;
        }

        _logger.LogInformation(
            "Starting manual sync for all connectors (Sidecar Mode) with {LookbackDays} day lookback",
            _settings.BackfillDays
        );

        var connectorTasks = new List<Task<ConnectorSyncResult>>();
        var connectors = ConnectorMetadataService.GetAll();

        // Queue up all enabled connectors
        foreach (var connector in connectors)
        {
            if (IsConnectorConfiguredInternal(connector.ConnectorName))
            {
                 // Use the ServiceName from metadata (e.g., "dexcom-connector")
                 if (!string.IsNullOrEmpty(connector.ServiceName))
                 {
                     connectorTasks.Add(SyncConnectorAsync(connector.DisplayName, connector.ServiceName, cancellationToken));
                 }
                 else
                 {
                     _logger.LogWarning("Connector {ConnectorName} has no ServiceName configured, skipping manual sync override", connector.ConnectorName);
                 }
            }
        }

        result.TotalConnectors = connectorTasks.Count;

        if (result.TotalConnectors == 0)
        {
            _logger.LogWarning("No connectors are enabled for manual sync");
            result.Success = false;
            result.ErrorMessage = "No connectors are enabled";
            result.EndTime = DateTimeOffset.UtcNow;
            return result;
        }

        // Execute all connectors in parallel
        _logger.LogInformation("Syncing {Count} connector sidecars in parallel", result.TotalConnectors);
        var connectorResults = await Task.WhenAll(connectorTasks);

        result.ConnectorResults = connectorResults.ToList();
        result.SuccessfulConnectors = connectorResults.Count(r => r.Success);
        result.FailedConnectors = connectorResults.Count(r => !r.Success);
        result.Success = result.FailedConnectors == 0;
        result.EndTime = DateTimeOffset.UtcNow;

        _logger.LogInformation(
            "Manual sync completed in {Duration}. Success: {SuccessCount}/{TotalCount}",
            result.Duration,
            result.SuccessfulConnectors,
            result.TotalConnectors
        );

        return result;
    }

    /// <summary>
    /// Syncs a single connector by modifying the sidecar service
    /// </summary>
    private async Task<ConnectorSyncResult> SyncConnectorAsync(
        string displayName,
        string serviceName,
        CancellationToken cancellationToken
    )
    {
        var result = new ConnectorSyncResult { ConnectorName = displayName };
        var startTime = DateTimeOffset.UtcNow;

        try
        {
            // Ensure service name includes http scheme if not present (though our logic assumes host only)
            // But checking previous logic it used $"http://{serviceName}/sync"

            var url = $"http://{serviceName}/sync";
            _logger.LogInformation("Triggering sidecar sync for {ConnectorName} at {Url}", displayName, url);

            var client = _httpClientFactory.CreateClient();

            // We can pass the lookback days/startdate if the sidecar endpoint supports it.
            // Currently sidecars use their own config, but we could potentially pass a query param ?backfillDays=X
            // For now, keep it simple as a trigger.

            var response = await client.PostAsync(url, null, cancellationToken);

            // Note: We don't read content unless it's an error to save performance if body is large (it shouldn't be for sync trigger)
            // But logging error content is useful.

            result.Duration = DateTimeOffset.UtcNow - startTime;

            if (response.IsSuccessStatusCode)
            {
                 _logger.LogInformation(
                    "Sidecar sync triggered successfully for {ConnectorName}",
                    displayName
                );
                result.Success = true;
            }
            else
            {
                var error = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogWarning(
                    "Sidecar sync failed for {ConnectorName}: {StatusCode} - {Error}",
                    displayName,
                    response.StatusCode,
                    error
                );
                result.Success = false;
                result.ErrorMessage = $"Sidecar returned {response.StatusCode}: {error}";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Error triggering sidecar sync for {ConnectorName}: {Message}",
                displayName,
                ex.Message
            );
            result.Success = false;
            result.ErrorMessage = ex.Message;
            result.Duration = DateTimeOffset.UtcNow - startTime;
        }

        return result;
    }
}
