using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using OpenApi.Remote.Attributes;
using Nocturne.API.Attributes;
using Nocturne.API.RateLimiting;
using Nocturne.API.Authorization;
using Nocturne.API.Extensions;
using Nocturne.API.Models;
using Nocturne.API.Multitenancy;
using Nocturne.API.Services.Connectors;
using Nocturne.Connectors.Core.Services;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Services;

namespace Nocturne.API.Controllers.V4.Platform;

/// <summary>
/// Data sources, connectors, and uploaders for the tenant. Read their status and capabilities, and get
/// setup instructions for uploaders such as xDrip+, Loop and AAPS. Start syncs, reset sync cursors,
/// and delete data.
/// </summary>
/// <seealso cref="IDataSourceService"/>
/// <seealso cref="IConnectorHealthService"/>
/// <seealso cref="IConnectorSyncService"/>
[ApiController]
[System.ComponentModel.DisplayName("Data Sources and Sync")]
[Tags("Connectors")]
[Route("api/v4/services")]
[Produces("application/json")]
[Authorize]
public class ServicesController : ControllerBase
{
    private const string SyncRunningCode = "sync_running";
    private const string ImportRunningCode = "import_running";

    private readonly IDataSourceService _dataSourceService;
    private readonly IConnectionService _connectionService;
    private readonly IConnectorHealthService _connectorHealthService;
    private readonly IConnectorSyncService _connectorSyncService;
    private readonly ILogger<ServicesController> _logger;
    private readonly ITenantAccessor _tenantAccessor;
    private readonly BaseDomainOptions _baseDomain;

    /// <summary>
    /// Initializes a new instance of <see cref="ServicesController"/>.
    /// </summary>
    /// <param name="dataSourceService">Service for querying active data sources and their status.</param>
    /// <param name="connectionService">Service that lists the tenant's connections.</param>
    /// <param name="connectorHealthService">Service for connector health state queries.</param>
    /// <param name="connectorSyncService">Service for triggering on-demand connector syncs.</param>
    /// <param name="logger">Logger instance.</param>
    /// <param name="tenantAccessor">Resolved tenant context, used to build the tenant's subdomain base URL.</param>
    /// <param name="baseDomain">Platform base-domain options used to construct the tenant subdomain.</param>
    public ServicesController(
        IDataSourceService dataSourceService,
        IConnectionService connectionService,
        IConnectorHealthService connectorHealthService,
        IConnectorSyncService connectorSyncService,
        ILogger<ServicesController> logger,
        ITenantAccessor tenantAccessor,
        IOptions<BaseDomainOptions> baseDomain
    )
    {
        _dataSourceService = dataSourceService;
        _connectionService = connectionService;
        _connectorHealthService = connectorHealthService;
        _connectorSyncService = connectorSyncService;
        _logger = logger;
        _tenantAccessor = tenantAccessor;
        _baseDomain = baseDomain.Value;
    }

    /// <summary>
    /// Get a complete overview of services, data sources, and available integrations.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Complete services overview including active data sources, connectors, and uploader apps</returns>
    [HttpGet]
    [RequireScope(Scope.DevicesRead)]
    [RemoteQuery]
    [ProducesResponseType(typeof(ServicesOverview), 200)]
    [ProducesResponseType(500)]
    [ErrorEnvelope]
    public async Task<ActionResult<ServicesOverview>> GetServicesOverview(
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogDebug("Getting services overview");

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

    /// <summary>
    /// List the tenant's connections: every authorized app, API token and configured connector, with
    /// the records each wrote in the last 30 days, and the records none of them wrote.
    /// </summary>
    /// <inheritdoc cref="IConnectionService.GetConnectionsAsync"/>
    /// <remarks>
    /// It lists the caller's credentials, which is the account holder's to read, so a delegated
    /// credential is refused (<see cref="RequireInteractiveSessionAttribute"/>).
    /// </remarks>
    [HttpGet("connections")]
    [RequireInteractiveSession]
    [RequireScope(Scope.DevicesRead)]
    [RemoteQuery]
    [ProducesResponseType(typeof(ConnectionsOverview), 200)]
    public async Task<ActionResult<ConnectionsOverview>> GetConnections(CancellationToken cancellationToken = default) =>
        Ok(await _connectionService.GetConnectionsAsync(
            HttpContext.GetSubjectId(), HttpContext.MayKeepDeviceCredentials(), cancellationToken));

    /// <summary>
    /// Get all active data sources that have been sending data to this Nocturne instance.
    /// This includes CGM apps, AID systems, and any other uploaders.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of active data sources with their status</returns>
    [HttpGet("data-sources")]
    [RequireScope(Scope.DevicesRead)]
    [RemoteQuery]
    [ProducesResponseType(typeof(List<DataSourceInfo>), 200)]
    [ProducesResponseType(500)]
    [ErrorEnvelope]
    public async Task<ActionResult<List<DataSourceInfo>>> GetActiveDataSources(
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogDebug("Getting active data sources");

        var dataSources = await _dataSourceService.GetActiveDataSourcesAsync(cancellationToken);
        return Ok(dataSources);
    }

    /// <summary>
    /// Get detailed information about a specific data source.
    /// </summary>
    /// <param name="id">Data source ID or device ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Data source information if found</returns>
    [HttpGet("data-sources/{id}")]
    [RequireScope(Scope.DevicesRead)]
    [RemoteQuery]
    [ProducesResponseType(typeof(DataSourceInfo), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    [ErrorEnvelope]
    public async Task<ActionResult<DataSourceInfo>> GetDataSource(
        string id,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogDebug("Getting data source: {Id}", id);

        var dataSource = await _dataSourceService.GetDataSourceInfoAsync(id, cancellationToken);
        if (dataSource == null)
        {
            return Problem(detail: $"Data source not found: {id}", statusCode: 404, title: "Not Found");
        }
        return Ok(dataSource);
    }

    /// <summary>
    /// Get available connectors that can be configured to pull data into Nocturne.
    /// </summary>
    /// <returns>List of available connectors</returns>
    [HttpGet("connectors")]
    [RequireScope(Scope.ConnectorsRead)]
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
    /// Get capabilities for a specific connector, and when an API secret the caller saves on it stops.
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

        if (!ConnectorConfigurationService.AdoptsSecret(id))
            return Ok(capabilities);

        var ceiling = HttpContext.CallerGrantCeiling() ?? GrantCeiling.Nothing;
        if (ConnectorSecretSaver.MayAdoptFor(ceiling.Scopes, ceiling.HistoryClamped))
            capabilities.SavedSecretStopsAt = HttpContext.CallerAccessEndsAt();
        else
            capabilities.SavedSecretMakesNoCredential = true;

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
    /// Whether the tenant holds demo data, which <see cref="DeleteDemoData"/> would delete.
    /// </summary>
    [HttpGet("data-sources/demo")]
    [RequireScope(Scope.TenantSettings)]
    [RemoteQuery]
    [ProducesResponseType(typeof(DemoDataPresence), 200)]
    [ErrorEnvelope]
    public async Task<ActionResult<DemoDataPresence>> GetDemoDataPresence(
        CancellationToken cancellationToken = default
    ) => Ok(new DemoDataPresence(await _dataSourceService.HoldsDemoDataAsync(cancellationToken)));

    /// <summary>
    /// Delete all demo data. This operation is safe as demo data can be easily regenerated.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Result of the delete operation</returns>
    [HttpDelete("data-sources/demo")]
    [RequireScope(Scope.TenantSettings)]
    [DenyDemoSubject]
    [RemoteCommand(Invalidates = ["GetServicesOverview", "GetConnections", "GetActiveDataSources", "ConnectorStatus_GetStatus", "GetDemoDataPresence"])]
    [ProducesResponseType(typeof(DataSourceDeleteResult), 200)]
    [ProducesResponseType(500)]
    [ErrorEnvelope]
    public async Task<ActionResult<DataSourceDeleteResult>> DeleteDemoData(
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogInformation("Deleting demo data via API");

        var result = await _dataSourceService.DeleteDemoDataAsync(cancellationToken);
        if (!result.Success)
        {
            return StatusCode(500, result);
        }
        return Ok(result);
    }

    /// <summary>
    /// Delete all data from a specific data source.
    /// WARNING: This is a destructive operation that cannot be undone.
    /// </summary>
    /// <param name="id">Data source ID or device ID</param>
    /// <param name="allowReimport">Let the source send the deleted records again.</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Result of the delete operation</returns>
    [HttpDelete("data-sources/{id}")]
    [RequireScope(Scope.TenantSettings)]
    [DenyDemoSubject]
    [RemoteCommand(Invalidates = ["GetServicesOverview", "GetConnections", "GetActiveDataSources", "ConnectorStatus_GetStatus"])]
    [ProducesResponseType(typeof(DataSourceDeleteResult), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    [ErrorEnvelope]
    public async Task<ActionResult<DataSourceDeleteResult>> DeleteDataSourceData(
        string id,
        [FromQuery] bool allowReimport = false,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogWarning("Deleting all data for data source: {Id}", id);

        var result = await _dataSourceService.DeleteDataSourceDataAsync(id, allowReimport, cancellationToken);
        if (!result.Success)
        {
            if (result.ErrorCode == DataSourceDeleteError.NotFound)
            {
                return Problem(detail: $"Data source not found: {id}", statusCode: 404, title: "Not Found");
            }
            return StatusCode(500, result);
        }
        return Ok(result);
    }

    /// <summary>
    /// Delete every record a connection's credentials wrote, whatever device or data source it names.
    /// WARNING: This is a destructive operation that cannot be undone.
    /// </summary>
    /// <remarks>
    /// The delete covers every credential of the named connection, revoked ones and those whose records
    /// are older than the connections list shows, and each has to be an app or API token of the caller.
    /// A connector's records have no credential and keep the delete by data source.
    /// </remarks>
    /// <param name="request">Credentials of one connection, and whether they may send the records again.</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Result of the delete operation</returns>
    [HttpPost("connections/delete-records")]
    [RequireScope(Scope.TenantSettings)]
    [DenyDemoSubject]
    [RemoteCommand(Invalidates = ["GetServicesOverview", "GetConnections", "GetActiveDataSources", "ConnectorStatus_GetStatus"])]
    [ProducesResponseType(typeof(DataSourceDeleteResult), 200)]
    [ProducesResponseType(401)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<DataSourceDeleteResult>> DeleteConnectionRecords(
        [FromBody] DeleteConnectionRecordsRequest request,
        CancellationToken cancellationToken = default
    )
    {
        if (HttpContext.GetSubjectId() is not { } subjectId)
            return Unauthorized();

        var result = await _connectionService.DeleteRecordsAsync(
            subjectId, request.GrantIds, request.AllowReimport, cancellationToken);
        return result.Success
            ? Ok(result)
            : Problem(detail: result.Error, statusCode: 404, title: "Not Found");
    }

    /// <summary>
    /// Get a summary of data counts for a specific connector.
    /// Returns the number of entries, treatments, and device statuses synced by this connector.
    /// </summary>
    /// <param name="id">Connector ID (e.g., "dexcom")</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Data summary with counts by type</returns>
    [HttpGet("connectors/{id}/data-summary")]
    [RequireScope(Scope.ConnectorsRead)]
    [RemoteQuery]
    [ProducesResponseType(typeof(ConnectorDataSummary), 200)]
    [ProducesResponseType(500)]
    [ErrorEnvelope]
    public async Task<ActionResult<ConnectorDataSummary>> GetConnectorDataSummary(
        string id,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogDebug("Getting data summary for connector: {Id}", id);

        var summary = await _dataSourceService.GetConnectorDataSummaryAsync(
            id,
            cancellationToken
        );
        return Ok(summary);
    }

    /// <summary>
    /// Delete all data from a specific connector.
    /// WARNING: This is a destructive operation that cannot be undone.
    /// </summary>
    /// <param name="id">Connector ID (e.g., "dexcom")</param>
    /// <param name="allowReimport">
    /// Let the connector import the deleted records again; an enabled connector stays enabled and its
    /// next sync re-imports the history.
    /// </param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Result of the delete operation</returns>
    [HttpDelete("connectors/{id}/data")]
    [RequireScope(Scope.TenantSettings)]
    [DenyDemoSubject]
    [RemoteCommand(Invalidates = ["GetServicesOverview", "GetConnections", "GetActiveDataSources", "ConnectorStatus_GetStatus", "GetConnectorDataSummary"])]
    [ProducesResponseType(typeof(DataSourceDeleteResult), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(409)]
    [ProducesResponseType(500)]
    [ErrorEnvelope]
    public async Task<ActionResult<DataSourceDeleteResult>> DeleteConnectorData(
        string id,
        [FromQuery] bool allowReimport = false,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogWarning("Deleting all data for connector: {Id}", id);

        var result = await _dataSourceService.DeleteConnectorDataAsync(id, allowReimport, cancellationToken);

        if (!result.Success)
        {
            if (result.ErrorCode == DataSourceDeleteError.NotFound)
            {
                return Problem(detail: $"Connector not found: {id}", statusCode: 404, title: "Not Found");
            }
            if (result.ErrorCode == DataSourceDeleteError.SyncRunning)
            {
                return ConnectorMetadataService.GetByConnectorId(id)?.IsImport == true
                    ? this.ProblemWithCode(
                        ImportRunningCode,
                        "An import is running for this connector. Wait for it to end, then try again.",
                        StatusCodes.Status409Conflict)
                    : this.ProblemWithCode(
                        SyncRunningCode,
                        "A sync is running for this connector. Wait for it to end, then try again.",
                        StatusCodes.Status409Conflict);
            }
            return StatusCode(500, result);
        }

        return Ok(result);
    }

    /// <summary>
    /// Trigger a manual sync for a specific connector.
    /// </summary>
    /// <param name="id">Connector ID (e.g., "dexcom", "tidepool")</param>
    /// <param name="request">Sync request parameters (date range and data types)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Sync result with success status and details</returns>
    [HttpPost("connectors/{id}/sync")]
    [RequireScope(Scope.ConnectorsReadWrite)]
    [LimitDelegatedConnectorSync]
    [DenyDemoSubject]
    [RemoteCommand(
        Invalidates = [
            "GetServicesOverview",
            "GetActiveDataSources",
            "GetConnectorDataSummary",
            "GetConnectorSyncStatus",
        ]
    )]
    [ProducesResponseType(typeof(Nocturne.Connectors.Core.Models.SyncResult), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(409)]
    [ProducesResponseType(404)]
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

        if (request.WindowError() is { } windowError)
            return Problem(detail: windowError, statusCode: 400, title: "Bad Request");

        var result = await _connectorSyncService.TriggerSyncAsync(id, request, cancellationToken);
        return SyncOrConflict(result);
    }

    /// <summary>
    /// Reset a connector's sync cursor for the current tenant and re-pull historical data.
    /// Re-ingested records are deduplicated. The request runs synchronously: a full-history re-pull can take minutes and may exceed proxy or browser timeouts, so limit it with <c>from</c> or <c>dataTypes</c>.
    /// </summary>
    /// <remarks>
    /// The connector re-pulls the window up to now. Once it has signed in to its source, it
    /// forgets how far earlier syncs reached in each data type it re-pulls, and the sync schedule
    /// of those types. It keeps the record of older history an earlier import has still to fetch,
    /// so that import continues after the reset, and it keeps its vendor state. A reset whose
    /// sign-in is refused changes nothing. Re-ingested records are deduplicated on their
    /// idempotency keys (see the "Syncing" guide), so it is safe to run after fixing a connector
    /// bug to push corrected data to a tenant.
    ///
    /// <para><b>Latency:</b> this runs synchronously. A full-history re-pull with no lower bound
    /// re-ingests every data type over the connector's entire history (glucose can be tens of
    /// thousands of records), which is a multi-minute request and may approach or exceed
    /// reverse-proxy / browser timeouts. Scope it with <c>from</c> and/or <c>dataTypes</c> when
    /// possible. The cross-tenant equivalent
    /// (<c>POST /api/v4/admin/connectors/{tenantId}/reset-cursors</c>) runs as a background job to
    /// avoid this.</para>
    /// </remarks>
    /// <param name="id">Connector ID (e.g., "nightscout", "dexcom").</param>
    /// <param name="request">Optional lower bound and data-type filter. Omit <c>from</c> to re-pull all available history; omit <c>dataTypes</c> to reset every supported type.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Sync result with success status and details.</returns>
    [HttpPost("connectors/{id}/reset-cursor")]
    [RequireScope(Scope.ConnectorsReadWrite)]
    [LimitDelegatedConnectorSync]
    [DenyDemoSubject]
    [RemoteCommand(
        Invalidates = [
            "GetServicesOverview",
            "GetActiveDataSources",
            "GetConnectorDataSummary",
            "GetConnectorSyncStatus",
        ]
    )]
    [ProducesResponseType(typeof(Nocturne.Connectors.Core.Models.SyncResult), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(409)]
    [ProducesResponseType(404)]
    public async Task<
        ActionResult<Nocturne.Connectors.Core.Models.SyncResult>
    > ResetConnectorCursor(
        string id,
        [FromBody] ResetCursorRequest request,
        CancellationToken cancellationToken
    )
    {
        if (string.IsNullOrWhiteSpace(id))
            return Problem(detail: "Connector ID is required", statusCode: 400, title: "Bad Request");

        var syncRequest = Nocturne.Connectors.Core.Models.SyncRequest.Reset(
            request.From, DateTime.UtcNow, request.DataTypes);

        if (syncRequest.WindowError() is { } windowError)
            return Problem(detail: windowError, statusCode: 400, title: "Bad Request");

        _logger.LogInformation(
            "Cursor reset requested for connector {ConnectorId} (from {From})",
            id,
            request.From?.ToString("o", CultureInfo.InvariantCulture) ?? "beginning");

        var result = await _connectorSyncService.TriggerSyncAsync(id, syncRequest, cancellationToken);
        return SyncOrConflict(result);
    }

    /// <summary>
    /// Get sync status for a specific connector, including latest timestamps and connector state.
    /// Used by connectors on startup to determine where to resume syncing from.
    /// </summary>
    /// <param name="id">The connector ID (e.g., "dexcom", "librelinkup", "glooko")</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Complete sync status including timestamps for entries, treatments, and connector state</returns>
    [HttpGet("connectors/{id}/sync-status")]
    [RequireScope(Scope.ConnectorsRead)]
    [RemoteQuery]
    [ProducesResponseType(typeof(ConnectorSyncStatus), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    [ErrorEnvelope]
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

        if (ConnectorMetadataService.GetRegistrationByConnectorId(id)?.DataSourceId is not { Length: > 0 } dataSource)
        {
            return Problem(detail: $"Unknown connector '{id}'", statusCode: 404, title: "Not Found");
        }

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

    private ActionResult<Nocturne.Connectors.Core.Models.SyncResult> SyncOrConflict(
        Nocturne.Connectors.Core.Models.SyncResult result) =>
        result.AlreadyRunning
            ? Problem(detail: result.Message, statusCode: 409, title: "Conflict")
            : Ok(result);

    private string GetBaseUrl()
    {
        // Prefer the tenant's own subdomain ({slug}.{base-domain}). This is the URL
        // external uploaders (xDrip+, Loop, AAPS) must target, and it differs per
        // tenant — unlike the apex origin or the internal request host
        // seen when the web app calls this endpoint server-side.
        var slug = _tenantAccessor.Context?.Slug;
        var baseDomain = _baseDomain.BaseDomain;
        if (!string.IsNullOrEmpty(slug) && !string.IsNullOrEmpty(baseDomain))
        {
            return $"https://{slug}.{baseDomain.TrimEnd('/')}";
        }

        // Self-host / single-instance fallback: apex origin, then request host.
        if (!string.IsNullOrEmpty(_baseDomain.PublicOrigin))
        {
            return _baseDomain.PublicOrigin;
        }

        var request = HttpContext.Request;
        return $"{request.PublicScheme()}://{request.Host}";
    }

}

/// <summary>The credentials whose records <see cref="ServicesController.DeleteConnectionRecords"/> deletes.</summary>
public class DeleteConnectionRecordsRequest
{
    /// <summary>The grant ids of credentials of one connection, as the connections list shows them.</summary>
    public List<Guid> GrantIds { get; init; } = [];

    /// <summary>Let the credentials send the deleted records again.</summary>
    public bool AllowReimport { get; init; }
}

/// <summary>
/// Request body for resetting a connector's sync cursor and re-pulling history.
/// </summary>
public class ResetCursorRequest
{
    /// <summary>
    /// Optional lower bound for the re-pull. When null, no lower bound is applied and all
    /// available history is re-ingested.
    /// </summary>
    public DateTime? From { get; init; }

    /// <summary>
    /// Optional set of data types to reset. When null or empty, every data type the connector
    /// supports is re-pulled.
    /// </summary>
    public List<Nocturne.Connectors.Core.Models.SyncDataType>? DataTypes { get; init; }
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

/// <summary>Whether the tenant holds demo data.</summary>
public record DemoDataPresence(bool HasDemoData);
