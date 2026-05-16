using System.IdentityModel.Tokens.Jwt;
using Microsoft.Extensions.Logging;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.Core.Utilities;
using Nocturne.Connectors.FreeStyle.Configurations;
using Nocturne.Connectors.FreeStyle.Mappers;
using Nocturne.Connectors.FreeStyle.Models;
using Nocturne.Core.Constants;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.Connectors.FreeStyle.Services;

/// <summary>
///     Connector service for LibreLinkUp data source.
///     Writes SensorGlucose records directly instead of legacy Entry objects.
/// </summary>
public class LibreConnectorService(
    HttpClient httpClient,
    IConnectorServerResolver<LibreLinkUpConnectorConfiguration> serverResolver,
    ILogger<LibreConnectorService> logger,
    IRetryDelayStrategy retryDelayStrategy,
    IRateLimitingStrategy rateLimitingStrategy,
    LibreLinkAuthTokenProvider tokenProvider,
    IConnectorPublisher? publisher = null
)
    : BaseConnectorService<LibreLinkUpConnectorConfiguration>(
        httpClient,
        serverResolver,
        logger,
        publisher
    )
{
    private readonly LibreSensorGlucoseMapper _sensorGlucoseMapper = new(logger);

    private readonly IRateLimitingStrategy _rateLimitingStrategy =
        rateLimitingStrategy ?? throw new ArgumentNullException(nameof(rateLimitingStrategy));

    private readonly IRetryDelayStrategy _retryDelayStrategy =
        retryDelayStrategy ?? throw new ArgumentNullException(nameof(retryDelayStrategy));

    private readonly LibreLinkAuthTokenProvider _tokenProvider =
        tokenProvider ?? throw new ArgumentNullException(nameof(tokenProvider));

    private string _accountIdHash = string.Empty;
    private string? _bearerToken;
    private LibreUserConnection? _selectedConnection;

    private Dictionary<string, string>? RequestHeaders
    {
        get
        {
            Dictionary<string, string>? headers = null;

            if (!string.IsNullOrWhiteSpace(_accountIdHash))
            {
                headers = new Dictionary<string, string> { { "Account-Id", _accountIdHash } };
            }

            if (!string.IsNullOrEmpty(_bearerToken))
            {
                headers ??= new Dictionary<string, string>();
                headers["Authorization"] = $"Bearer {_bearerToken}";
            }

            return headers;
        }
    }

    public override string ServiceName => "LibreLinkUp";
    protected override string ConnectorSource => DataSources.LibreConnector;
    public override List<SyncDataType> SupportedDataTypes => [SyncDataType.Glucose];

    public override bool IsHealthy => base.IsHealthy && !_tokenProvider.IsTokenExpired;

    public override async Task<bool> AuthenticateAsync()
    {
        // Legacy method; actual auth happens per-tenant in sync flow
        TrackSuccessfulRequest();
        return true;
    }

    private async Task<bool> AuthenticateWithConfigAsync(LibreLinkUpConnectorConfiguration config)
    {
        var token = await _tokenProvider.GetValidTokenAsync(config);
        if (token == null)
        {
            _accountIdHash = string.Empty;
            TrackFailedRequest("Failed to get valid token");
            return false;
        }

        _accountIdHash = string.Empty;
        try
        {
            var handler = new JwtSecurityTokenHandler();
            var jwt = handler.ReadToken(token) as JwtSecurityToken;
            if (jwt is null) _logger.LogWarning("LibreLinkUp token is not a valid JWT");

            if (jwt is not null)
            {
                var claim = jwt.Claims.FirstOrDefault(c => c.Type == "id");
                if (claim?.Value is { Length: > 0 } value) _accountIdHash = HashUtils.Sha256Hex(value);
                if (_accountIdHash.Length == 0) _logger.LogWarning("LibreLinkUp token missing id claim");
            }
        }
        catch (ArgumentException)
        {
            _logger.LogWarning("LibreLinkUp token is not a valid JWT");
        }

        _bearerToken = token;

        await LoadConnectionsAsync(config);

        TrackSuccessfulRequest();
        return true;
    }

    /// <summary>
    ///     Fetches SensorGlucose records from the LibreLinkUp API.
    /// </summary>
    private async Task<IEnumerable<SensorGlucose>> FetchSensorGlucoseAsync(
        LibreLinkUpConnectorConfiguration config, DateTime? since = null)
    {
        if (_tokenProvider.IsTokenExpired || _selectedConnection == null)
        {
            _logger.LogInformation("Token expired or missing connection, attempting to re-authenticate");
            if (!await AuthenticateWithConfigAsync(config))
            {
                _logger.LogError("Failed to authenticate with LibreLinkUp");
                return [];
            }
        }

        if (string.IsNullOrWhiteSpace(_selectedConnection?.PatientId))
        {
            _logger.LogError("Invalid LibreLinkUp patient id");
            TrackFailedRequest("Invalid patient id");
            return [];
        }

        var url = _serverResolver.BuildUrl(config,
            string.Format(LibreLinkUpConstants.ApiPaths.GraphData, _selectedConnection.PatientId));

        await _rateLimitingStrategy.ApplyDelayAsync(0);

        var result = await ExecuteWithRetryAsync(
            async () => await FetchSensorGlucoseCoreAsync(url, since),
            _retryDelayStrategy,
            async () =>
            {
                _tokenProvider.InvalidateToken();
                _selectedConnection = null;
                return await AuthenticateWithConfigAsync(config);
            },
            operationName: "FetchSensorGlucoseData"
        );

        return result ?? [];
    }

    /// <summary>
    ///     Performs sync, publishing SensorGlucose records directly to the V4 data store.
    /// </summary>
    protected override async Task<SyncResult> PerformSyncInternalAsync(
        SyncRequest request,
        LibreLinkUpConnectorConfiguration config,
        CancellationToken cancellationToken,
        ISyncProgressReporter? progressReporter = null
    )
    {
        var result = new SyncResult { StartTime = DateTimeOffset.UtcNow, Success = true };

        var enabledTypes = config.GetEnabledDataTypes(SupportedDataTypes);
        if (!enabledTypes.Contains(SyncDataType.Glucose))
        {
            result.EndTime = DateTimeOffset.UtcNow;
            return result;
        }

        try
        {
            var sensorGlucose = await FetchSensorGlucoseAsync(config, request.From);
            var sgList = sensorGlucose.ToList();

            if (sgList.Count > 0)
            {
                var success = await PublishSensorGlucoseDataAsync(sgList, config, cancellationToken);
                result.ItemsSynced[SyncDataType.Glucose] = sgList.Count;
                result.LastEntryTimes[SyncDataType.Glucose] = DateTimeOffset
                    .FromUnixTimeMilliseconds(sgList.Max(s => s.Mills))
                    .UtcDateTime;

                if (!success)
                {
                    result.Success = false;
                    result.Errors.Add("SensorGlucose publish failed");
                }
                else
                {
                    _logger.LogInformation("Synced {Count} SensorGlucose records from LibreLinkUp", sgList.Count);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during LibreLinkUp sync");
            result.Success = false;
            result.Errors.Add($"Sync error: {ex.Message}");
        }

        result.EndTime = DateTimeOffset.UtcNow;
        return result;
    }

    private async Task LoadConnectionsAsync(LibreLinkUpConnectorConfiguration config)
    {
        try
        {
            var response = await GetWithHeadersAsync(
                _serverResolver.BuildUrl(config, LibreLinkUpConstants.ApiPaths.Connections),
                RequestHeaders);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Failed to load LibreLinkUp connections: {StatusCode}", response.StatusCode);
                return;
            }

            var connectionsResponse = await DeserializeResponseAsync<LibreConnectionsResponse>(response);

            if (connectionsResponse?.Data == null || connectionsResponse.Data.Length == 0)
            {
                _logger.LogWarning("No LibreLinkUp connections found");
                return;
            }

            if (!string.IsNullOrEmpty(config.PatientId))
                _selectedConnection = connectionsResponse.Data.FirstOrDefault(c =>
                    c.PatientId == config.PatientId
                );

            if (_selectedConnection == null)
            {
                _selectedConnection = connectionsResponse.Data.First();
                _logger.LogInformation(
                    "Selected LibreLinkUp connection: {PatientName} ({PatientId})",
                    _selectedConnection.FirstName + " " + _selectedConnection.LastName,
                    _selectedConnection.PatientId
                );
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading LibreLinkUp connections");
        }
    }

    private async Task<List<SensorGlucose>?> FetchSensorGlucoseCoreAsync(string url, DateTime? since)
    {
        var response = await GetWithHeadersAsync(url, RequestHeaders);

        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync();
            throw new HttpRequestException(
                $"HTTP {(int)response.StatusCode} {response.StatusCode}: {errorContent}",
                null,
                response.StatusCode
            );
        }

        var graphResponse = await DeserializeResponseAsync<LibreGraphResponse>(response);

        if (graphResponse?.Data?.GraphData == null || graphResponse.Data.GraphData.Length == 0)
        {
            _logger.LogDebug("No glucose data returned from LibreLinkUp");
            return [];
        }

        var measurements = graphResponse.Data.GraphData.ToList();
        var latestMeasurement = graphResponse.Data.Connection.GlucoseMeasurement;

        var latestTimestamp = latestMeasurement.FactoryTimestamp;
        if (!measurements.Any(m => m.FactoryTimestamp == latestTimestamp))
        {
            measurements.Add(latestMeasurement);
        }
        else
        {
            var existing = measurements.First(m => m.FactoryTimestamp == latestTimestamp);
            if (existing.TrendArrow == 0 && latestMeasurement.TrendArrow != 0)
                existing.TrendArrow = latestMeasurement.TrendArrow;
        }

        var glucoseRecords = measurements
            .Where(m => m.ValueInMgPerDl > 0)
            .Select(_sensorGlucoseMapper.ConvertMeasurement)
            .Where(sg => sg != null)
            .Cast<SensorGlucose>()
            .Where(sg => !since.HasValue || DateTimeOffset.FromUnixTimeMilliseconds(sg.Mills).UtcDateTime > since.Value)
            .OrderBy(sg => sg.Mills)
            .ToList();

        _logger.LogInformation(
            "[{ConnectorSource}] Successfully fetched {Count} SensorGlucose records from LibreLinkUp",
            ConnectorSource,
            glucoseRecords.Count
        );

        return glucoseRecords;
    }

    private class LibreConnectionsResponse
    {
        public required LibreUserConnection[] Data { get; set; }
    }

    private class LibreUserConnection
    {
        public required string PatientId { get; set; }
        public required string FirstName { get; set; }
        public required string LastName { get; set; }
    }

    private class LibreGraphResponse
    {
        public required LibreConnectionData Data { get; set; }
    }

    private class LibreConnectionData
    {
        public required LibreConnection Connection { get; set; }
        public required LibreGlucoseMeasurement[] GraphData { get; set; }
    }

    private class LibreConnection
    {
        public required LibreGlucoseMeasurement GlucoseMeasurement { get; set; }
    }
}
