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

        /// <summary>
        /// Cached JWT token for v3 API authentication
        /// </summary>
        private string? _jwtToken;
        private DateTime _jwtTokenExpiry = DateTime.MinValue;

        /// <summary>
        /// Hash API secret using SHA1 to match Nightscout's expected format
        /// </summary>
        private static string HashApiSecret(string apiSecret)
        {
            using var sha1 = System.Security.Cryptography.SHA1.Create();
            var hashBytes = sha1.ComputeHash(System.Text.Encoding.UTF8.GetBytes(apiSecret));
            return Convert.ToHexString(hashBytes).ToLowerInvariant();
        }

        /// <summary>
        /// Gets a JWT token for v3 API authentication
        /// </summary>
        private async Task<string?> GetJwtTokenAsync()
        {
            // Return cached token if still valid
            if (!string.IsNullOrEmpty(_jwtToken) && DateTime.UtcNow < _jwtTokenExpiry)
            {
                return _jwtToken;
            }

            try
            {
                var apiSecret = _config.SourceApiSecret;
                if (string.IsNullOrEmpty(apiSecret))
                {
                    _logger.LogWarning("No API secret configured for JWT authentication");
                    return null;
                }

                // Request JWT token using api secret as token
                var tokenUrl = $"/api/v2/authorization/request/token={apiSecret}";
                _logger.LogDebug("Requesting JWT token from {Url}", tokenUrl);

                var response = await _httpClient.GetAsync(tokenUrl);
                if (!response.IsSuccessStatusCode)
                {
                    var errorContent = await response.Content.ReadAsStringAsync();
                    _logger.LogWarning(
                        "Failed to get JWT token: {StatusCode} - {Error}",
                        response.StatusCode,
                        errorContent
                    );
                    return null;
                }

                var content = await response.Content.ReadAsStringAsync();
                var authResponse = JsonSerializer.Deserialize<JsonElement>(content);

                if (authResponse.TryGetProperty("token", out var tokenElement))
                {
                    _jwtToken = tokenElement.GetString();
                    // JWT tokens typically expire in 1 hour, refresh at 50 minutes
                    _jwtTokenExpiry = DateTime.UtcNow.AddMinutes(50);
                    _logger.LogInformation("Successfully obtained JWT token for v3 API");
                    return _jwtToken;
                }

                _logger.LogWarning("JWT token response did not contain expected 'token' field");
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error obtaining JWT token for v3 API");
                return null;
            }
        }

        /// <summary>
        /// Adds JWT authentication header to request for v3 API calls
        /// </summary>
        private async Task<bool> AddJwtAuthHeaderAsync(HttpRequestMessage request)
        {
            var token = await GetJwtTokenAsync();
            if (string.IsNullOrEmpty(token))
            {
                return false;
            }

            request.Headers.Add("Authorization", $"Bearer {token}");
            return true;
        }

        /// <summary>
        /// Adds API secret header to request for v1 API calls that require authentication
        /// </summary>
        private void AddApiSecretHeader(HttpRequestMessage request)
        {
            var apiSecret = _config.SourceApiSecret;
            if (!string.IsNullOrEmpty(apiSecret))
            {
                // Nightscout v1 API expects SHA1 hashed secret in the api-secret header
                var hashedSecret = HashApiSecret(apiSecret);
                request.Headers.Add("api-secret", hashedSecret);
                _logger.LogDebug("Added api-secret header with hashed secret");
            }
            else
            {
                _logger.LogWarning("No API secret configured for authentication");
            }
        }

        /// <summary>
        /// Builds a URL with secret query parameter for v1 API authentication
        /// Some Nightscout endpoints require authentication via query parameter
        /// </summary>
        private string BuildAuthenticatedUrl(string baseUrl)
        {
            var apiSecret = _config.SourceApiSecret;
            if (string.IsNullOrEmpty(apiSecret))
            {
                _logger.LogWarning("No API secret configured for URL authentication");
                return baseUrl;
            }

            var hashedSecret = HashApiSecret(apiSecret);
            var separator = baseUrl.Contains('?') ? "&" : "?";
            return $"{baseUrl}{separator}secret={hashedSecret}";
        }

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
                var request = new HttpRequestMessage(HttpMethod.Get, "/api/v3/lastModified");
                if (!await AddJwtAuthHeaderAsync(request))
                {
                    _logger.LogWarning("Could not add JWT auth for lastModified request");
                    return null;
                }

                var response = await _httpClient.SendAsync(request);
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

                    var request = new HttpRequestMessage(HttpMethod.Get, urlBuilder.ToString());
                    if (!await AddJwtAuthHeaderAsync(request))
                    {
                        _logger.LogWarning(
                            "Could not add JWT auth for {Collection} fetch",
                            collection
                        );
                        break;
                    }

                    _logger.LogDebug(
                        "Fetching {Collection} from v3 API: {Url}",
                        collection,
                        urlBuilder.ToString()
                    );

                    var response = await _httpClient.SendAsync(request);
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
        /// Fetch entries using v3 API with pagination
        /// </summary>
        public async Task<IEnumerable<Entry>> FetchEntriesV3Async(DateTime? since = null)
        {
            return await FetchCollectionV3Async<Entry>("entries", since, 1000, "date", true);
        }

        /// <summary>
        /// Fetch treatments using v3 API with pagination
        /// </summary>
        public async Task<IEnumerable<Treatment>> FetchTreatmentsV3Async(DateTime? since = null)
        {
            return await FetchCollectionV3Async<Treatment>(
                "treatments",
                since,
                1000,
                "created_at",
                true
            );
        }

        /// <summary>
        /// Fetch device status using v3 API with pagination
        /// </summary>
        public async Task<IEnumerable<DeviceStatus>> FetchDeviceStatusV3Async(
            DateTime? since = null
        )
        {
            return await FetchCollectionV3Async<DeviceStatus>(
                "devicestatus",
                since,
                1000,
                "created_at",
                true
            );
        }

        /// <summary>
        /// Fetch profiles using v3 API
        /// </summary>
        public async Task<IEnumerable<Profile>> FetchProfilesV3Async(DateTime? since = null)
        {
            return await FetchCollectionV3Async<Profile>("profile", since, 100, "created_at", true);
        }

        /// <summary>
        /// Fetch food entries using v3 API
        /// </summary>
        public async Task<IEnumerable<Food>> FetchFoodV3Async(DateTime? since = null)
        {
            return await FetchCollectionV3Async<Food>("food", since, 1000, "created_at", true);
        }

        /// <summary>
        /// Sync all data from source Nightscout using v3 API
        /// More efficient than v1 API due to lastModified tracking and proper pagination
        /// </summary>
        public async Task<bool> SyncNightscoutDataV3Async(
            NightscoutConnectorConfiguration config,
            CancellationToken cancellationToken = default,
            DateTime? since = null
        )
        {
            try
            {
                _logger.LogInformation("Starting Nightscout data sync using v3 API");

                // Check if v3 API is available
                if (!await SupportsV3ApiAsync())
                {
                    _logger.LogWarning(
                        "Source Nightscout does not support v3 API, falling back to v1"
                    );
                    return await SyncNightscoutDataAsync(config, cancellationToken, since);
                }

                var allSuccess = true;
                var sinceTimestamp = since ?? DateTime.UtcNow.AddHours(-24);

                // Try to get lastModified to optimize sync
                var lastModified = await GetLastModifiedAsync();
                if (lastModified != null)
                {
                    _logger.LogInformation(
                        "Got lastModified timestamps: entries={Entries}, treatments={Treatments}, devicestatus={DeviceStatus}",
                        lastModified.GetValueOrDefault("entries"),
                        lastModified.GetValueOrDefault("treatments"),
                        lastModified.GetValueOrDefault("devicestatus")
                    );
                }

                // Sync entries
                try
                {
                    _stateService?.SetState(ConnectorState.Syncing, "Downloading entries...");
                    var entries = await FetchEntriesV3Async(sinceTimestamp);
                    var entriesArray = entries.ToArray();

                    if (entriesArray.Length > 0)
                    {
                        var success = await PublishGlucoseDataInBatchesAsync(
                            entriesArray,
                            config,
                            cancellationToken
                        );
                        if (success)
                        {
                            _logger.LogInformation(
                                "Successfully synced {Count} entries via v3 API",
                                entriesArray.Length
                            );
                        }
                        else
                        {
                            _logger.LogWarning("Failed to sync entries");
                            allSuccess = false;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error syncing entries via v3 API");
                    allSuccess = false;
                }

                // Sync treatments
                try
                {
                    _stateService?.SetState(ConnectorState.Syncing, "Downloading treatments...");
                    var treatments = await FetchTreatmentsV3Async(sinceTimestamp);
                    var treatmentsArray = treatments.ToArray();

                    if (treatmentsArray.Length > 0)
                    {
                        var success = await PublishTreatmentDataAsync(
                            treatmentsArray,
                            config,
                            cancellationToken
                        );
                        if (success)
                        {
                            _logger.LogInformation(
                                "Successfully synced {Count} treatments via v3 API",
                                treatmentsArray.Length
                            );
                        }
                        else
                        {
                            _logger.LogWarning("Failed to sync treatments");
                            allSuccess = false;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error syncing treatments via v3 API");
                    allSuccess = false;
                }

                // Sync device status
                try
                {
                    _stateService?.SetState(ConnectorState.Syncing, "Downloading device status...");
                    var deviceStatuses = await FetchDeviceStatusV3Async(sinceTimestamp);
                    var deviceStatusArray = deviceStatuses.ToArray();

                    if (deviceStatusArray.Length > 0)
                    {
                        var success = await PublishDeviceStatusAsync(
                            deviceStatusArray,
                            config,
                            cancellationToken
                        );
                        if (success)
                        {
                            _logger.LogInformation(
                                "Successfully synced {Count} device statuses via v3 API",
                                deviceStatusArray.Length
                            );
                        }
                        else
                        {
                            _logger.LogWarning("Failed to sync device status");
                            allSuccess = false;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error syncing device status via v3 API");
                    allSuccess = false;
                }

                // Sync profiles
                try
                {
                    _stateService?.SetState(ConnectorState.Syncing, "Downloading profiles...");
                    var profiles = await FetchProfilesV3Async(null);
                    var profilesArray = profiles.ToArray();

                    if (profilesArray.Length > 0)
                    {
                        var success = await PublishProfileDataAsync(
                            profilesArray,
                            config,
                            cancellationToken
                        );
                        if (success)
                        {
                            _logger.LogInformation(
                                "Successfully synced {Count} profiles via v3 API",
                                profilesArray.Length
                            );
                        }
                        else
                        {
                            _logger.LogWarning("Failed to sync profiles");
                            allSuccess = false;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error syncing profiles via v3 API");
                    allSuccess = false;
                }

                // Sync food
                try
                {
                    _stateService?.SetState(ConnectorState.Syncing, "Downloading food...");
                    var foods = await FetchFoodV3Async(null);
                    var foodsArray = foods.ToArray();

                    if (foodsArray.Length > 0)
                    {
                        var success = await PublishFoodDataAsync(
                            foodsArray,
                            config,
                            cancellationToken
                        );
                        if (success)
                        {
                            _logger.LogInformation(
                                "Successfully synced {Count} food entries via v3 API",
                                foodsArray.Length
                            );
                        }
                        else
                        {
                            _logger.LogWarning("Failed to sync food");
                            allSuccess = false;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error syncing food via v3 API");
                    allSuccess = false;
                }

                if (allSuccess)
                {
                    _logger.LogInformation("Nightscout v3 API data sync completed successfully");
                    _stateService?.SetState(ConnectorState.Idle, "Nightscout sync complete");
                }
                else
                {
                    _logger.LogWarning("Nightscout v3 API data sync completed with some failures");
                    _stateService?.SetState(ConnectorState.Error, "Nightscout sync completed with errors");
                }

                return allSuccess;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during Nightscout v3 API data sync");
                _stateService?.SetState(ConnectorState.Error, "Error during Nightscout sync");
                return false;
            }
        }

        #endregion

        public override async Task<IEnumerable<Entry>> FetchGlucoseDataAsync(DateTime? since = null)
        {
            // Use the base class helper for file I/O and data fetching
            var entries = await FetchWithOptionalFileIOAsync(
                _config,
                async (s) => await FetchBatchDataAsync(s),
                TransformBatchDataToEntries,
                _fileService,
                "nightscout_batch",
                since
            );

            _logger.LogInformation(
                "[{ConnectorSource}] Retrieved {Count} glucose entries from Nightscout",
                ConnectorSource,
                entries.Count()
            );

            return entries;
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

        private async Task<Entry[]?> FetchBatchDataAsync(DateTime? since = null)
        {
            try
            {
                // Apply rate limiting
                await _rateLimitingStrategy.ApplyDelayAsync(0);
                // Use a much higher count to get more data per request
                int hundredAndTwentyDays = 120 * 24 * 60 * 5;
                var result = await FetchRawDataWithRetryAsync(since, hundredAndTwentyDays);

                // Log batch data summary
                if (result != null && result.Length > 0)
                {
                    var validEntries = result
                        .Where(e => e != null && (e.Mgdl > 0 || e.Sgv > 0))
                        .ToArray();
                    var minDate =
                        validEntries.Length > 0 ? validEntries.Min(e => e.Date) : DateTime.MinValue;
                    var maxDate =
                        validEntries.Length > 0 ? validEntries.Max(e => e.Date) : DateTime.MinValue;

                    _logger.LogInformation(
                        "[{ConnectorSource}] Fetched Nightscout batch data: TotalEntries={TotalCount}, ValidEntries={ValidCount}, DateRange={MinDate:yyyy-MM-dd HH:mm} to {MaxDate:yyyy-MM-dd HH:mm}",
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
                    "[{ConnectorSource}] Error fetching glucose data from source Nightscout",
                    ConnectorSource
                );
                _failedRequestCount++;
                return null;
            }
        }

        private async Task<Entry[]?> FetchRawDataWithRetryAsync(
            DateTime? since = null,
            int count = 120 * 24 * 60 * 5 // Increase default limit to 100k to get more data per request
        )
        {
            const int maxRetries = 3;
            HttpRequestException? lastException = null;

            for (int attempt = 0; attempt < maxRetries; attempt++)
            {
                try
                {
                    _logger.LogDebug(
                        "Fetching Nightscout glucose data (attempt {Attempt}/{MaxRetries})",
                        attempt + 1,
                        maxRetries
                    );

                    var startTime = since ?? DateTime.UtcNow.AddHours(-24); // Default to last 24 hours

                    var url =
                        $"/api/v1/entries/sgv.json?find[date][$gte]={ToUnixTimestamp(startTime)}&count={count}";

                    var response = await _httpClient.GetAsync(url);

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
                                "Nightscout data fetch failed with retryable error on attempt {Attempt}: {StatusCode} - {Error}",
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
                                "Nightscout data fetch failed with non-retryable error: {StatusCode} - {Error}",
                                response.StatusCode,
                                errorContent
                            );
                            _failedRequestCount++;
                            throw new HttpRequestException(
                                $"HTTP error {response.StatusCode}: {errorContent}"
                            );
                        }
                    }
                    else
                    {
                        var jsonContent = await response.Content.ReadAsStringAsync();
                        var entries = JsonSerializer.Deserialize<Entry[]>(jsonContent);

                        // Reset failed request count on successful fetch
                        _failedRequestCount = 0;

                        return entries ?? Array.Empty<Entry>();
                    }
                }
                catch (HttpRequestException ex)
                {
                    lastException = ex;
                    _logger.LogWarning(
                        ex,
                        "HTTP error during Nightscout data fetch attempt {Attempt}: {Message}",
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
                        "JSON parsing error during Nightscout data fetch attempt {Attempt}",
                        attempt + 1
                    );
                    _failedRequestCount++;
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        ex,
                        "Unexpected error during Nightscout data fetch attempt {Attempt}",
                        attempt + 1
                    );
                    _failedRequestCount++;
                    throw;
                }
            }

            // All attempts failed
            _failedRequestCount++;
            _logger.LogError(
                "Nightscout data fetch failed after {MaxRetries} attempts",
                maxRetries
            );

            if (lastException != null)
            {
                throw lastException;
            }
            return null;
        }

        /// <summary>
        /// Fetch treatments from source Nightscout
        /// </summary>
        public async Task<IEnumerable<Treatment>> FetchTreatmentsAsync(DateTime? since = null)
        {
            try
            {
                var startTime = since ?? DateTime.UtcNow.AddHours(-24);
                int count = 120 * 24 * 60 * 5;

                var url =
                    $"/api/v1/treatments.json?find[created_at][$gte]={startTime:yyyy-MM-ddTHH:mm:ss.fffZ}&count={count}";

                _logger.LogDebug("Fetching Nightscout treatments: {Url}", url);

                var response = await _httpClient.GetAsync(url);

                if (!response.IsSuccessStatusCode)
                {
                    var errorContent = await response.Content.ReadAsStringAsync();
                    _logger.LogError(
                        "Failed to fetch Nightscout treatments: {StatusCode} - {Error}",
                        response.StatusCode,
                        errorContent
                    );
                    return Enumerable.Empty<Treatment>();
                }

                var jsonContent = await response.Content.ReadAsStringAsync();
                var treatments = JsonSerializer.Deserialize<Treatment[]>(jsonContent);

                if (treatments == null || treatments.Length == 0)
                {
                    _logger.LogDebug("No treatments returned from source Nightscout");
                    return Enumerable.Empty<Treatment>();
                }

                var filteredTreatments = treatments
                    .Where(treatment =>
                        treatment != null
                        && (
                            !since.HasValue
                            || (
                                treatment.CreatedAt != null
                                && DateTime.Parse(treatment.CreatedAt) > since.Value
                            )
                        )
                    )
                    .OrderBy(treatment => DateTime.Parse(treatment.CreatedAt!))
                    .ToList();

                _logger.LogInformation(
                    "Successfully fetched {Count} treatments from source Nightscout",
                    filteredTreatments.Count
                );
                return filteredTreatments;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching treatments from source Nightscout");
                return Enumerable.Empty<Treatment>();
            }
        }

        /// <summary>
        /// Fetch device status entries from source Nightscout
        /// </summary>
        public async Task<IEnumerable<DeviceStatus>> FetchDeviceStatusAsync(DateTime? since = null)
        {
            try
            {
                var startTime = since ?? DateTime.UtcNow.AddHours(-24);
                var count = 100;

                var url =
                    $"/api/v1/devicestatus.json?find[created_at][$gte]={startTime:yyyy-MM-ddTHH:mm:ss.fffZ}&count={count}";

                _logger.LogDebug("Fetching Nightscout device status: {Url}", url);

                var response = await _httpClient.GetAsync(url);

                if (!response.IsSuccessStatusCode)
                {
                    var errorContent = await response.Content.ReadAsStringAsync();
                    _logger.LogError(
                        "Failed to fetch Nightscout device status: {StatusCode} - {Error}",
                        response.StatusCode,
                        errorContent
                    );
                    return Enumerable.Empty<DeviceStatus>();
                }

                var jsonContent = await response.Content.ReadAsStringAsync();
                var deviceStatus = JsonSerializer.Deserialize<DeviceStatus[]>(jsonContent);

                if (deviceStatus == null || deviceStatus.Length == 0)
                {
                    _logger.LogDebug("No device status returned from source Nightscout");
                    return Enumerable.Empty<DeviceStatus>();
                }

                var filteredDeviceStatus = deviceStatus
                    .Where(status =>
                        status != null
                        && (
                            !since.HasValue
                            || (
                                status.CreatedAt != null
                                && DateTime.Parse(status.CreatedAt) > since.Value
                            )
                        )
                    )
                    .OrderBy(status => DateTime.Parse(status.CreatedAt!))
                    .ToList();

                _logger.LogInformation(
                    "Successfully fetched {Count} device status entries from source Nightscout",
                    filteredDeviceStatus.Count
                );
                return filteredDeviceStatus;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching device status from source Nightscout");
                return Enumerable.Empty<DeviceStatus>();
            }
        }

        /// <summary>
        /// Fetch profiles from source Nightscout
        /// </summary>
        public async Task<IEnumerable<Profile>> FetchProfilesAsync(DateTime? since = null)
        {
            try
            {
                var baseUrl = "/api/v1/profile.json";
                var url = BuildAuthenticatedUrl(baseUrl);

                _logger.LogDebug("Fetching Nightscout profiles: {Url}", baseUrl);

                // Profiles require authentication - use both header and query param
                var request = new HttpRequestMessage(HttpMethod.Get, url);
                AddApiSecretHeader(request);

                var response = await _httpClient.SendAsync(request);

                if (!response.IsSuccessStatusCode)
                {
                    var errorContent = await response.Content.ReadAsStringAsync();
                    _logger.LogError(
                        "Failed to fetch Nightscout profiles: {StatusCode} - {Error}",
                        response.StatusCode,
                        errorContent
                    );
                    return Enumerable.Empty<Profile>();
                }

                var jsonContent = await response.Content.ReadAsStringAsync();
                _logger.LogDebug(
                    "Profile JSON response: {Json}",
                    jsonContent.Length > 500 ? jsonContent.Substring(0, 500) + "..." : jsonContent
                );

                var profiles = JsonSerializer.Deserialize<Profile[]>(jsonContent);

                if (profiles == null || profiles.Length == 0)
                {
                    _logger.LogDebug("No profiles returned from source Nightscout");
                    return Enumerable.Empty<Profile>();
                }

                _logger.LogInformation(
                    "Successfully fetched {Count} profiles from source Nightscout",
                    profiles.Length
                );
                return profiles;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching profiles from source Nightscout");
                return Enumerable.Empty<Profile>();
            }
        }

        /// <summary>
        /// Fetch food entries from source Nightscout
        /// </summary>
        public async Task<IEnumerable<Food>> FetchFoodAsync(DateTime? since = null)
        {
            try
            {
                var count = 10000;
                var baseUrl = $"/api/v1/food.json?count={count}";
                var url = BuildAuthenticatedUrl(baseUrl);

                _logger.LogDebug("Fetching Nightscout food entries: {Url}", baseUrl);

                // Food requires authentication - use both header and query param
                var request = new HttpRequestMessage(HttpMethod.Get, url);
                AddApiSecretHeader(request);

                var response = await _httpClient.SendAsync(request);

                if (!response.IsSuccessStatusCode)
                {
                    var errorContent = await response.Content.ReadAsStringAsync();
                    _logger.LogError(
                        "Failed to fetch Nightscout food entries: {StatusCode} - {Error}",
                        response.StatusCode,
                        errorContent
                    );
                    return Enumerable.Empty<Food>();
                }

                var jsonContent = await response.Content.ReadAsStringAsync();
                var foods = JsonSerializer.Deserialize<Food[]>(jsonContent);

                if (foods == null || foods.Length == 0)
                {
                    _logger.LogDebug("No food entries returned from source Nightscout");
                    return Enumerable.Empty<Food>();
                }

                _logger.LogInformation(
                    "Successfully fetched {Count} food entries from source Nightscout",
                    foods.Length
                );
                return foods;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching food entries from source Nightscout");
                return Enumerable.Empty<Food>();
            }
        }

        /// <summary>
        /// Fetch activity entries from source Nightscout
        /// </summary>
        public async Task<IEnumerable<Activity>> FetchActivityAsync(DateTime? since = null)
        {
            try
            {
                var startTime = since ?? DateTime.UtcNow.AddDays(-30);
                var count = 10000;

                var baseUrl =
                    $"/api/v1/activity.json?find[created_at][$gte]={startTime:yyyy-MM-ddTHH:mm:ss.fffZ}&count={count}";
                var url = BuildAuthenticatedUrl(baseUrl);

                _logger.LogDebug("Fetching Nightscout activities: {Url}", baseUrl);

                // Activity requires authentication - use both header and query param
                var request = new HttpRequestMessage(HttpMethod.Get, url);
                AddApiSecretHeader(request);

                var response = await _httpClient.SendAsync(request);

                if (!response.IsSuccessStatusCode)
                {
                    var errorContent = await response.Content.ReadAsStringAsync();
                    _logger.LogError(
                        "Failed to fetch Nightscout activities: {StatusCode} - {Error}",
                        response.StatusCode,
                        errorContent
                    );
                    return Enumerable.Empty<Activity>();
                }

                var jsonContent = await response.Content.ReadAsStringAsync();
                var activities = JsonSerializer.Deserialize<Activity[]>(jsonContent);

                if (activities == null || activities.Length == 0)
                {
                    _logger.LogDebug("No activities returned from source Nightscout");
                    return Enumerable.Empty<Activity>();
                }

                _logger.LogInformation(
                    "Successfully fetched {Count} activities from source Nightscout",
                    activities.Length
                );
                return activities;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching activities from source Nightscout");
                return Enumerable.Empty<Activity>();
            }
        }

        /// <summary>
        /// Upload device status to target Nightscout
        /// </summary>
        public async Task<bool> UploadDeviceStatusToNightscoutAsync(
            IEnumerable<DeviceStatus> deviceStatusEntries,
            NightscoutConnectorConfiguration config
        )
        {
            try
            {
                var nightscoutUrl = config.NightscoutUrl.TrimEnd('/');
                var apiSecret = config.NightscoutApiSecret ?? config.ApiSecret;

                if (string.IsNullOrEmpty(apiSecret))
                {
                    throw new ArgumentException("API Secret is required for Nightscout upload");
                }

                var statusArray = deviceStatusEntries.ToList();

                if (statusArray.Count == 0)
                {
                    _logger.LogDebug("No device status entries to upload");
                    return true;
                }

                var json = JsonSerializer.Serialize(statusArray);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var request = new HttpRequestMessage(
                    HttpMethod.Post,
                    $"{nightscoutUrl}/api/v1/devicestatus"
                );
                request.Content = content;
                request.Headers.Add("API-SECRET", apiSecret);
                request.Headers.Add("User-Agent", "Nocturne-Connect/1.0");

                _logger.LogInformation(
                    "Uploading {Count} device status entries to {NightscoutUrl}",
                    statusArray.Count,
                    nightscoutUrl
                );

                var response = await _httpClient.SendAsync(request);

                if (response.IsSuccessStatusCode)
                {
                    _logger.LogInformation(
                        "Successfully uploaded {Count} device status entries",
                        statusArray.Count
                    );
                    return true;
                }
                else
                {
                    var errorContent = await response.Content.ReadAsStringAsync();
                    _logger.LogError(
                        "Failed to upload device status entries: {StatusCode} - {Error}",
                        response.StatusCode,
                        errorContent
                    );
                    return false;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error uploading device status to Nightscout");
                return false;
            }
        }

        /// <summary>
        /// Syncs Nightscout data using the API data submitter
        /// Fetches and submits entries, treatments, and device status
        /// </summary>
        /// <param name="config">Connector configuration</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>True if sync was successful</returns>
        public async Task<bool> SyncNightscoutDataAsync(
            NightscoutConnectorConfiguration config,
            CancellationToken cancellationToken = default,
            DateTime? since = null
        )
        {
            try
            {
                _logger.LogInformation("Starting Nightscout data sync using API data submitter");

                var allSuccess = true;
                var sinceTimestamp = since ?? DateTime.UtcNow.AddHours(-24);

                // Sync glucose entries
                try
                {
                    _stateService?.SetState(ConnectorState.Syncing, "Downloading entries...");
                    var entries = await FetchGlucoseDataAsync(sinceTimestamp);
                    var entriesArray = entries.ToArray();

                    if (entriesArray.Length > 0)
                    {
                        var entriesSuccess = await PublishGlucoseDataInBatchesAsync(
                            entriesArray,
                            config,
                            cancellationToken
                        );

                        if (entriesSuccess)
                        {
                            _logger.LogInformation(
                                "Successfully synced {Count} glucose entries",
                                entriesArray.Length
                            );
                        }
                        else
                        {
                            _logger.LogWarning("Failed to sync glucose entries");
                            allSuccess = false;
                        }
                    }
                    else
                    {
                        _logger.LogDebug("No glucose entries to sync");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error syncing glucose entries");
                    allSuccess = false;
                }

                // Sync treatments (insulin, carbs, temp basals, etc.)
                try
                {
                    _stateService?.SetState(ConnectorState.Syncing, "Downloading treatments...");
                    var treatments = await FetchTreatmentsAsync(sinceTimestamp);
                    var treatmentsArray = treatments.ToArray();

                    if (treatmentsArray.Length > 0)
                    {
                        var treatmentsSuccess = await PublishTreatmentDataAsync(
                            treatmentsArray,
                            config,
                            cancellationToken
                        );

                        if (treatmentsSuccess)
                        {
                            _logger.LogInformation(
                                "Successfully synced {Count} treatments",
                                treatmentsArray.Length
                            );
                        }
                        else
                        {
                            _logger.LogWarning("Failed to sync treatments");
                            allSuccess = false;
                        }
                    }
                    else
                    {
                        _logger.LogDebug("No treatments to sync");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error syncing treatments");
                    allSuccess = false;
                }

                // Sync device status (pump data, loop status, etc.)
                try
                {
                    _stateService?.SetState(ConnectorState.Syncing, "Downloading device status...");
                    var deviceStatuses = await FetchDeviceStatusAsync(sinceTimestamp);
                    var deviceStatusArray = deviceStatuses.ToArray();

                    if (deviceStatusArray.Length > 0)
                    {
                        var deviceStatusSuccess = await PublishDeviceStatusAsync(
                            deviceStatusArray,
                            config,
                            cancellationToken
                        );

                        if (deviceStatusSuccess)
                        {
                            _logger.LogInformation(
                                "Successfully synced {Count} device status entries",
                                deviceStatusArray.Length
                            );
                        }
                        else
                        {
                            _logger.LogWarning("Failed to sync device status");
                            allSuccess = false;
                        }
                    }
                    else
                    {
                        _logger.LogDebug("No device status entries to sync");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error syncing device status");
                    allSuccess = false;
                }

                // Sync profiles
                try
                {
                    _stateService?.SetState(ConnectorState.Syncing, "Downloading profiles...");
                    var profiles = await FetchProfilesAsync(null);
                    var profilesArray = profiles.ToArray();

                    if (profilesArray.Length > 0)
                    {
                        var profilesSuccess = await PublishProfileDataAsync(
                            profilesArray,
                            config,
                            cancellationToken
                        );

                        if (profilesSuccess)
                        {
                            _logger.LogInformation(
                                "Successfully synced {Count} profiles",
                                profilesArray.Length
                            );
                        }
                        else
                        {
                            _logger.LogWarning("Failed to sync profiles");
                            allSuccess = false;
                        }
                    }
                    else
                    {
                        _logger.LogDebug("No profiles to sync");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error syncing profiles");
                    allSuccess = false;
                }

                // Sync food entries
                try
                {
                    _stateService?.SetState(ConnectorState.Syncing, "Downloading food...");
                    var foods = await FetchFoodAsync(null);
                    var foodsArray = foods.ToArray();

                    if (foodsArray.Length > 0)
                    {
                        var foodsSuccess = await PublishFoodDataAsync(
                            foodsArray,
                            config,
                            cancellationToken
                        );

                        if (foodsSuccess)
                        {
                            _logger.LogInformation(
                                "Successfully synced {Count} food entries",
                                foodsArray.Length
                            );
                        }
                        else
                        {
                            _logger.LogWarning("Failed to sync food entries");
                            allSuccess = false;
                        }
                    }
                    else
                    {
                        _logger.LogDebug("No food entries to sync");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error syncing food entries");
                    allSuccess = false;
                }

                // Sync activity entries
                try
                {
                    _stateService?.SetState(ConnectorState.Syncing, "Downloading activity...");
                    var activities = await FetchActivityAsync(sinceTimestamp);
                    var activitiesArray = activities.ToArray();

                    if (activitiesArray.Length > 0)
                    {
                        var activitiesSuccess = await PublishActivityDataAsync(
                            activitiesArray,
                            config,
                            cancellationToken
                        );

                        if (activitiesSuccess)
                        {
                            _logger.LogInformation(
                                "Successfully synced {Count} activities",
                                activitiesArray.Length
                            );
                        }
                        else
                        {
                            _logger.LogWarning("Failed to sync activities");
                            allSuccess = false;
                        }
                    }
                    else
                    {
                        _logger.LogDebug("No activities to sync");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error syncing activities");
                    allSuccess = false;
                }

                if (allSuccess)
                {
                    _logger.LogInformation("Nightscout data sync completed successfully");
                    _stateService?.SetState(ConnectorState.Idle, "Nightscout sync complete");
                }
                else
                {
                    _logger.LogWarning("Nightscout data sync completed with some failures");
                    _stateService?.SetState(ConnectorState.Error, "Nightscout sync completed with errors");
                }

                return allSuccess;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during Nightscout data sync");
                return false;
            }
        }

        private static long ToUnixTimestamp(DateTime dateTime)
        {
            return ((DateTimeOffset)dateTime).ToUnixTimeMilliseconds();
        }
    }
}
