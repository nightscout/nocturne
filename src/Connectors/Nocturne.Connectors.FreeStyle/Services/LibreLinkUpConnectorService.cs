using System.IdentityModel.Tokens.Jwt;
using Microsoft.Extensions.Logging;
using Nocturne.Connectors.Core.Extensions;
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
    private LibreSensor? _currentSensor;
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

    private async Task<bool> AuthenticateWithConfigAsync(
        LibreLinkUpConnectorConfiguration config, CancellationToken cancellationToken)
    {
        var token = await _tokenProvider.GetValidTokenAsync(config, cancellationToken);
        if (token == null)
        {
            _accountIdHash = string.Empty;
            TrackFailedAuthentication(_tokenProvider.SignInFailureReason);
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

        await LoadConnectionsAsync(config, cancellationToken);

        TrackSuccessfulRequest();
        return true;
    }

    /// <summary>
    ///     Fetches SensorGlucose records from the LibreLinkUp API. Null when the fetch could not
    ///     complete, which the sync reports as a failure; an empty list is the source's answer of
    ///     nothing new.
    /// </summary>
    private async Task<List<SensorGlucose>?> FetchSensorGlucoseAsync(
        LibreLinkUpConnectorConfiguration config, DateTime? since, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_selectedConnection?.PatientId))
        {
            _logger.LogError("Invalid LibreLinkUp patient id");
            TrackFailedRequest("Invalid patient id");
            return null;
        }

        var url = _serverResolver.BuildUrl(config,
            string.Format(LibreLinkUpConstants.ApiPaths.GraphData, _selectedConnection.PatientId));

        await _rateLimitingStrategy.ApplyDelayAsync(0);

        var result = await ExecuteWithRetryAsync(
            async () => await FetchSensorGlucoseCoreAsync(url, since, cancellationToken),
            _retryDelayStrategy,
            async () =>
            {
                _tokenProvider.InvalidateToken();
                _selectedConnection = null;
                return await AuthenticateWithConfigAsync(config, cancellationToken);
            },
            maxRetries: config.MaxRetryAttempts,
            operationName: "FetchSensorGlucoseData",
            cancellationToken: cancellationToken
        );

        return result;
    }

    /// <summary>
    ///     Performs sync, publishing SensorGlucose records directly to the V4 data store.
    /// </summary>
    protected override async Task<SyncResult> PerformSyncInternalAsync(
        SyncRequest request,
        LibreLinkUpConnectorConfiguration config,
        CancellationToken cancellationToken)
    {
        var result = new SyncResult { Success = true };

        var activeTypes = ResolveActiveTypes(request, config);
        // The sensor arrives with the glucose graph, so a run wanting only device events still fetches it.
        if (!activeTypes.Contains(SyncDataType.Glucose) && !activeTypes.Contains(SyncDataType.DeviceEvents))
        {
            return result;
        }

        if (_tokenProvider.IsTokenExpired || _selectedConnection == null)
        {
            _logger.LogInformation("Token expired or missing connection, attempting to re-authenticate");
            if (!await AuthenticateWithConfigAsync(config, cancellationToken))
            {
                _logger.LogError("Failed to authenticate with LibreLinkUp");
                return AuthenticationFailedResult();
            }
        }

        try
        {
            var sensorGlucose = await FetchSensorGlucoseAsync(config, request.From, cancellationToken);

            if (sensorGlucose == null)
            {
                RecordFetchFailure(result, SyncDataType.Glucose, activeTypes);
                RecordFetchFailure(result, SyncDataType.DeviceEvents, activeTypes);
                return result;
            }

            await PublishRecordTypeAsync(result, SyncDataType.Glucose, activeTypes,
                sensorGlucose, PublishSensorGlucoseDataAsync, config, cancellationToken);

            List<DeviceEvent> sensorStarts = LibreSensorSessionMapper.ToSensorStart(_currentSensor) is { } start ? [start] : [];
            await PublishRecordTypeAsync(result, SyncDataType.DeviceEvents, activeTypes,
                sensorStarts, PublishDeviceEventDataAsync, config, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            RecordSyncError(result, ex);
        }

        return result;
    }

    private async Task LoadConnectionsAsync(
        LibreLinkUpConnectorConfiguration config, CancellationToken cancellationToken)
    {
        try
        {
            var response = await GetWithHeadersAsync(
                _serverResolver.BuildUrl(config, LibreLinkUpConstants.ApiPaths.Connections),
                RequestHeaders,
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Failed to load LibreLinkUp connections: {StatusCode}", response.StatusCode);
                return;
            }

            var connectionsResponse = await DeserializeResponseAsync<LibreConnectionsResponse>(response);

            var connections = ConnectorFaults.NonNullRecords(
                connectionsResponse?.Data, _logger, ConnectorSource, "LibreLinkUp connection");
            if (connections.Count == 0)
            {
                _logger.LogWarning("No LibreLinkUp connections found");
                return;
            }

            if (!string.IsNullOrEmpty(config.PatientId))
                _selectedConnection = connections.FirstOrDefault(c =>
                    c.PatientId == config.PatientId
                );

            if (_selectedConnection == null)
            {
                _selectedConnection = connections[0];
                _logger.LogInformation(
                    "Selected LibreLinkUp connection: {PatientName} ({PatientId})",
                    _selectedConnection.FirstName + " " + _selectedConnection.LastName,
                    _selectedConnection.PatientId
                );
            }
        }
        catch (Exception ex) when (ConnectorFaults.IsSourceFailure(ex, cancellationToken))
        {
            _logger.LogError(ex, "Error loading LibreLinkUp connections");
        }
    }

    private async Task<List<SensorGlucose>?> FetchSensorGlucoseCoreAsync(
        string url, DateTime? since, CancellationToken cancellationToken)
    {
        var response = await GetWithHeadersAsync(url, RequestHeaders, cancellationToken);

        await response.EnsureSuccessWithBodyAsync();

        var graphResponse = await DeserializeResponseAsync<LibreGraphResponse>(response);
        _currentSensor = graphResponse?.Data?.Connection?.Sensor;

        if (graphResponse?.Data?.GraphData == null || graphResponse.Data.GraphData.Length == 0)
        {
            _logger.LogDebug("No glucose data returned from LibreLinkUp");
            return [];
        }

        var measurements = ConnectorFaults.NonNullRecords(
            graphResponse.Data.GraphData, _logger, ConnectorSource, "LibreLinkUp measurement").ToList();
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
        public LibreSensor? Sensor { get; set; }
    }
}
