using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Services;
using Nocturne.Core.Contracts;
using Nocturne.Core.Models.Services;

namespace Nocturne.API.Controllers.V4;

/// <summary>
/// Services controller for managing data sources, connectors, and integrations.
/// Provides information about connected data sources, available connectors,
/// and setup instructions for uploaders like xDrip+, Loop, AAPS, etc.
/// </summary>
[ApiController]
[Route("api/v4/services")]
[Produces("application/json")]
public class ServicesController : ControllerBase
{
    private readonly IDataSourceService _dataSourceService;
    private readonly IManualSyncService _manualSyncService;
    private readonly ILogger<ServicesController> _logger;
    private readonly IConfiguration _configuration;

    public ServicesController(
        IDataSourceService dataSourceService,
        IManualSyncService manualSyncService,
        ILogger<ServicesController> logger,
        IConfiguration configuration
    )
    {
        _dataSourceService = dataSourceService;
        _manualSyncService = manualSyncService;
        _logger = logger;
        _configuration = configuration;
    }

    /// <summary>
    /// Get a complete overview of services, data sources, and available integrations.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Complete services overview including active data sources, connectors, and uploader apps</returns>
    [HttpGet]
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
            return StatusCode(500, new { error = "Failed to get services overview" });
        }
    }

    /// <summary>
    /// Get all active data sources that have been sending data to this Nocturne instance.
    /// This includes CGM apps, AID systems, and any other uploaders.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of active data sources with their status</returns>
    [HttpGet("data-sources")]
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
            return StatusCode(500, new { error = "Failed to get active data sources" });
        }
    }

    /// <summary>
    /// Get detailed information about a specific data source.
    /// </summary>
    /// <param name="id">Data source ID or device ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Data source information if found</returns>
    [HttpGet("data-sources/{id}")]
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
                return NotFound(new { error = $"Data source not found: {id}" });
            }
            return Ok(dataSource);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting data source: {Id}", id);
            return StatusCode(500, new { error = "Failed to get data source" });
        }
    }

    /// <summary>
    /// Get available connectors that can be configured to pull data into Nocturne.
    /// </summary>
    /// <returns>List of available connectors</returns>
    [HttpGet("connectors")]
    [ProducesResponseType(typeof(List<AvailableConnector>), 200)]
    public ActionResult<List<AvailableConnector>> GetAvailableConnectors()
    {
        _logger.LogDebug("Getting available connectors");
        var connectors = _dataSourceService.GetAvailableConnectors();
        return Ok(connectors);
    }

    /// <summary>
    /// Get uploader apps that can push data to Nocturne with setup instructions.
    /// </summary>
    /// <returns>List of uploader apps with setup instructions</returns>
    [HttpGet("uploaders")]
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
            return NotFound(new { error = $"Uploader app not found: {appId}" });
        }

        var baseUrl = GetBaseUrl();

        return Ok(
            new UploaderSetupResponse
            {
                App = app,
                BaseUrl = baseUrl,
                ApiSecretPlaceholder = "YOUR-API-SECRET",
                FullApiUrl = $"{baseUrl}/api/v1",
                EntriesUrl = $"{baseUrl}/api/v1/entries",
                TreatmentsUrl = $"{baseUrl}/api/v1/treatments",
                DeviceStatusUrl = $"{baseUrl}/api/v1/devicestatus",
                // Format for xDrip+ style URL with embedded secret
                XdripStyleUrl = $"https://YOUR-API-SECRET@{GetHostFromUrl(baseUrl)}/api/v1",
            }
        );
    }

    /// <summary>
    /// Delete all demo data. This operation is safe as demo data can be easily regenerated.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Result of the delete operation</returns>
    [HttpDelete("data-sources/demo")]
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
            return StatusCode(500, new { error = "Failed to delete demo data" });
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
            return StatusCode(500, new { error = "Failed to delete data source data" });
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
            return StatusCode(500, new { error = "Failed to delete connector data" });
        }
    }

    /// <summary>
    /// Trigger a manual sync of all enabled connectors.
    /// This will sync data for the configured lookback period for all enabled connectors.
    /// Only available if BackfillDays is configured in appsettings.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Result of the manual sync operation</returns>
    [HttpPost("manual-sync")]
    [ProducesResponseType(typeof(ManualSyncResult), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<ManualSyncResult>> TriggerManualSync(
        [FromQuery] int? days = null,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogInformation("Manual sync triggered via API");

        if (!_manualSyncService.IsEnabled())
        {
            _logger.LogWarning("Manual sync is not enabled");
            return BadRequest(new { error = "Manual sync is not enabled. Configure BackfillDays in appsettings." });
        }

        try
        {
            var result = await _manualSyncService.TriggerManualSyncAsync(days, cancellationToken);

            if (!result.Success)
            {
                return StatusCode(500, result);
            }

            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error triggering manual sync");
            return StatusCode(500, new { error = "Failed to trigger manual sync" });
        }
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

    private static string GetHostFromUrl(string url)
    {
        try
        {
            var uri = new Uri(url);
            return uri.Host + (uri.Port != 80 && uri.Port != 443 ? $":{uri.Port}" : "");
        }
        catch
        {
            return url.Replace("https://", "").Replace("http://", "").TrimEnd('/');
        }
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
    /// Placeholder for where the API secret goes
    /// </summary>
    public string ApiSecretPlaceholder { get; set; } = "YOUR-API-SECRET";

    /// <summary>
    /// Full API URL (base + /api/v1)
    /// </summary>
    public string FullApiUrl { get; set; } = string.Empty;

    /// <summary>
    /// Entries endpoint URL
    /// </summary>
    public string EntriesUrl { get; set; } = string.Empty;

    /// <summary>
    /// Treatments endpoint URL
    /// </summary>
    public string TreatmentsUrl { get; set; } = string.Empty;

    /// <summary>
    /// Device status endpoint URL
    /// </summary>
    public string DeviceStatusUrl { get; set; } = string.Empty;

    /// <summary>
    /// xDrip+ style URL with embedded secret placeholder
    /// </summary>
    public string XdripStyleUrl { get; set; } = string.Empty;
}
