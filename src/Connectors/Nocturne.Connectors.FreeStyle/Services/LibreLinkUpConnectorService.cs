using System.IdentityModel.Tokens.Jwt;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.Core.Utilities;
using Nocturne.Connectors.FreeStyle.Configurations;
using Nocturne.Connectors.FreeStyle.Mappers;
using Nocturne.Connectors.FreeStyle.Models;
using Nocturne.Core.Constants;
using Nocturne.Core.Models;

namespace Nocturne.Connectors.FreeStyle.Services;

/// <summary>
///     Connector service for LibreLinkUp data source
///     Enhanced implementation based on the original nightscout-connect LibreLinkUp implementation
/// </summary>
public class LibreConnectorService(
    HttpClient httpClient,
    IOptions<LibreLinkUpConnectorConfiguration> config,
    ILogger<LibreConnectorService> logger,
    IRetryDelayStrategy retryDelayStrategy,
    IRateLimitingStrategy rateLimitingStrategy,
    LibreLinkAuthTokenProvider tokenProvider,
    IConnectorPublisher? publisher = null
)
    : BaseConnectorService<LibreLinkUpConnectorConfiguration>(
        httpClient,
        logger,
        publisher
    )
{
    private readonly LibreLinkUpConnectorConfiguration _config =
        config?.Value ?? throw new ArgumentNullException(nameof(config));

    private readonly LibreEntryMapper _entryMapper = new(logger);

    private readonly IRateLimitingStrategy _rateLimitingStrategy =
        rateLimitingStrategy ?? throw new ArgumentNullException(nameof(rateLimitingStrategy));

    private readonly IRetryDelayStrategy _retryDelayStrategy =
        retryDelayStrategy ?? throw new ArgumentNullException(nameof(retryDelayStrategy));

    private readonly LibreLinkAuthTokenProvider _tokenProvider =
        tokenProvider ?? throw new ArgumentNullException(nameof(tokenProvider));

    private string _accountIdHash = string.Empty;
    private LibreUserConnection? _selectedConnection;

    /// <summary>
    ///     Custom headers to include in API requests (Account-Id for LibreLinkUp)
    /// </summary>
    private Dictionary<string, string>? RequestHeaders =>
        string.IsNullOrWhiteSpace(_accountIdHash)
            ? null
            : new Dictionary<string, string> { { "Account-Id", _accountIdHash } };

    public override string ServiceName => "LibreLinkUp";

    /// <summary>
    ///     Gets the source identifier for this connector
    /// </summary>
    protected override string ConnectorSource => DataSources.LibreConnector;

    public override List<SyncDataType> SupportedDataTypes => [SyncDataType.Glucose];

    /// <summary>
    ///     Override health check to also consider token expiry
    /// </summary>
    public override bool IsHealthy => base.IsHealthy && !_tokenProvider.IsTokenExpired;

    public override async Task<bool> AuthenticateAsync()
    {
        var token = await _tokenProvider.GetValidTokenAsync();
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

        // Set up authorization header for subsequent requests
        _httpClient.DefaultRequestHeaders.Remove("Authorization");
        _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");

        // Get connections to find the patient data
        await LoadConnectionsAsync();

        TrackSuccessfulRequest();
        return true;
    }

    private async Task LoadConnectionsAsync()
    {
        try
        {
            var response = await GetWithHeadersAsync(
                LibreLinkUpConstants.ApiPaths.Connections,
                RequestHeaders
            );

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Failed to load LibreLinkUp connections: {StatusCode}",
                    response.StatusCode
                );
                return;
            }

            var connectionsResponse = await DeserializeResponseAsync<LibreConnectionsResponse>(
                response
            );

            if (connectionsResponse?.Data == null || connectionsResponse.Data.Length == 0)
            {
                _logger.LogWarning("No LibreLinkUp connections found");
                return;
            }

            // Select the specified patient or the first available connection
            if (!string.IsNullOrEmpty(_config.PatientId))
                _selectedConnection = connectionsResponse.Data.FirstOrDefault(c =>
                    c.PatientId == _config.PatientId
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

    public override async Task<IEnumerable<Entry>> FetchGlucoseDataAsync(DateTime? since = null)
    {
        // Check if we need to authenticate or re-authenticate
        if (_tokenProvider.IsTokenExpired || _selectedConnection == null)
        {
            _logger.LogInformation(
                "Token expired or missing connection, attempting to re-authenticate"
            );
            if (!await AuthenticateAsync())
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

        var url = string.Format(
            LibreLinkUpConstants.ApiPaths.GraphData,
            _selectedConnection.PatientId
        );

        // Apply rate limiting before first attempt
        await _rateLimitingStrategy.ApplyDelayAsync(0);

        var result = await ExecuteWithRetryAsync(
            async () => await FetchGlucoseDataCoreAsync(url, since),
            _retryDelayStrategy,
            async () =>
            {
                _tokenProvider.InvalidateToken();
                _selectedConnection = null;
                return await AuthenticateAsync();
            },
            operationName: "FetchGlucoseData"
        );

        return result ?? Enumerable.Empty<Entry>();
    }

    /// <summary>
    ///     Core glucose data fetch logic without retry handling (called by ExecuteWithRetryAsync)
    /// </summary>
    private async Task<List<Entry>?> FetchGlucoseDataCoreAsync(string url, DateTime? since)
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

        // Only add latest measurement if not already in GraphData (avoid duplicates)
        var latestTimestamp = latestMeasurement.FactoryTimestamp;
        if (!measurements.Any(m => m.FactoryTimestamp == latestTimestamp))
        {
            measurements.Add(latestMeasurement);
        }
        else
        {
            // Update existing entry with trend arrow from latest measurement
            var existing = measurements.First(m => m.FactoryTimestamp == latestTimestamp);
            if (existing.TrendArrow == 0 && latestMeasurement.TrendArrow != 0)
            {
                existing.TrendArrow = latestMeasurement.TrendArrow;
            }
        }

        var glucoseEntries = measurements
            .Where(measurement => measurement.ValueInMgPerDl > 0)
            .Select(_entryMapper.ConvertLibreEntry)
            .Where(entry => !since.HasValue || entry.Date > since.Value)
            .OrderBy(entry => entry.Date)
            .ToList();

        _logger.LogInformation(
            "[{ConnectorSource}] Successfully fetched {Count} glucose entries from LibreLinkUp",
            ConnectorSource,
            glucoseEntries.Count
        );

        return glucoseEntries;
    }

    private class LibreLoginResponse
    {
        public required LibreLoginData Data { get; set; }
    }

    private class LibreLoginData
    {
        public required LibreAuthTicket AuthTicket { get; set; }
    }

    private class LibreAuthTicket
    {
        public required string Token { get; set; }
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