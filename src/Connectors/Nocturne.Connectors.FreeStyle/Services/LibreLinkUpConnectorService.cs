using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using Nocturne.Connectors.Configurations;
using Nocturne.Connectors.Core.Constants;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.FreeStyle;
using Nocturne.Connectors.FreeStyle.Constants;
using Nocturne.Core.Models;
using Nocturne.Core.Constants;

#nullable enable

namespace Nocturne.Connectors.FreeStyle.Services
{
    /// <summary>
    /// Connector service for LibreLinkUp data source
    /// Enhanced implementation based on the original nightscout-connect LibreLinkUp implementation
    /// </summary>
    public class LibreConnectorService : BaseConnectorService<LibreLinkUpConnectorConfiguration>
    {
        private readonly LibreLinkUpConnectorConfiguration _config;
        private readonly IRetryDelayStrategy _retryDelayStrategy;
        private readonly IRateLimitingStrategy _rateLimitingStrategy;
        private readonly IAuthTokenProvider _tokenProvider;
        private LibreUserConnection? _selectedConnection;
        private int _failedRequestCount = 0;

        private static readonly Dictionary<string, string> KnownEndpoints = new()
        {
            { "AE", LibreLinkUpConstants.Endpoints.AE },
            { "AP", LibreLinkUpConstants.Endpoints.AP },
            { "AU", LibreLinkUpConstants.Endpoints.AU },
            { "CA", LibreLinkUpConstants.Endpoints.CA },
            { "DE", LibreLinkUpConstants.Endpoints.DE },
            { "EU", LibreLinkUpConstants.Endpoints.EU },
            { "EU2", LibreLinkUpConstants.Endpoints.EU2 },
            { "FR", LibreLinkUpConstants.Endpoints.FR },
            { "JP", LibreLinkUpConstants.Endpoints.JP },
            { "US", LibreLinkUpConstants.Endpoints.US },
        };

        private static readonly Dictionary<int, Direction> TrendArrowMap = new()
        {
            { 1, Direction.SingleDown },
            { 2, Direction.FortyFiveDown },
            { 3, Direction.Flat },
            { 4, Direction.FortyFiveUp },
            { 5, Direction.SingleUp },
        };

        public override string ServiceName => "LibreLinkUp";

        /// <summary>
        /// Gets the source identifier for this connector
        /// </summary>
        public override string ConnectorSource => DataSources.LibreConnector;

        public override List<SyncDataType> SupportedDataTypes => new() { SyncDataType.Glucose };

        public LibreConnectorService(
            HttpClient httpClient,
            IOptions<LibreLinkUpConnectorConfiguration> config,
            ILogger<LibreConnectorService> logger,
            IRetryDelayStrategy retryDelayStrategy,
            IRateLimitingStrategy rateLimitingStrategy,
            IAuthTokenProvider tokenProvider,
            IApiDataSubmitter? apiDataSubmitter = null,
            IConnectorMetricsTracker? metricsTracker = null,
            IConnectorStateService? stateService = null
        )
            : base(httpClient, logger, apiDataSubmitter, metricsTracker, stateService)
        {
            _config = config?.Value ?? throw new ArgumentNullException(nameof(config));
            _retryDelayStrategy =
                retryDelayStrategy ?? throw new ArgumentNullException(nameof(retryDelayStrategy));
            _rateLimitingStrategy =
                rateLimitingStrategy
                ?? throw new ArgumentNullException(nameof(rateLimitingStrategy));
            _tokenProvider = tokenProvider ?? throw new ArgumentNullException(nameof(tokenProvider));
        }

        public override async Task<bool> AuthenticateAsync()
        {
            var token = await _tokenProvider.GetValidTokenAsync();
            if (token == null)
            {
                _failedRequestCount++;
                return false;
            }

            // Set up authorization header for subsequent requests
            _httpClient.DefaultRequestHeaders.Remove("Authorization");
            _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");

            // Get connections to find the patient data
            await LoadConnectionsAsync();

            _failedRequestCount = 0;
            return true;
        }

        private async Task LoadConnectionsAsync()
        {
            try
            {
                var response = await _httpClient.GetAsync(
                    LibreLinkUpConstants.ApiPaths.Connections
                );

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning(
                        "Failed to load LibreLinkUp connections: {StatusCode}",
                        response.StatusCode
                    );
                    return;
                }

                var responseContent = await response.Content.ReadAsStringAsync();
                var connectionsResponse = JsonSerializer.Deserialize<LibreConnectionsResponse>(
                    responseContent
                );

                if (connectionsResponse?.Data == null || connectionsResponse.Data.Length == 0)
                {
                    _logger.LogWarning("No LibreLinkUp connections found");
                    return;
                }

                // Select the specified patient or the first available connection
                if (!string.IsNullOrEmpty(_config.LibrePatientId))
                {
                    _selectedConnection = connectionsResponse.Data.FirstOrDefault(c =>
                        c.PatientId == _config.LibrePatientId
                    );
                }

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
            const int maxRetries = 3;

            try
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
                        return Enumerable.Empty<Entry>();
                    }
                }

                var url = string.Format(
                    LibreLinkUpConstants.ApiPaths.GraphData,
                    _selectedConnection?.PatientId!
                );

                return await FetchGlucoseDataWithRetryAsync(url, since, maxRetries);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching glucose data from LibreLinkUp");
                _failedRequestCount++;
                return Enumerable.Empty<Entry>();
            }
        }

        private async Task<IEnumerable<Entry>> FetchGlucoseDataWithRetryAsync(
            string url,
            DateTime? since,
            int maxRetries
        )
        {
            HttpRequestException? lastException = null;

            for (int attempt = 0; attempt < maxRetries; attempt++)
            {
                try
                {
                    // Apply rate limiting
                    await _rateLimitingStrategy.ApplyDelayAsync(attempt);

                    _logger.LogDebug(
                        "Fetching LibreLinkUp glucose data: {Url} (attempt {Attempt}/{MaxRetries})",
                        url,
                        attempt + 1,
                        maxRetries
                    );

                    var response = await _httpClient.GetAsync(url);

                    if (response.IsSuccessStatusCode)
                    {
                        var jsonContent = await response.Content.ReadAsStringAsync();
                        var graphResponse = JsonSerializer.Deserialize<LibreGraphResponse>(
                            jsonContent
                        );

                        if (graphResponse?.Data?.Connection?.GlucoseMeasurement == null)
                        {
                            _logger.LogDebug("No glucose data returned from LibreLinkUp");
                            return Enumerable.Empty<Entry>();
                        }

                        var measurements =
                            graphResponse.Data.Connection.GlucoseMeasurement.ToList();
                        var glucoseEntries = measurements
                            .Where(measurement =>
                                measurement != null && measurement.ValueInMgPerDl > 0
                            )
                            .Select(ConvertLibreEntry)
                            .Where(entry =>
                                entry != null && (!since.HasValue || entry.Date > since.Value)
                            )
                            .OrderBy(entry => entry.Date)
                            .ToList();

                        // Reset failed request count on successful data fetch
                        _failedRequestCount = 0;

                        _logger.LogInformation(
                            "[{ConnectorSource}] Successfully fetched {Count} glucose entries from LibreLinkUp",
                            ConnectorSource,
                            glucoseEntries.Count
                        );
                        return glucoseEntries;
                    }
                    else
                    {
                        var errorContent = await response.Content.ReadAsStringAsync();

                        // Handle specific error cases
                        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                        {
                            _logger.LogWarning(
                                "LibreLinkUp returned unauthorized, clearing token and attempting re-authentication"
                            );
                            _tokenProvider.InvalidateToken();
                            _selectedConnection = null;

                            // Try to re-authenticate once
                            if (await AuthenticateAsync())
                            {
                                _logger.LogInformation(
                                    "Re-authentication successful, retrying data fetch"
                                );
                                return await FetchGlucoseDataWithRetryAsync(
                                    url,
                                    since,
                                    maxRetries - attempt
                                );
                            }
                            else
                            {
                                _logger.LogError("Re-authentication failed");
                                _failedRequestCount++;
                                return Enumerable.Empty<Entry>();
                            }
                        }
                        else if (
                            response.StatusCode == System.Net.HttpStatusCode.TooManyRequests
                            || response.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable
                            || response.StatusCode == System.Net.HttpStatusCode.InternalServerError
                        )
                        {
                            // Retryable errors
                            lastException = new HttpRequestException(
                                $"HTTP {(int)response.StatusCode} {response.StatusCode}: {errorContent}"
                            );
                            _logger.LogWarning(
                                "LibreLinkUp data fetch failed with retryable error on attempt {Attempt}: {StatusCode} - {Error}",
                                attempt + 1,
                                response.StatusCode,
                                errorContent
                            );

                            if (attempt < maxRetries - 1)
                            {
                                _logger.LogInformation(
                                    "Applying retry backoff before attempt {NextAttempt}",
                                    attempt + 2
                                );
                                await _retryDelayStrategy.ApplyRetryDelayAsync(attempt);
                                continue;
                            }
                        }
                        else
                        {
                            // Non-retryable error
                            _logger.LogError(
                                "LibreLinkUp data fetch failed with non-retryable error: {StatusCode} - {Error}",
                                response.StatusCode,
                                errorContent
                            );
                            _failedRequestCount++;
                            return Enumerable.Empty<Entry>();
                        }
                    }
                }
                catch (HttpRequestException ex)
                    when (ex.Message.Contains("timeout") || ex.Message.Contains("network"))
                {
                    lastException = ex;
                    _logger.LogWarning(
                        ex,
                        "Network error during LibreLinkUp data fetch attempt {Attempt}: {Message}",
                        attempt + 1,
                        ex.Message
                    );

                    if (attempt < maxRetries - 1)
                    {
                        _logger.LogInformation(
                            "Applying retry backoff before attempt {NextAttempt}",
                            attempt + 2
                        );
                        await _retryDelayStrategy.ApplyRetryDelayAsync(attempt);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        ex,
                        "Unexpected error during LibreLinkUp data fetch attempt {Attempt}",
                        attempt + 1
                    );
                    _failedRequestCount++;
                    throw;
                }
            }

            // All attempts failed
            _failedRequestCount++;
            _logger.LogError(
                "LibreLinkUp data fetch failed after {MaxRetries} attempts",
                maxRetries
            );

            if (lastException != null)
            {
                throw lastException;
            }

            return Enumerable.Empty<Entry>();
        }

        /// <summary>
        /// Get the current health status of the connector
        /// </summary>
        public bool IsHealthy =>
            _failedRequestCount < LibreLinkUpConstants.Configuration.MaxHealthFailures
            && !_tokenProvider.IsTokenExpired;

        /// <summary>
        /// Get the current failed request count
        /// </summary>
        public int FailedRequestCount => _failedRequestCount;

        /// <summary>
        /// Reset the failed request counter (useful for health monitoring)
        /// </summary>
        public void ResetFailedRequestCount()
        {
            _failedRequestCount = 0;
        }

        private Entry ConvertLibreEntry(LibreGlucoseMeasurement measurement)
        {
            try
            {
                var timestamp = DateTime.Parse(measurement.FactoryTimestamp);

                // Adjust timezone offset
                var offset = TimeSpan.FromMinutes(
                    timestamp.Kind == DateTimeKind.Local
                        ? TimeZoneInfo.Local.GetUtcOffset(timestamp).TotalMinutes
                        : 0
                );
                timestamp = timestamp.Subtract(offset);
                var direction = TrendArrowMap.GetValueOrDefault(
                    measurement.TrendArrow,
                    Direction.NotComputable
                );
                return new Entry
                {
                    Date = timestamp,
                    Sgv = measurement.ValueInMgPerDl,
                    Direction = direction.ToString(),
                    Device = LibreLinkUpConstants.Configuration.DeviceIdentifier,
                    Type = LibreLinkUpConstants.Configuration.EntryType,
                };
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error converting LibreLinkUp entry: {@Entry}", measurement);
                return new Entry { Type = "sgv", Device = "nightscout-connect-libre-linkup" };
            }
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
        }

        private class LibreConnection
        {
            public required LibreGlucoseMeasurement[] GlucoseMeasurement { get; set; }
        }

        private class LibreGlucoseMeasurement
        {
            public required string FactoryTimestamp { get; set; }
            public int ValueInMgPerDl { get; set; }
            public int TrendArrow { get; set; }
        }
    }
}
