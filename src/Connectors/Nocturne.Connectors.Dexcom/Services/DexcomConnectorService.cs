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
using Nocturne.Connectors.Dexcom.Constants;
using Nocturne.Core.Models;
using Nocturne.Core.Constants;

#nullable enable

namespace Nocturne.Connectors.Dexcom.Services
{
    /// <summary>
    /// Connector service for Dexcom Share data source
    /// Enhanced implementation based on the original nightscout-connect Dexcom Share implementation
    /// </summary>
    public class DexcomConnectorService : BaseConnectorService<DexcomConnectorConfiguration>
    {
        private readonly DexcomConnectorConfiguration _config;
        private readonly IRetryDelayStrategy _retryDelayStrategy;
        private readonly IRateLimitingStrategy _rateLimitingStrategy;
        private readonly IAuthTokenProvider _tokenProvider;
        private readonly IConnectorFileService<DexcomEntry[]>? _fileService = null; // Optional file service for data persistence
        private int _failedRequestCount = 0;
        private static readonly Dictionary<string, string> KnownServers = new()
        {
            { "us", DexcomConstants.Servers.US },
            { "ous", DexcomConstants.Servers.OUS },
        };
        private static readonly Dictionary<int, Direction> TrendDirections = new()
        {
            { 0, Direction.NONE },
            { 1, Direction.DoubleUp },
            { 2, Direction.SingleUp },
            { 3, Direction.FortyFiveUp },
            { 4, Direction.Flat },
            { 5, Direction.FortyFiveDown },
            { 6, Direction.SingleDown },
            { 7, Direction.DoubleDown },
            { 8, Direction.NotComputable },
            { 9, Direction.RateOutOfRange },
        };

        /// <summary>
        /// Gets whether the connector is in a healthy state based on recent request failures
        /// </summary>
        public bool IsHealthy => _failedRequestCount < 5;

        /// <summary>
        /// Gets the source identifier for this connector
        /// </summary>
        public override string ConnectorSource => DataSources.DexcomConnector;

        /// <summary>
        /// Gets the number of consecutive failed requests
        /// </summary>
        public int FailedRequestCount => _failedRequestCount;

        /// <summary>
        /// Resets the failed request counter
        /// </summary>
        public void ResetFailedRequestCount()
        {
            _failedRequestCount = 0;
            _logger.LogInformation("Dexcom connector failed request count reset");
        }

        public override string ServiceName => "Dexcom Share";

        public override List<SyncDataType> SupportedDataTypes => new() { SyncDataType.Glucose };

        public DexcomConnectorService(
            HttpClient httpClient,
            IOptions<DexcomConnectorConfiguration> config,
            ILogger<DexcomConnectorService> logger,
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

            _failedRequestCount = 0;
            return true;
        }

        public override async Task<IEnumerable<Entry>> FetchGlucoseDataAsync(DateTime? since = null)
        {
            // Use the base class helper for file I/O and data fetching
            var entries = await FetchWithOptionalFileIOAsync(
                _config,
                async (s) => await FetchBatchDataAsync(s),
                TransformBatchDataToEntries,
                _fileService,
                "dexcom_batch",
                since
            );

            _logger.LogInformation(
                "[{ConnectorSource}] Retrieved {Count} glucose entries from Dexcom",
                ConnectorSource,
                entries.Count()
            );

            return entries;
        }

        private IEnumerable<Entry> TransformBatchDataToEntries(DexcomEntry[] batchData)
        {
            if (batchData == null || batchData.Length == 0)
            {
                return Enumerable.Empty<Entry>();
            }

            return batchData
                .Where(entry => entry != null && entry.Value > 0)
                .Select(ConvertDexcomEntry)
                .OrderBy(entry => entry.Date)
                .ToList();
        }

        private async Task<DexcomEntry[]?> FetchBatchDataAsync(DateTime? since = null)
        {
            try
            {
                // Get valid session token from provider
                var sessionId = await _tokenProvider.GetValidTokenAsync();
                if (string.IsNullOrEmpty(sessionId))
                {
                    _logger.LogWarning(
                        "[{ConnectorSource}] Failed to get valid session, authentication failed",
                        ConnectorSource
                    );
                    _failedRequestCount++;
                    return null;
                }

                // Apply rate limiting
                await _rateLimitingStrategy.ApplyDelayAsync(0);

                var result = await FetchRawDataWithRetryAsync(sessionId, since);

                // Log batch data summary
                if (result != null)
                {
                    var validEntries = result.Where(e => e != null && e.Value > 0).ToArray();
                    var minDate = validEntries.Length > 0 ? validEntries.Min(e => e.WT) : "N/A";
                    var maxDate = validEntries.Length > 0 ? validEntries.Max(e => e.WT) : "N/A";

                    _logger.LogInformation(
                        "[{ConnectorSource}] Fetched Dexcom batch data: TotalEntries={TotalCount}, ValidEntries={ValidCount}, DateRange={MinDate} to {MaxDate}",
                        ConnectorSource,
                        result.Length,
                        validEntries.Length,
                        minDate,
                        maxDate
                    );
                }

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "[{ConnectorSource}] Error fetching glucose data from Dexcom Share",
                    ConnectorSource
                );
                _failedRequestCount++;
                return null;
            }
        }

        private async Task<DexcomEntry[]?> FetchRawDataWithRetryAsync(string sessionId, DateTime? since = null)
        {
            const int maxRetries = 3;
            HttpRequestException? lastException = null;

            for (int attempt = 0; attempt < maxRetries; attempt++)
            {
                try
                {
                    _logger.LogDebug(
                        "Fetching Dexcom glucose data (attempt {Attempt}/{MaxRetries})",
                        attempt + 1,
                        maxRetries
                    );

                    // Calculate time range
                    var twoDaysAgo = DateTime.UtcNow.AddDays(-2);
                    var startTime = since.HasValue
                        ? (since.Value > twoDaysAgo ? since.Value : twoDaysAgo)
                        : twoDaysAgo;

                    var timeDiff = DateTime.UtcNow - startTime;
                    var maxCount = Math.Ceiling(timeDiff.TotalMinutes / 5); // 5-minute intervals
                    var minutes = (int)(maxCount * 5);

                    var url =
                        $"/ShareWebServices/Services/Publisher/ReadPublisherLatestGlucoseValues?sessionID={sessionId}&minutes={minutes}&maxCount={(int)maxCount}";

                    var response = await _httpClient.PostAsync(
                        url,
                        new StringContent("{}", Encoding.UTF8, "application/json")
                    );

                    if (!response.IsSuccessStatusCode)
                    {
                        var errorContent = await response.Content.ReadAsStringAsync();

                        // Handle session expiration
                        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                        {
                            _logger.LogWarning(
                                "Dexcom session expired, invalidating token and retrying"
                            );
                            _tokenProvider.InvalidateToken();

                            // Get new token for retry
                            sessionId = await _tokenProvider.GetValidTokenAsync() ?? string.Empty;
                            if (string.IsNullOrEmpty(sessionId))
                            {
                                _logger.LogError("Failed to re-authenticate with Dexcom Share");
                                _failedRequestCount++;
                                return null;
                            }
                            continue; // Retry with new session
                        }

                        // Check for retryable errors
                        if (
                            response.StatusCode == System.Net.HttpStatusCode.TooManyRequests
                            || response.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable
                            || response.StatusCode == System.Net.HttpStatusCode.InternalServerError
                        )
                        {
                            lastException = new HttpRequestException(
                                $"HTTP {(int)response.StatusCode} {response.StatusCode}: {errorContent}"
                            );
                            _logger.LogWarning(
                                "Dexcom data fetch failed with retryable error on attempt {Attempt}: {StatusCode} - {Error}",
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
                                "Dexcom data fetch failed with non-retryable error: {StatusCode} - {Error}",
                                response.StatusCode,
                                errorContent
                            );
                            _failedRequestCount++;
                            return null;
                        }
                    }
                    else
                    {
                        var jsonContent = await response.Content.ReadAsStringAsync();
                        var dexcomEntries = JsonSerializer.Deserialize<DexcomEntry[]>(jsonContent);

                        // Reset failed request count on successful fetch
                        _failedRequestCount = 0;

                        return dexcomEntries ?? Array.Empty<DexcomEntry>();
                    }
                }
                catch (HttpRequestException ex)
                {
                    lastException = ex;
                    _logger.LogWarning(
                        ex,
                        "HTTP error during Dexcom data fetch attempt {Attempt}: {Message}",
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
                catch (JsonException ex)
                {
                    _logger.LogError(
                        ex,
                        "JSON parsing error during Dexcom data fetch attempt {Attempt}",
                        attempt + 1
                    );
                    _failedRequestCount++;
                    return null;
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        ex,
                        "Unexpected error during Dexcom data fetch attempt {Attempt}",
                        attempt + 1
                    );
                    _failedRequestCount++;
                    return null;
                }
            }

            // All attempts failed
            _failedRequestCount++;
            _logger.LogError("Dexcom data fetch failed after {MaxRetries} attempts", maxRetries);

            if (lastException != null)
            {
                throw lastException;
            }

            return null;
        }


        private Entry ConvertDexcomEntry(DexcomEntry dexcomEntry)
        {
            try
            {
                // Parse Dexcom's date format: /Date(1426292016000-0700)/
                var wallTimeMatch = System.Text.RegularExpressions.Regex.Match(
                    dexcomEntry.WT,
                    @"\((\d+)\)"
                );
                if (!wallTimeMatch.Success)
                {
                    _logger.LogWarning(
                        "Could not parse Dexcom timestamp: {Timestamp}",
                        dexcomEntry.WT
                    );
                    return new Entry { Type = "sgv", Device = "nightscout-connect-dexcom-share" };
                }

                var wallTimeMillis = long.Parse(wallTimeMatch.Groups[1].Value);
                var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(wallTimeMillis).DateTime;

                var direction = TrendDirections.GetValueOrDefault(
                    dexcomEntry.Trend,
                    Direction.NotComputable
                );
                return new Entry
                {
                    Date = timestamp,
                    Sgv = dexcomEntry.Value,
                    Direction = direction.ToString(),
                    Device = ConnectorSource,
                    Type = "sgv",
                };
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error converting Dexcom entry: {@Entry}", dexcomEntry);
                return new Entry { Type = "sgv", Device = "nightscout-connect-dexcom-share" };
            }
        }

        public class DexcomEntry
        {
            public string DT { get; set; } = string.Empty;
            public string ST { get; set; } = string.Empty;
            public int Trend { get; set; }
            public int Value { get; set; }
            public string WT { get; set; } = string.Empty;
        }
    }
}
