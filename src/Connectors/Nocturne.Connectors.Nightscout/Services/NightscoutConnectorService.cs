using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using Nocturne.Connectors.Configurations;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Services;
using Nocturne.Core.Models;
using Nocturne.Core.Constants;

namespace Nocturne.Connectors.Nightscout.Services
{
    /// <summary>
    /// Connector service for Nightscout-to-Nightscout data synchronization
    /// Fetches data from one Nightscout instance and uploads to another
    /// </summary>
    public class NightscoutConnectorService : BaseConnectorService<NightscoutConnectorConfiguration>
    {
        private readonly NightscoutConnectorConfiguration _config;
        private readonly IRetryDelayStrategy _retryDelayStrategy;
        private readonly IRateLimitingStrategy _rateLimitingStrategy;
        private readonly IConnectorFileService<Entry[]>? _fileService = null; // Optional file service for data persistence
        private int _failedRequestCount = 0;

        /// <summary>
        /// Gets the connector source identifier
        /// </summary>
        public override string ConnectorSource => DataSources.NightscoutConnector;

        /// <summary>
        /// Gets whether the connector is in a healthy state based on recent request failures
        /// </summary>
        public bool IsHealthy => _failedRequestCount < 5;

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
            _logger.LogInformation("Nightscout connector failed request count reset");
        }

        public override string ServiceName => "Nightscout";

        public override List<SyncDataType> SupportedDataTypes =>
            new()
            {
                SyncDataType.Glucose,
                SyncDataType.Treatments,
                SyncDataType.Profiles,
                SyncDataType.DeviceStatus,
                SyncDataType.Activity,
                SyncDataType.Food
            };

        public NightscoutConnectorService(
            HttpClient httpClient,
            IOptions<NightscoutConnectorConfiguration> config,
            ILogger<NightscoutConnectorService> logger,
            IRetryDelayStrategy retryDelayStrategy,
            IRateLimitingStrategy rateLimitingStrategy,
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
        }

        // Note: Authentication is now handled by NightscoutAuthHandler (DelegatingHandler)
        // JWT token management and API-secret header injection are centralized there

        /// <summary>
        /// Checks if the source Nightscout supports v3 API
        /// </summary>
        private async Task<bool> SupportsV3ApiAsync()
        {
            try
            {
                var response = await _httpClient.GetAsync("/api/v3/version");
                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        public override async Task<bool> AuthenticateAsync()
        {
            const int maxRetries = 3;
            HttpRequestException? lastException = null;

            for (int attempt = 0; attempt < maxRetries; attempt++)
            {
                try
                {
                    _logger.LogInformation(
                        "Testing connection to source Nightscout: {SourceEndpoint} (attempt {Attempt}/{MaxRetries})",
                        _config.SourceEndpoint,
                        attempt + 1,
                        maxRetries
                    );

                    // Test connection by fetching server status
                    var response = await _httpClient.GetAsync("/api/v1/status");

                    if (!response.IsSuccessStatusCode)
                    {
                        var errorContent = await response.Content.ReadAsStringAsync();

                        // Check for retryable errors
                        if (
                            response.StatusCode == System.Net.HttpStatusCode.TooManyRequests
                            || response.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable
                            || response.StatusCode == System.Net.HttpStatusCode.InternalServerError
                            || response.StatusCode == System.Net.HttpStatusCode.BadGateway
                            || response.StatusCode == System.Net.HttpStatusCode.GatewayTimeout
                        )
                        {
                            lastException = new HttpRequestException(
                                $"HTTP {(int)response.StatusCode} {response.StatusCode}: {errorContent}"
                            );
                            _logger.LogWarning(
                                "Nightscout connection test failed with retryable error on attempt {Attempt}: {StatusCode} - {Error}",
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
                            _logger.LogError(
                                "Failed to connect to source Nightscout with non-retryable error: {StatusCode} - {Error}",
                                response.StatusCode,
                                errorContent
                            );
                            _failedRequestCount++;
                            return false;
                        }
                    }
                    else
                    {
                        var content = await response.Content.ReadAsStringAsync();
                        var status = JsonSerializer.Deserialize<StatusResponse>(content);

                        if (status?.Status != "ok")
                        {
                            _logger.LogError(
                                "Source Nightscout status is not OK: {Status}",
                                status?.Status
                            );
                            _failedRequestCount++;
                            return false;
                        }

                        // Reset failed request count on successful connection
                        _failedRequestCount = 0;

                        _logger.LogInformation(
                            "Successfully connected to source Nightscout. Version: {Version}",
                            status.Version
                        );
                        return true;
                    }
                }
                catch (HttpRequestException ex)
                {
                    lastException = ex;
                    _logger.LogWarning(
                        ex,
                        "HTTP error during Nightscout connection test attempt {Attempt}: {Message}",
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
                        "Unexpected error during Nightscout connection test attempt {Attempt}",
                        attempt + 1
                    );
                    _failedRequestCount++;
                    return false;
                }
            }

            // All attempts failed
            _failedRequestCount++;
            _logger.LogError(
                "Nightscout connection test failed after {MaxRetries} attempts",
                maxRetries
            );

            if (lastException != null)
            {
                throw lastException;
            }

            return false;
        }

        #region V3 API Methods

        /// <summary>
        /// Gets the last modified timestamps for all collections using v3 API
        /// This is useful for efficient incremental syncing
        /// </summary>
        public async Task<Dictionary<string, long>?> GetLastModifiedAsync()
        {
            try
            {
                // Auth headers are automatically added by NightscoutAuthHandler
                var response = await _httpClient.GetAsync("/api/v3/lastModified");
                if (!response.IsSuccessStatusCode)
                {
                    var errorContent = await response.Content.ReadAsStringAsync();
                    _logger.LogWarning(
                        "Failed to get lastModified from v3 API: {StatusCode} - {Error}",
                        response.StatusCode,
                        errorContent
                    );
                    return null;
                }

                var content = await response.Content.ReadAsStringAsync();
                var json = JsonSerializer.Deserialize<JsonElement>(content);

                if (
                    json.TryGetProperty("result", out var result)
                    && result.TryGetProperty("collections", out var collections)
                )
                {
                    var lastModified = new Dictionary<string, long>();
                    foreach (var prop in collections.EnumerateObject())
                    {
                        if (prop.Value.TryGetInt64(out var timestamp))
                        {
                            lastModified[prop.Name] = timestamp;
                        }
                    }
                    return lastModified;
                }

                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting lastModified from v3 API");
                return null;
            }
        }

        /// <summary>
        /// Generic v3 API fetch method for any collection with pagination support
        /// </summary>
        private async Task<T[]> FetchCollectionV3Async<T>(
            string collection,
            DateTime? since = null,
            int limit = 1000,
            string? sortField = null,
            bool descending = true
        )
        {
            var allItems = new List<T>();
            var skip = 0;
            var hasMore = true;

            while (hasMore)
            {
                try
                {
                    var urlBuilder = new StringBuilder(
                        $"/api/v3/{collection}?limit={limit}&skip={skip}"
                    );

                    if (!string.IsNullOrEmpty(sortField))
                    {
                        urlBuilder.Append(
                            descending ? $"&sort$desc={sortField}" : $"&sort={sortField}"
                        );
                    }

                    if (since.HasValue)
                    {
                        var sinceMs = ((DateTimeOffset)since.Value).ToUnixTimeMilliseconds();
                        // Use srvModified for efficient delta sync
                        urlBuilder.Append($"&srvModified$gte={sinceMs}");
                    }

                    _logger.LogDebug(
                        "Fetching {Collection} from v3 API: {Url}",
                        collection,
                        urlBuilder.ToString()
                    );

                    // Auth headers are automatically added by NightscoutAuthHandler
                    var response = await _httpClient.GetAsync(urlBuilder.ToString());

                    if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                    {
                        // Handler already tried to refresh JWT; fall back to v1 API
                        _logger.LogWarning(
                            "v3 API returned 401 for {Collection} after auth retry, falling back to v1 API",
                            collection
                        );
                        return await FetchCollectionV1Async<T>(
                            collection,
                            since,
                            limit,
                            sortField ?? "date"
                        );
                    }

                    if (!response.IsSuccessStatusCode)
                    {
                        var errorContent = await response.Content.ReadAsStringAsync();
                        _logger.LogError(
                            "Failed to fetch {Collection} from v3 API: {StatusCode} - {Error}",
                            collection,
                            response.StatusCode,
                            errorContent
                        );
                        break;
                    }

                    var content = await response.Content.ReadAsStringAsync();
                    var json = JsonSerializer.Deserialize<JsonElement>(content);

                    T[]? items = null;
                    if (json.TryGetProperty("result", out var result))
                    {
                        items = JsonSerializer.Deserialize<T[]>(result.GetRawText());
                    }

                    if (items == null || items.Length == 0)
                    {
                        hasMore = false;
                    }
                    else
                    {
                        allItems.AddRange(items);
                        skip += items.Length;

                        // If we got fewer items than the limit, we've reached the end
                        hasMore = items.Length >= limit;

                        _logger.LogDebug(
                            "Fetched {Count} {Collection} items (total: {Total})",
                            items.Length,
                            collection,
                            allItems.Count
                        );
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error fetching {Collection} from v3 API", collection);
                    break;
                }
            }

            _logger.LogInformation(
                "Successfully fetched {Count} {Collection} items from v3 API",
                allItems.Count,
                collection
            );

            return allItems.ToArray();
        }

        /// <summary>
        /// Fallback v1 API fetch method for collections when v3/JWT is unavailable
        /// </summary>
        private async Task<T[]> FetchCollectionV1Async<T>(
            string collection,
            DateTime? since = null,
            int limit = 1000,
            string dateField = "date"
        )
        {
            try
            {
                // V1 endpoints usually follow /api/v1/{collection}.json
                var endpoint = collection == "entries" ? "entries.json" : collection;
                var urlBuilder = new StringBuilder($"/api/v1/{endpoint}?count={limit}");

                if (since.HasValue)
                {
                    var sinceMs = ((DateTimeOffset)since.Value).ToUnixTimeMilliseconds();
                    urlBuilder.Append($"&find[{dateField}][$gte]={sinceMs}");
                }

                _logger.LogDebug("Fetching from v1 API: {Url}", urlBuilder.ToString());

                // Auth headers are automatically added by NightscoutAuthHandler
                var response = await _httpClient.GetAsync(urlBuilder.ToString());
                if (!response.IsSuccessStatusCode)
                {
                    var errorContent = await response.Content.ReadAsStringAsync();
                    _logger.LogWarning(
                        "V1 fallback fetch failed for {Collection}: {StatusCode} - {Error}",
                        collection,
                        response.StatusCode,
                        errorContent
                    );
                    return Array.Empty<T>();
                }

                var content = await response.Content.ReadAsStringAsync();
                return JsonSerializer.Deserialize<T[]>(content) ?? Array.Empty<T>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in v1 fallback fetch for {Collection}", collection);
                return Array.Empty<T>();
            }
        }


        #endregion

        public override async Task<IEnumerable<Entry>> FetchGlucoseDataAsync(DateTime? since = null)
        {
            return await FetchCollectionV3Async<Entry>("entries", since, 1000, "date", true);
        }

        protected override async Task<IEnumerable<Entry>> FetchGlucoseDataRangeAsync(
            DateTime? from,
            DateTime? to
        )
        {
            return await FetchGlucoseDataAsync(from);
        }

        protected override async Task<IEnumerable<Treatment>> FetchTreatmentsAsync(
            DateTime? from,
            DateTime? to
        )
        {
            return await FetchCollectionV3Async<Treatment>(
                "treatments",
                from,
                1000,
                "created_at",
                true
            );
        }

        protected override async Task<IEnumerable<DeviceStatus>> FetchDeviceStatusAsync(
            DateTime? from,
            DateTime? to
        )
        {
            return await FetchCollectionV3Async<DeviceStatus>(
                "devicestatus",
                from,
                1000,
                "created_at",
                true
            );
        }

        protected override async Task<IEnumerable<Profile>> FetchProfilesAsync(
            DateTime? from,
            DateTime? to
        )
        {
            return await FetchCollectionV3Async<Profile>("profiles", from, 100, "startDate", true);
        }

        protected override async Task<IEnumerable<Activity>> FetchActivitiesAsync(
            DateTime? from,
            DateTime? to
        )
        {
            return await FetchCollectionV3Async<Activity>("activity", from, 1000, "created_at", true);
        }

        protected override async Task<IEnumerable<Food>> FetchFoodsAsync(DateTime? from, DateTime? to)
        {
            return await FetchCollectionV3Async<Food>("food", from, 1000, "created_at", true);
        }

        private IEnumerable<Entry> TransformBatchDataToEntries(Entry[] batchData)
        {
            if (batchData == null || batchData.Length == 0)
            {
                return Enumerable.Empty<Entry>();
            }

            return batchData
                .Where(entry => entry != null && (entry.Mgdl > 0 || entry.Sgv > 0))
                .OrderByDescending(entry => entry.Date)
                .ToList();
        }

    }
}
