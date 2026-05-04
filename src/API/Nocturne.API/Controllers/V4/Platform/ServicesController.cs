using Microsoft.AspNetCore.Mvc;
using OpenApi.Remote.Attributes;
using Nocturne.API.Models;
using Nocturne.API.Services.Connectors;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Core.Models.Services;

namespace Nocturne.API.Controllers.V4.Platform;

/// <summary>
/// Services controller for managing data sources, connectors, and integrations.
/// Provides information about connected data sources, available connectors,
/// sync status for connectors, and setup instructions for uploaders like xDrip+, Loop, AAPS, etc.
/// </summary>
/// <seealso cref="IDataSourceService"/>
/// <seealso cref="IConnectorHealthService"/>
/// <seealso cref="IConnectorSyncService"/>
[ApiController]
[Route("api/v4/services")]
[Produces("application/json")]
public class ServicesController : ControllerBase
{
    private readonly IDataSourceService _dataSourceService;
    private readonly IConnectorHealthService _connectorHealthService;
    private readonly IConnectorSyncService _connectorSyncService;
    private readonly ILogger<ServicesController> _logger;
    private readonly IConfiguration _configuration;

    /// <summary>
    /// Initializes a new instance of <see cref="ServicesController"/>.
    /// </summary>
    /// <param name="dataSourceService">Service for querying active data sources and their status.</param>
    /// <param name="connectorHealthService">Service for connector health state queries.</param>
    /// <param name="connectorSyncService">Service for triggering on-demand connector syncs.</param>
    /// <param name="logger">Logger instance.</param>
    /// <param name="configuration">Application configuration for base URL resolution.</param>
    public ServicesController(
        IDataSourceService dataSourceService,
        IConnectorHealthService connectorHealthService,
        IConnectorSyncService connectorSyncService,
        ILogger<ServicesController> logger,
        IConfiguration configuration
    )
    {
        _dataSourceService = dataSourceService;
        _connectorHealthService = connectorHealthService;
        _connectorSyncService = connectorSyncService;
        _logger = logger;
        _configuration = configuration;
    }

    /// <summary>
    /// Get a complete overview of services, data sources, and available integrations.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Complete services overview including active data sources, connectors, and uploader apps</returns>
    [HttpGet]
    [RemoteQuery]
    [ProducesResponseType(typeof(ServicesOverview), 200)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<ServicesOverview>> GetServicesOverview(
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogDebug("Getting services overview");

        try
        {
            // Get base URL for API endpoint info
            var baseUrl = GetBaseUrl();
            var isAuthenticated = User.Identity?.IsAuthenticated ?? false;

            var overview = await _dataSourceService.GetServicesOverviewAsync(
                baseUrl,
                isAuthenticated,
                cancellationToken
            );
            return Ok(overview);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting services overview");
            return Problem(detail: "Failed to get services overview", statusCode: 500, title: "Internal Server Error");
        }
    }

    /// <summary>
    /// Get all active data sources that have been sending data to this Nocturne instance.
    /// This includes CGM apps, AID systems, and any other uploaders.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of active data sources with their status</returns>
    [HttpGet("data-sources")]
    [RemoteQuery]
    [ProducesResponseType(typeof(List<DataSourceInfo>), 200)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<List<DataSourceInfo>>> GetActiveDataSources(
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogDebug("Getting active data sources");

        try
        {
            var dataSources = await _dataSourceService.GetActiveDataSourcesAsync(cancellationToken);
            return Ok(dataSources);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting active data sources");
            return Problem(detail: "Failed to get active data sources", statusCode: 500, title: "Internal Server Error");
        }
    }

    /// <summary>
    /// Get detailed information about a specific data source.
    /// </summary>
    /// <param name="id">Data source ID or device ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Data source information if found</returns>
    [HttpGet("data-sources/{id}")]
    [RemoteQuery]
    [ProducesResponseType(typeof(DataSourceInfo), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<DataSourceInfo>> GetDataSource(
        string id,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogDebug("Getting data source: {Id}", id);

        try
        {
            var dataSource = await _dataSourceService.GetDataSourceInfoAsync(id, cancellationToken);
            if (dataSource == null)
            {
                return Problem(detail: $"Data source not found: {id}", statusCode: 404, title: "Not Found");
            }
            return Ok(dataSource);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting data source: {Id}", id);
            return Problem(detail: "Failed to get data source", statusCode: 500, title: "Internal Server Error");
        }
    }

    /// <summary>
    /// Get available connectors that can be configured to pull data into Nocturne.
    /// </summary>
    /// <returns>List of available connectors</returns>
    [HttpGet("connectors")]
    [RemoteQuery]
    [ProducesResponseType(typeof(List<AvailableConnector>), 200)]
    public async Task<ActionResult<List<AvailableConnector>>> GetAvailableConnectors(
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Getting available connectors");
        var connectors = await _dataSourceService.GetAvailableConnectorsAsync(cancellationToken);
        return Ok(connectors);
    }

    /// <summary>
    /// Get capabilities for a specific connector.
    /// </summary>
    /// <param name="id">The connector ID (e.g., "dexcom", "libre")</param>
    /// <returns>Connector capabilities</returns>
    [HttpGet("connectors/{id}/capabilities")]
    [RemoteQuery]
    [ProducesResponseType(typeof(ConnectorCapabilities), 200)]
    [ProducesResponseType(404)]
    public ActionResult<ConnectorCapabilities> GetConnectorCapabilities(string id)
    {
        _logger.LogDebug("Getting connector capabilities for: {Id}", id);

        var capabilities = _dataSourceService.GetConnectorCapabilities(id);
        if (capabilities == null)
        {
            return Problem(detail: $"Connector not found: {id}", statusCode: 404, title: "Not Found");
        }

        return Ok(capabilities);
    }

    /// <summary>
    /// Get uploader apps that can push data to Nocturne with setup instructions.
    /// </summary>
    /// <returns>List of uploader apps with setup instructions</returns>
    [HttpGet("uploaders")]
    [RemoteQuery]
    [ProducesResponseType(typeof(List<UploaderApp>), 200)]
    public ActionResult<List<UploaderApp>> GetUploaderApps()
    {
        _logger.LogDebug("Getting uploader apps");
        var uploaders = _dataSourceService.GetUploaderApps();
        return Ok(uploaders);
    }

    /// <summary>
    /// Get API endpoint information for configuring external apps.
    /// This provides all the information needed to configure xDrip+, Loop, AAPS, etc.
    /// </summary>
    /// <returns>API endpoint information</returns>
    [HttpGet("api-info")]
    [RemoteQuery]
    [ProducesResponseType(typeof(ApiEndpointInfo), 200)]
    public ActionResult<ApiEndpointInfo> GetApiInfo()
    {
        _logger.LogDebug("Getting API endpoint info");

        var baseUrl = GetBaseUrl();
        var isAuthenticated = User.Identity?.IsAuthenticated ?? false;

        var info = new ApiEndpointInfo
        {
            BaseUrl = baseUrl,
            RequiresApiSecret = true,
            IsAuthenticated = isAuthenticated,
            EntriesEndpoint = "/api/v1/entries",
            TreatmentsEndpoint = "/api/v1/treatments",
            DeviceStatusEndpoint = "/api/v1/devicestatus",
        };

        return Ok(info);
    }

    /// <summary>
    /// Get setup instructions for a specific uploader app.
    /// </summary>
    /// <param name="appId">The uploader app ID (e.g., "xdrip", "loop", "aaps")</param>
    /// <returns>Setup instructions for the specified app</returns>
    [HttpGet("uploaders/{appId}/setup")]
    [RemoteQuery]
    [ProducesResponseType(typeof(UploaderSetupResponse), 200)]
    [ProducesResponseType(404)]
    public ActionResult<UploaderSetupResponse> GetUploaderSetup(string appId)
    {
        _logger.LogDebug("Getting setup instructions for: {AppId}", appId);

        var uploaders = _dataSourceService.GetUploaderApps();
        var app = uploaders.FirstOrDefault(u =>
            u.Id.Equals(appId, StringComparison.OrdinalIgnoreCase)
        );

        if (app == null)
        {
            return Problem(detail: $"Uploader app not found: {appId}", statusCode: 404, title: "Not Found");
        }

        var baseUrl = GetBaseUrl();

        var response = new UploaderSetupResponse
        {
            App = app,
            BaseUrl = baseUrl,
        };

        // Apps that support OAuth device authorization get a deep-link URL for QR code scanning
        if (appId.Equals("xdrip", StringComparison.OrdinalIgnoreCase))
        {
            response.ConnectUrl = $"xdrip://connect/nocturne?url={Uri.EscapeDataString(baseUrl)}";
        }

        return Ok(response);
    }

    /// <summary>
    /// Delete all demo data. This operation is safe as demo data can be easily regenerated.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Result of the delete operation</returns>
    [HttpDelete("data-sources/demo")]
    [RemoteCommand(Invalidates = ["GetServicesOverview", "GetActiveDataSources", "GetStatus"])]
    [ProducesResponseType(typeof(DataSourceDeleteResult), 200)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<DataSourceDeleteResult>> DeleteDemoData(
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogInformation("Deleting demo data via API");

        try
        {
            var result = await _dataSourceService.DeleteDemoDataAsync(cancellationToken);
            if (!result.Success)
            {
                return StatusCode(500, result);
            }
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting demo data");
            return Problem(detail: "Failed to delete demo data", statusCode: 500, title: "Internal Server Error");
        }
    }

    /// <summary>
    /// Delete all data from a specific data source.
    /// WARNING: This is a destructive operation that cannot be undone.
    /// </summary>
    /// <param name="id">Data source ID or device ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Result of the delete operation</returns>
    [HttpDelete("data-sources/{id}")]
    [RemoteCommand(Invalidates = ["GetServicesOverview", "GetActiveDataSources", "GetStatus"])]
    [ProducesResponseType(typeof(DataSourceDeleteResult), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<DataSourceDeleteResult>> DeleteDataSourceData(
        string id,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogWarning("Deleting all data for data source: {Id}", id);

        try
        {
            var result = await _dataSourceService.DeleteDataSourceDataAsync(id, cancellationToken);
            if (!result.Success)
            {
                if (result.Error?.Contains("not found") == true)
                {
                    return NotFound(result);
                }
                return StatusCode(500, result);
            }
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting data for data source: {Id}", id);
            return Problem(detail: "Failed to delete data source data", statusCode: 500, title: "Internal Server Error");
        }
    }

    /// <summary>
    /// Get a summary of data counts for a specific connector.
    /// Returns the number of entries, treatments, and device statuses synced by this connector.
    /// </summary>
    /// <param name="id">Connector ID (e.g., "dexcom")</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Data summary with counts by type</returns>
    [HttpGet("connectors/{id}/data-summary")]
    [RemoteQuery]
    [ProducesResponseType(typeof(ConnectorDataSummary), 200)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<ConnectorDataSummary>> GetConnectorDataSummary(
        string id,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogDebug("Getting data summary for connector: {Id}", id);

        try
        {
            var summary = await _dataSourceService.GetConnectorDataSummaryAsync(
                id,
                cancellationToken
            );
            return Ok(summary);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting data summary for connector: {Id}", id);
            return Problem(detail: "Failed to get connector data summary", statusCode: 500, title: "Internal Server Error");
        }
    }

    /// <summary>
    /// Delete all data from a specific connector.
    /// WARNING: This is a destructive operation that cannot be undone.
    /// </summary>
    /// <param name="id">Connector ID (e.g., "dexcom")</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Result of the delete operation</returns>
    [HttpDelete("connectors/{id}/data")]
    [RemoteCommand(Invalidates = ["GetServicesOverview", "GetActiveDataSources", "GetStatus", "GetConnectorDataSummary"])]
    [ProducesResponseType(typeof(DataSourceDeleteResult), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<DataSourceDeleteResult>> DeleteConnectorData(
        string id,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogWarning("Deleting all data for connector: {Id}", id);

        try
        {
            var result = await _dataSourceService.DeleteConnectorDataAsync(id, cancellationToken);

            if (!result.Success)
            {
                if (result.Error?.Contains("not found") == true)
                {
                    return NotFound(result);
                }
                return StatusCode(500, result);
            }

            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting data for connector: {Id}", id);
            return Problem(detail: "Failed to delete connector data", statusCode: 500, title: "Internal Server Error");
        }
    }

    /// <summary>
    /// Trigger a manual sync for a specific connector.
    /// </summary>
    /// <param name="id">Connector ID (e.g., "dexcom", "tidepool")</param>
    /// <param name="request">Sync request parameters (date range and data types)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Sync result with success status and details</returns>
    [HttpPost("connectors/{id}/sync")]
    [RemoteCommand]
    [ProducesResponseType(typeof(Nocturne.Connectors.Core.Models.SyncResult), 200)]
    [ProducesResponseType(400)]
    public async Task<
        ActionResult<Nocturne.Connectors.Core.Models.SyncResult>
    > TriggerConnectorSync(
        string id,
        [FromBody] Nocturne.Connectors.Core.Models.SyncRequest request,
        CancellationToken cancellationToken
    )
    {
        if (string.IsNullOrWhiteSpace(id))
            return Problem(detail: "Connector ID is required", statusCode: 400, title: "Bad Request");

        var result = await _connectorSyncService.TriggerSyncAsync(id, request, cancellationToken);
        return Ok(result);
    }

    /// <summary>
    /// Get sync status for a specific connector, including latest timestamps and connector state.
    /// Used by connectors on startup to determine where to resume syncing from.
    /// </summary>
    /// <param name="id">The connector ID (e.g., "dexcom", "libre", "glooko")</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Complete sync status including timestamps for entries, treatments, and connector state</returns>
    [HttpGet("connectors/{id}/sync-status")]
    [RemoteQuery]
    [ProducesResponseType(typeof(ConnectorSyncStatus), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<ConnectorSyncStatus>> GetConnectorSyncStatus(
        string id,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogDebug("Getting sync status for connector: {Id}", id);

        if (string.IsNullOrWhiteSpace(id))
        {
            return Problem(detail: "Connector ID is required", statusCode: 400, title: "Bad Request");
        }

        try
        {
            // Map connector ID to data source name used in database
            var dataSource = MapConnectorIdToDataSource(id);

            // Get latest timestamps from V4 glucose tables
            var entryTimestamp = await _dataSourceService.GetLatestGlucoseTimestampBySourceAsync(
                dataSource,
                cancellationToken
            );

            var oldestEntryTimestamp =
                await _dataSourceService.GetOldestGlucoseTimestampBySourceAsync(
                    dataSource,
                    cancellationToken
                );

            var treatmentTimestamp = await _dataSourceService.GetLatestTreatmentTimestampBySourceAsync(
                dataSource, cancellationToken);
            var oldestTreatmentTimestamp = await _dataSourceService.GetOldestTreatmentTimestampBySourceAsync(
                dataSource, cancellationToken);

            // Get connector health/state
            var connectorStatuses = await _connectorHealthService.GetConnectorStatusesAsync(
                cancellationToken
            );
            var connectorStatus = connectorStatuses.FirstOrDefault(c =>
                c.Id.Equals(id, StringComparison.OrdinalIgnoreCase)
            );

            return Ok(
                new ConnectorSyncStatus
                {
                    ConnectorId = id,
                    DataSource = dataSource,
                    LatestEntryTimestamp = entryTimestamp,
                    OldestEntryTimestamp = oldestEntryTimestamp,
                    LatestTreatmentTimestamp = treatmentTimestamp,
                    OldestTreatmentTimestamp = oldestTreatmentTimestamp,
                    HasEntries = entryTimestamp.HasValue,
                    HasTreatments = treatmentTimestamp.HasValue,
                    State = connectorStatus?.State ?? "Unknown",
                    StateMessage = connectorStatus?.StateMessage,
                    IsHealthy = connectorStatus?.IsHealthy ?? false,
                    QueriedAt = DateTime.UtcNow,
                }
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting sync status for connector: {Id}", id);
            return Problem(detail: "Failed to get sync status", statusCode: 500, title: "Internal Server Error");
        }
    }

    /// <summary>
    /// Maps a connector ID (e.g., "dexcom") to the data source name used in the database (e.g., "dexcom-connector")
    /// </summary>
    private static string MapConnectorIdToDataSource(string connectorId)
    {
        // Most connectors use "{id}-connector" format
        return connectorId.ToLowerInvariant() switch
        {
            "dexcom" => "dexcom-connector",
            "libre" => "libre-connector",
            "glooko" => "glooko-connector",
            "nightscout" => "nightscout-connector",
            "carelink" => "carelink-connector",
            "myfitnesspal" => "myfitnesspal-connector",
            "tidepool" => "tidepool-connector",
            _ => $"{connectorId.ToLowerInvariant()}-connector",
        };
    }

    private string GetBaseUrl()
    {
        // Try to get configured base URL first
        var configuredUrl = _configuration["BaseUrl"];
        if (!string.IsNullOrEmpty(configuredUrl))
        {
            return configuredUrl.TrimEnd('/');
        }

        // Fall back to request URL
        var request = HttpContext.Request;
        return $"{request.Scheme}://{request.Host}";
    }

}

/// <summary>
/// Response model for uploader setup instructions
/// </summary>
public class UploaderSetupResponse
{
    /// <summary>
    /// The uploader app details
    /// </summary>
    public UploaderApp App { get; set; } = new();

    /// <summary>
    /// Base URL for this Nocturne instance
    /// </summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// Deep-link URL for apps that support OAuth device authorization via QR code
    /// (e.g. xdrip://connect/nocturne?url=https://your-instance.com).
    /// When present, the frontend shows a QR code and inline device-code input.
    /// </summary>
    public string? ConnectUrl { get; set; }
}

/// <summary>
/// Response model for connector sync status
/// </summary>
public class ConnectorSyncStatus
{
    /// <summary>
    /// The connector ID (e.g., "dexcom", "libre")
    /// </summary>
    public string ConnectorId { get; set; } = string.Empty;

    /// <summary>
    /// The data source name used in the database (e.g., "dexcom-connector")
    /// </summary>
    public string DataSource { get; set; } = string.Empty;

    /// <summary>
    /// The timestamp of the latest entry, or null if no entries exist
    /// </summary>
    public DateTime? LatestEntryTimestamp { get; set; }

    /// <summary>
    /// The timestamp of the oldest entry, or null if no entries exist
    /// </summary>
    public DateTime? OldestEntryTimestamp { get; set; }

    /// <summary>
    /// The timestamp of the latest treatment, or null if no treatments exist
    /// </summary>
    public DateTime? LatestTreatmentTimestamp { get; set; }

    /// <summary>
    /// The timestamp of the oldest treatment, or null if no treatments exist
    /// </summary>
    public DateTime? OldestTreatmentTimestamp { get; set; }

    /// <summary>
    /// Whether any entries exist for this connector
    /// </summary>
    public bool HasEntries { get; set; }

    /// <summary>
    /// Whether any treatments exist for this connector
    /// </summary>
    public bool HasTreatments { get; set; }

    /// <summary>
    /// Current connector state (Idle, Syncing, BackingOff, Error)
    /// </summary>
    public string State { get; set; } = "Unknown";

    /// <summary>
    /// Optional message describing the current state
    /// </summary>
    public string? StateMessage { get; set; }

    /// <summary>
    /// Whether the connector is healthy
    /// </summary>
    public bool IsHealthy { get; set; }

    /// <summary>
    /// When this status was queried
    /// </summary>
    public DateTime QueriedAt { get; set; }
}
