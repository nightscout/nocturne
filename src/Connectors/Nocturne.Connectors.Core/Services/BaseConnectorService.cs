using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Core.Models;

#nullable enable

namespace Nocturne.Connectors.Core.Services
{
    /// <summary>
    /// Base implementation for connector services with common Nightscout upload functionality
    /// </summary>
    /// <typeparam name="TConfig">The connector-specific configuration type</typeparam>
    public abstract class BaseConnectorService<TConfig> : IConnectorService<TConfig>
        where TConfig : IConnectorConfiguration
    {
        protected readonly HttpClient _httpClient;
        protected readonly IApiDataSubmitter? _apiDataSubmitter;
        protected readonly ILogger _logger;
        protected readonly IConnectorMetricsTracker? _metricsTracker;
        private const int MaxRetries = 3;
        private static readonly TimeSpan[] RetryDelays =
        {
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(15),
            TimeSpan.FromSeconds(30),
        };

        /// <summary>
        /// Unique identifier for this connector service type
        /// </summary>
        public abstract string ConnectorSource { get; }

        protected readonly IConnectorStateService? _stateService;

        /// <summary>
        /// Base constructor for connector services using IHttpClientFactory pattern
        /// </summary>
        /// <param name="httpClient">HttpClient instance from IHttpClientFactory (will not be disposed)</param>
        /// <param name="logger">Logger instance for this connector</param>
        /// <param name="apiDataSubmitter">Optional API data submitter for Nocturne mode</param>
        /// <param name="metricsTracker">Optional metrics tracker</param>
        /// <param name="stateService">Optional state service for tracking connector state</param>
        protected BaseConnectorService(
            HttpClient httpClient,
            ILogger logger,
            IApiDataSubmitter? apiDataSubmitter = null,
            IConnectorMetricsTracker? metricsTracker = null,
            IConnectorStateService? stateService = null
        )
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _apiDataSubmitter = apiDataSubmitter;
            _metricsTracker = metricsTracker;
            _stateService = stateService;
        }

        /// <summary>
        /// Get the timestamp of the most recent treatment from the Nocturne API
        /// This enables "catch up" functionality to fetch only new data since the last upload
        /// </summary>
        /// <remarks>
        /// TODO: Implement querying Nocturne API for most recent treatment timestamp.
        /// For now, returns null to use default lookback period.
        /// </remarks>
        protected virtual async Task<DateTime?> FetchLatestTreatmentTimestampAsync(TConfig config)
        {
            return await Task.FromResult<DateTime?>(null);
        }

        /// <summary>
        /// Calculate the optimal "since" timestamp for data fetching
        /// Uses catch-up logic to fetch from the most recent treatment, or falls back to default lookback
        /// </summary>
        protected virtual async Task<DateTime> CalculateSinceTimestampAsync(
            TConfig config,
            DateTime? defaultSince = null
        )
        {
            if (defaultSince.HasValue)
            {
                return defaultSince.Value;
            }

            // First try to get the most recent treatment timestamp from target Nightscout
            var mostRecentTimestamp = await FetchLatestTreatmentTimestampAsync(config);

            if (mostRecentTimestamp.HasValue)
            {
                // Add a small overlap to ensure we don't miss any entries
                var sinceWithOverlap = mostRecentTimestamp.Value.AddMinutes(-5);

                // Maximum 7 days for safety
                var maxLookback = DateTime.UtcNow.AddDays(-7);
                if (sinceWithOverlap < maxLookback)
                {
                    return maxLookback;
                }

                return sinceWithOverlap;
            }

            // Fallback to provided default or 24 hours
            var fallbackSince = defaultSince ?? DateTime.UtcNow.AddHours(-24);
            Console.WriteLine(
                $"Using fallback mode: fetching data since {fallbackSince:yyyy-MM-dd HH:mm:ss} UTC"
            );
            return fallbackSince;
        }

        /// <summary>
        /// Hash API secret using SHA1 to match Nightscout's expected format
        /// </summary>
        private static string HashApiSecret(string apiSecret)
        {
            using var sha1 = SHA1.Create();
            var hashBytes = sha1.ComputeHash(Encoding.UTF8.GetBytes(apiSecret));
            return Convert.ToHexString(hashBytes).ToLowerInvariant();
        }

        public abstract string ServiceName { get; }

        public abstract Task<bool> AuthenticateAsync();
        public abstract Task<IEnumerable<Entry>> FetchGlucoseDataAsync(DateTime? since = null);

        /// <summary>
        /// Helper method to track metrics for any data type
        /// </summary>
        protected void TrackMetrics<T>(IEnumerable<T> items)
        {
            if (_metricsTracker == null) return;

            // To avoid multiple enumerations if not a collection
            var itemList = items as ICollection<T> ?? items.ToList();
            var count = itemList.Count;

            if (count == 0) return;

            DateTime? latestTime = null;

            // Try to find the latest timestamp
            if (typeof(T) == typeof(Entry))
            {
                // Entries are typically sorted by date, check last
                var last = itemList.LastOrDefault() as Entry;
                latestTime = last?.Date;
            }
            else
            {
                // Generic timestamp lookup
                var type = typeof(T);
                var properties = type.GetProperties();

                // Look for common timestamp properties
                var timeProp = properties.FirstOrDefault(p =>
                    p.Name.Equals("CreatedAt", StringComparison.OrdinalIgnoreCase) ||
                    p.Name.Equals("EventTime", StringComparison.OrdinalIgnoreCase) ||
                    p.Name.Equals("Timestamp", StringComparison.OrdinalIgnoreCase) ||
                    p.Name.Equals("StartDate", StringComparison.OrdinalIgnoreCase) || // For Profile
                    p.Name.Equals("Date", StringComparison.OrdinalIgnoreCase));

                if (timeProp != null)
                {
                    // Scan for latest time
                    foreach (var item in itemList)
                    {
                        var val = timeProp.GetValue(item);
                        DateTime? dt = null;

                        if (val is DateTime d)
                            dt = d;
                        else if (val is string s && DateTime.TryParse(s, out var parsed))
                            dt = parsed.ToUniversalTime();
                        else if (val is long l)
                             dt = DateTimeOffset.FromUnixTimeMilliseconds(l).UtcDateTime;

                        if (dt.HasValue)
                        {
                            if (!latestTime.HasValue || dt.Value > latestTime.Value)
                                latestTime = dt;
                        }
                    }
                }
            }

            _metricsTracker.TrackEntries(count, latestTime);
        }

        /// <summary>
        /// Submits glucose data directly to the API via HTTP
        /// </summary>
        protected virtual async Task<bool> PublishGlucoseDataAsync(
            IEnumerable<Entry> entries,
            TConfig config,
            CancellationToken cancellationToken = default
        )
        {
            if (_apiDataSubmitter == null)
            {
                _logger?.LogWarning("API data submitter not available for glucose data submission");
                return false;
            }

            var entriesArray = entries.ToArray();
            if (entriesArray.Length == 0)
            {
                _logger?.LogInformation("No glucose entries to submit");
                return true;
            }

            try
            {
                var success = await _apiDataSubmitter.SubmitEntriesAsync(
                    entriesArray,
                    ConnectorSource,
                    cancellationToken
                );

                if (success)
                {
                    _logger?.LogInformation(
                        "Successfully submitted {Count} glucose entries",
                        entriesArray.Length
                    );
                    TrackMetrics(entriesArray);
                }

                return success;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to submit glucose data");
                return false;
            }
        }

        /// <summary>
        /// Submits treatment data directly to the API via HTTP
        /// </summary>
        protected virtual async Task<bool> PublishTreatmentDataAsync(
            IEnumerable<Treatment> treatments,
            TConfig config,
            CancellationToken cancellationToken = default
        )
        {
            if (_apiDataSubmitter == null)
            {
                _logger?.LogWarning(
                    "API data submitter not available for treatment data submission"
                );
                return false;
            }

            var treatmentsArray = treatments.ToArray();
            if (treatmentsArray.Length == 0)
            {
                _logger?.LogInformation("No treatments to submit");
                return true;
            }

            try
            {
                var success = await _apiDataSubmitter.SubmitTreatmentsAsync(
                    treatmentsArray,
                    ConnectorSource,
                    cancellationToken
                );

                if (success)
                {
                    _logger?.LogInformation(
                        "Successfully submitted {Count} treatments",
                        treatmentsArray.Length
                    );
                    TrackMetrics(treatmentsArray);
                }

                return success;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to submit treatment data");
                return false;
            }
        }

        /// <summary>
        /// Submits device status data directly to the API via HTTP
        /// </summary>
        protected virtual async Task<bool> PublishDeviceStatusAsync(
            IEnumerable<DeviceStatus> deviceStatuses,
            TConfig config,
            CancellationToken cancellationToken = default
        )
        {
            if (_apiDataSubmitter == null)
            {
                _logger?.LogWarning(
                    "API data submitter not available for device status submission"
                );
                return false;
            }

            var statusArray = deviceStatuses.ToArray();
            if (statusArray.Length == 0)
            {
                _logger?.LogInformation("No device statuses to submit");
                return true;
            }

            try
            {
                var success = await _apiDataSubmitter.SubmitDeviceStatusAsync(
                    statusArray,
                    ConnectorSource,
                    cancellationToken
                );

                if (success)
                {
                    _logger?.LogInformation(
                        "Successfully submitted {Count} device statuses",
                        statusArray.Length
                    );
                    TrackMetrics(statusArray);
                }

                return success;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to submit device status data");
                return false;
            }
        }

        /// <summary>
        /// Submits profile data directly to the API via HTTP
        /// </summary>
        protected virtual async Task<bool> PublishProfileDataAsync(
            IEnumerable<Profile> profiles,
            TConfig config,
            CancellationToken cancellationToken = default
        )
        {
            if (_apiDataSubmitter == null)
            {
                _logger?.LogWarning("API data submitter not available for profile data submission");
                return false;
            }

            var profilesArray = profiles.ToArray();
            if (profilesArray.Length == 0)
            {
                _logger?.LogInformation("No profiles to submit");
                return true;
            }

            try
            {
                var success = await _apiDataSubmitter.SubmitProfilesAsync(
                    profilesArray,
                    ConnectorSource,
                    cancellationToken
                );

                if (success)
                {
                    _logger?.LogInformation(
                        "Successfully submitted {Count} profiles",
                        profilesArray.Length
                    );
                    TrackMetrics(profilesArray);
                }

                return success;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to submit profile data");
                return false;
            }
        }

        /// <summary>
        /// Submits food data directly to the API via HTTP
        /// </summary>
        protected virtual async Task<bool> PublishFoodDataAsync(
            IEnumerable<Food> foods,
            TConfig config,
            CancellationToken cancellationToken = default
        )
        {
            if (_apiDataSubmitter == null)
            {
                _logger?.LogWarning("API data submitter not available for food data submission");
                return false;
            }

            var foodsArray = foods.ToArray();
            if (foodsArray.Length == 0)
            {
                _logger?.LogInformation("No food entries to submit");
                return true;
            }

            try
            {
                var success = await _apiDataSubmitter.SubmitFoodAsync(
                    foodsArray,
                    ConnectorSource,
                    cancellationToken
                );

                if (success)
                {
                    _logger?.LogInformation(
                        "Successfully submitted {Count} food entries",
                        foodsArray.Length
                    );
                    TrackMetrics(foodsArray);
                }

                return success;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to submit food data");
                return false;
            }
        }

        /// <summary>
        /// Submits activity data directly to the API via HTTP
        /// </summary>
        protected virtual async Task<bool> PublishActivityDataAsync(
            IEnumerable<Activity> activities,
            TConfig config,
            CancellationToken cancellationToken = default
        )
        {
            if (_apiDataSubmitter == null)
            {
                _logger?.LogWarning(
                    "API data submitter not available for activity data submission"
                );
                return false;
            }

            var activitiesArray = activities.ToArray();
            if (activitiesArray.Length == 0)
            {
                _logger?.LogInformation("No activities to submit");
                return true;
            }

            try
            {
                var success = await _apiDataSubmitter.SubmitActivityAsync(
                    activitiesArray,
                    ConnectorSource,
                    cancellationToken
                );

                if (success)
                {
                    _logger?.LogInformation(
                        "Successfully submitted {Count} activities",
                        activitiesArray.Length
                    );
                    TrackMetrics(activitiesArray);
                }

                return success;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to submit activity data");
                return false;
            }
        }

        /// <summary>
        /// Submits health data (comprehensive data from sources like Glooko)
        /// Currently only submits blood glucose readings as entries
        /// </summary>
        protected virtual async Task<bool> PublishHealthDataAsync(
            Entry[] bloodGlucoseReadings,
            object[] bloodPressureReadings,
            object[] weightReadings,
            object[] sleepReadings,
            TConfig config,
            CancellationToken cancellationToken = default
        )
        {
            if (_apiDataSubmitter == null)
            {
                _logger?.LogWarning("API data submitter not available for health data submission");
                return false;
            }

            // Submit blood glucose readings as entries
            if (bloodGlucoseReadings != null && bloodGlucoseReadings.Length > 0)
            {
                try
                {
                    var success = await _apiDataSubmitter.SubmitEntriesAsync(
                        bloodGlucoseReadings,
                        ConnectorSource,
                        cancellationToken
                    );

                    if (success)
                    {
                        _logger?.LogInformation(
                            "Successfully submitted {Count} blood glucose readings from health data",
                            bloodGlucoseReadings.Length
                        );
                        TrackMetrics(bloodGlucoseReadings);
                    }

                    return success;
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Failed to submit health data");
                    return false;
                }
            }

            _logger?.LogInformation("No blood glucose readings in health data to submit");
            return true;
        }

        /// <summary>
        /// Publishes messages in batches to optimize throughput
        /// </summary>
        protected virtual async Task<bool> PublishGlucoseDataInBatchesAsync(
            IEnumerable<Entry> entries,
            TConfig config,
            CancellationToken cancellationToken = default
        )
        {
            var entriesArray = entries.ToArray();
            if (entriesArray.Length == 0)
            {
                return true;
            }

            var batchSize = Math.Max(1, config.BatchSize);
            var batches = entriesArray
                .Select((entry, index) => new { entry, index })
                .GroupBy(x => x.index / batchSize)
                .Select(g => g.Select(x => x.entry).ToArray());

            bool allSuccessful = true;
            int batchNumber = 1;

            foreach (var batch in batches)
            {
                _logger?.LogDebug(
                    "Publishing batch {BatchNumber} with {Count} entries",
                    batchNumber,
                    batch.Length
                );

                var success = await PublishGlucoseDataAsync(batch, config, cancellationToken);
                if (!success)
                {
                    allSuccessful = false;
                    _logger?.LogWarning("Failed to publish batch {BatchNumber}", batchNumber);
                }

                batchNumber++;

                // Small delay between batches to avoid overwhelming the message bus
                if (batchNumber > 1)
                {
                    await Task.Delay(100, cancellationToken);
                }
            }

            return allSuccessful;
        }

        /// <summary>
        /// Publishes treatment messages in batches to optimize throughput
        /// </summary>
        protected virtual async Task<bool> PublishTreatmentDataInBatchesAsync(
            IEnumerable<Treatment> treatments,
            TConfig config,
            CancellationToken cancellationToken = default
        )
        {
            var treatmentsArray = treatments.ToArray();
            if (treatmentsArray.Length == 0)
            {
                return true;
            }

            var batchSize = Math.Max(1, config.BatchSize);
            var batches = treatmentsArray
                .Select((treatment, index) => new { treatment, index })
                .GroupBy(x => x.index / batchSize)
                .Select(g => g.Select(x => x.treatment).ToArray());

            bool allSuccessful = true;
            int batchNumber = 1;

            foreach (var batch in batches)
            {
                _logger?.LogDebug(
                    "Publishing treatment batch {BatchNumber} with {Count} entries",
                    batchNumber,
                    batch.Length
                );

                var success = await PublishTreatmentDataAsync(batch, config, cancellationToken);
                if (!success)
                {
                    allSuccessful = false;
                    _logger?.LogWarning("Failed to publish treatment batch {BatchNumber}", batchNumber);
                }

                batchNumber++;

                // Small delay between batches to avoid overwhelming the message bus
                if (batchNumber > 1)
                {
                    await Task.Delay(100, cancellationToken);
                }
            }

            return allSuccessful;
        }

        /// <summary>
        /// Main sync method that handles data synchronization based on connector mode
        /// </summary>
        public virtual async Task<bool> SyncDataAsync(
            TConfig config,
            CancellationToken cancellationToken = default,
            DateTime? since = null
        )
        {
            _logger.LogInformation("Starting data sync for {ConnectorSource}", ConnectorSource);
            _stateService?.SetState(ConnectorState.Syncing, "Syncing data...");

            try
            {
                // In Nocturne mode, only use message bus (no fallback to direct API)
                if (config.Mode == ConnectorMode.Nocturne)
                {
                    if (_apiDataSubmitter == null)
                    {
                        _logger?.LogError(
                            "API data submitter is required for Nocturne mode but is not available"
                        );
                        _stateService?.SetState(ConnectorState.Error, "Configuration error: API submitter missing");
                        return false;
                    }

                    try
                    {
                        _logger?.LogInformation(
                            "Using API data submitter for data synchronization from {ConnectorSource} in Nocturne mode",
                            ConnectorSource
                        );

                        // Fetch glucose data (use default since for Nocturne mode as no Nightscout lookup available)
                        var sinceTimestamp = since ?? DateTime.UtcNow.AddHours(-24);
                        var entries = await FetchGlucoseDataAsync(sinceTimestamp);

                        // Submit via API with batching
                        var success = await PublishGlucoseDataInBatchesAsync(
                            entries,
                            config,
                            cancellationToken
                        );

                        if (success)
                        {
                            _logger?.LogInformation(
                                "Successfully submitted data via API in Nocturne mode"
                            );
                            _stateService?.SetState(ConnectorState.Idle, "Sync completed successfully");
                            return true;
                        }
                        else
                        {
                            _logger?.LogError("API data submission failed in Nocturne mode");
                            _stateService?.SetState(ConnectorState.Error, "Data submission failed");
                            return false;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogError(ex, "API data submission failed in Nocturne mode");
                        _stateService?.SetState(ConnectorState.Error, $"Error: {ex.Message}");
                        return false;
                    }
                }

                // Standalone mode - prefer API submitter when available, fallback to direct API
                if (_apiDataSubmitter != null)
                {
                    try
                    {
                        _logger?.LogInformation(
                            "Using API data submitter for data synchronization from {ConnectorSource} in Standalone mode",
                            ConnectorSource
                        );

                        // Fetch glucose data
                        var sinceTimestamp = await CalculateSinceTimestampAsync(config, since);
                        var entries = await FetchGlucoseDataAsync(sinceTimestamp);

                        // Publish via message bus with batching
                        var success = await PublishGlucoseDataInBatchesAsync(
                            entries,
                            config,
                            cancellationToken
                        );

                        if (success)
                        {
                            _logger?.LogInformation("Successfully published data via message bus");
                            _stateService?.SetState(ConnectorState.Idle, "Sync completed successfully");
                            return true;
                        }
                        else if (config.FallbackToDirectApi)
                        {
                            _logger?.LogWarning(
                                "Message publishing failed, falling back to direct API upload"
                            );
                            var result = await UploadToNightscoutAsync(entries, config);
                            if (result) _stateService?.SetState(ConnectorState.Idle, "Sync completed (fallback used)");
                            else _stateService?.SetState(ConnectorState.Error, "Sync failed (fallback also failed)");
                            return result;
                        }
                        else
                        {
                            _logger?.LogError("Message publishing failed and fallback disabled");
                            _stateService?.SetState(ConnectorState.Error, "Message publishing failed");
                            return false;
                        }
                    }
                    catch (Exception ex) when (config.FallbackToDirectApi)
                    {
                        _logger?.LogWarning(
                            ex,
                            "Message bus processing failed, falling back to direct API"
                        );
                        var sinceTimestamp = await CalculateSinceTimestampAsync(config, since);
                        var entries = await FetchGlucoseDataAsync(sinceTimestamp);
                        var result = await UploadToNightscoutAsync(entries, config);
                        if (result) _stateService?.SetState(ConnectorState.Idle, "Sync completed (fallback used)");
                        else _stateService?.SetState(ConnectorState.Error, "Sync failed (fallback also failed)");
                        return result;
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogError(ex, "Message bus processing failed and fallback disabled");
                        _stateService?.SetState(ConnectorState.Error, $"Error: {ex.Message}");
                        return false;
                    }
                }
                else
                {
                    _logger?.LogInformation(
                        "Message bus not available, using direct API processing for {ConnectorSource} in Standalone mode",
                        ConnectorSource
                    );
                    var sinceTimestamp = await CalculateSinceTimestampAsync(config, since);
                    var entries = await FetchGlucoseDataAsync(sinceTimestamp);
                    var result = await UploadToNightscoutAsync(entries, config);
                    if (result) _stateService?.SetState(ConnectorState.Idle, "Sync completed");
                    else _stateService?.SetState(ConnectorState.Error, "Sync failed");
                    return result;
                }
            }
            catch (Exception ex)
            {
                 _logger?.LogError(ex, "Unexpected error in SyncDataAsync");
                 _stateService?.SetState(ConnectorState.Error, $"Unexpected error: {ex.Message}");
                 return false;
            }
        }

        /// <summary>
        /// Helper method to fetch data with optional file I/O support (load/save)
        /// </summary>
        protected async Task<IEnumerable<TResult>> FetchWithOptionalFileIOAsync<TData, TResult>(
            TConfig config,
            Func<DateTime?, Task<TData?>> fetchAction,
            Func<TData, IEnumerable<TResult>> transformAction,
            IConnectorFileService<TData>? fileService,
            string filePrefix,
            DateTime? since = null
        )
            where TData : class
        {
            var results = new List<TResult>();

            try
            {
                TData? data = null;

                // Check if we should load from file instead of fetching from API
                if (config.LoadFromFile && fileService != null)
                {
                    var fileToLoad = config.LoadFilePath;

                    if (!string.IsNullOrEmpty(fileToLoad))
                    {
                        _logger?.LogInformation(
                            "Loading data from specified file: {FilePath}",
                            fileToLoad
                        );
                        data = await fileService.LoadDataAsync(fileToLoad);
                    }
                    else
                    {
                        // Load from most recent file in data directory
                        var dataDir = config.DataDirectory;
                        var availableFiles = fileService.GetAvailableDataFiles(dataDir, filePrefix);

                        if (availableFiles.Length > 0)
                        {
                            var mostRecentFile = fileService.GetMostRecentDataFile(
                                dataDir,
                                filePrefix
                            );
                            if (mostRecentFile != null)
                            {
                                _logger?.LogInformation(
                                    "Loading data from most recent file: {FilePath}",
                                    mostRecentFile
                                );
                                data = await fileService.LoadDataAsync(mostRecentFile);
                            }
                        }
                        else
                        {
                            _logger?.LogWarning(
                                "No saved data files found in directory: {DataDirectory}",
                                dataDir
                            );
                        }
                    }
                }
                else if (config.LoadFromFile && fileService == null)
                {
                    _logger?.LogWarning(
                        "LoadFromFile is enabled but no file service is available."
                    );
                }

                // If no data loaded from file, fetch from API
                if (data == null)
                {
                    _logger?.LogInformation("Fetching fresh data from source");

                    // Use catch-up functionality to determine optimal since timestamp
                    var effectiveSince = await CalculateSinceTimestampAsync(config, since);
                    data = await fetchAction(effectiveSince);

                    // Save the fetched data if SaveRawData is enabled
                    if (data != null && config.SaveRawData && fileService != null)
                    {
                        var dataDir = config.DataDirectory;
                        var savedPath = await fileService.SaveDataAsync(data, dataDir, filePrefix);
                        if (savedPath != null)
                        {
                            _logger?.LogInformation(
                                "Saved raw data for debugging: {FilePath}",
                                savedPath
                            );
                        }
                    }
                    else if (data != null && config.SaveRawData && fileService == null)
                    {
                        _logger?.LogWarning(
                            "SaveRawData is enabled but no file service is available."
                        );
                    }
                }

                // Transform data to results
                if (data != null)
                {
                    var transformed = transformAction(data);
                    results.AddRange(transformed);

                    // Track metrics if we have results
                    // NOTE: Metrics are now tracked in the Publish* methods to ensure we count
                    // successfully uploaded data rather than just fetched data.
                }

                _logger?.LogInformation("Retrieved {Count} items from source", results.Count);
            }
            catch (InvalidOperationException)
            {
                // Re-throw authentication-related exceptions
                throw;
            }
            catch (HttpRequestException)
            {
                // Re-throw HTTP-related exceptions (including rate limiting)
                throw;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error fetching data");
            }

            return results;
        }

        /// <summary>
        /// Upload glucose entries to Nightscout using the entries API
        /// </summary>
        public virtual async Task<bool> UploadToNightscoutAsync(
            IEnumerable<Entry> entries,
            TConfig config
        )
        {
            try
            {
                // In Nocturne mode, direct upload should not be used
                if (config.Mode == ConnectorMode.Nocturne)
                {
                    _logger?.LogWarning(
                        "Direct Nightscout upload attempted in Nocturne mode - this should use message bus instead"
                    );
                    return false;
                }

                var nightscoutUrl = config.NightscoutUrl.TrimEnd('/');
                var apiSecret = !string.IsNullOrEmpty(config.NightscoutApiSecret)
                    ? config.NightscoutApiSecret
                    : config.ApiSecret;

                if (string.IsNullOrEmpty(nightscoutUrl))
                {
                    throw new ArgumentException("Nightscout URL is required for direct upload");
                }

                if (string.IsNullOrEmpty(apiSecret))
                {
                    throw new ArgumentException("API Secret is required for Nightscout upload");
                }
                var entriesArray = new List<object>();
                foreach (var entry in entries)
                {
                    entriesArray.Add(entry);
                }

                if (entriesArray.Count == 0)
                {
                    Console.WriteLine("No entries to upload");
                    return true;
                }

                // Split into batches of 100 entries each to avoid large requests
                const int batchSize = 100;
                var batches = entriesArray
                    .Select((entry, index) => new { entry, index })
                    .GroupBy(x => x.index / batchSize)
                    .Select(g => g.Select(x => x.entry).ToList())
                    .ToList();

                bool allSuccessful = true;

                foreach (var batch in batches)
                {
                    var success = await UploadBatchToNightscoutAsync(
                        batch,
                        nightscoutUrl,
                        apiSecret
                    );
                    if (!success)
                    {
                        allSuccessful = false;
                    }
                }

                if (allSuccessful)
                {
                    TrackMetrics(entries);
                }

                return allSuccessful;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error uploading to Nightscout: {ex.Message}");
                return false;
            }
        }

        private async Task<bool> UploadBatchToNightscoutAsync(
            List<object> batch,
            string nightscoutUrl,
            string apiSecret
        )
        {
            const int maxRetries = 3;
            var retryDelays = new[]
            {
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(15),
            };

            for (int attempt = 0; attempt <= maxRetries; attempt++)
            {
                try
                {
                    var json = JsonSerializer.Serialize(batch);
                    var content = new StringContent(json, Encoding.UTF8, "application/json");

                    // Hash the API secret to match Nightscout's expected format
                    var hashedApiSecret = HashApiSecret(apiSecret);

                    var request = new HttpRequestMessage(
                        HttpMethod.Post,
                        $"{nightscoutUrl}/api/v1/entries"
                    );
                    request.Content = content;
                    request.Headers.Add("API-SECRET", hashedApiSecret);
                    request.Headers.Add("User-Agent", "Nocturne-Connect/1.0");

                    Console.WriteLine(
                        $"Uploading batch of {batch.Count} entries to {nightscoutUrl} (attempt {attempt + 1})"
                    );

                    var response = await _httpClient.SendAsync(request);
                    if (response.IsSuccessStatusCode)
                    {
                        Console.WriteLine($"Successfully uploaded batch of {batch.Count} entries");
                        return true;
                    }
                    else if (IsRetryableStatusCode(response.StatusCode) && attempt < maxRetries)
                    {
                        // Retryable error - wait and retry
                        Console.WriteLine(
                            $"Retryable error {response.StatusCode}, waiting {retryDelays[attempt].TotalSeconds} seconds before retry"
                        );
                        await Task.Delay(retryDelays[attempt]);
                        continue;
                    }
                    else
                    {
                        var errorContent = await response.Content.ReadAsStringAsync();
                        Console.WriteLine(
                            $"Failed to upload batch: {response.StatusCode} - {errorContent}"
                        );
                        return false;
                    }
                }
                catch (HttpRequestException ex) when (attempt < maxRetries)
                {
                    Console.WriteLine(
                        $"HTTP error on attempt {attempt + 1}: {ex.Message}, retrying..."
                    );
                    await Task.Delay(retryDelays[attempt]);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error uploading batch to Nightscout: {ex.Message}");
                    return false;
                }
            }
            return false;
        }

        /// <summary>
        /// Upload treatments to Nightscout using the treatments API
        /// </summary>
        public virtual async Task<bool> UploadTreatmentsToNightscoutAsync(
            IEnumerable<Treatment> treatments,
            TConfig config
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

                var treatmentsArray = treatments.ToList();

                if (treatmentsArray.Count == 0)
                {
                    Console.WriteLine("No treatments to upload");
                    return true;
                }
                var json = JsonSerializer.Serialize(treatmentsArray);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                // Hash the API secret to match Nightscout's expected format
                var hashedApiSecret = HashApiSecret(apiSecret);

                var request = new HttpRequestMessage(
                    HttpMethod.Post,
                    $"{nightscoutUrl}/api/v1/treatments"
                );
                request.Content = content;
                request.Headers.Add("API-SECRET", hashedApiSecret);
                request.Headers.Add("User-Agent", "Nocturne-Connect/1.0");

                Console.WriteLine(
                    $"Uploading {treatmentsArray.Count} treatments to {nightscoutUrl}"
                );

                var response = await _httpClient.SendAsync(request);

                if (response.IsSuccessStatusCode)
                {
                    Console.WriteLine($"Successfully uploaded {treatmentsArray.Count} treatments");
                    return true;
                }
                else
                {
                    var errorContent = await response.Content.ReadAsStringAsync();
                    Console.WriteLine(
                        $"Failed to upload treatments: {response.StatusCode} - {errorContent}"
                    );
                    return false;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error uploading treatments to Nightscout: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Optional method for connectors to implement file-based data loading/saving for debugging
        /// </summary>
        /// <typeparam name="TData">The type of data to save/load</typeparam>
        /// <param name="config">Configuration containing file I/O settings</param>
        /// <param name="dataFetcher">Function to fetch fresh data from the API</param>        /// <param name="dataProcessor">Function to process the data into glucose entries</param>
        /// <param name="fileService">File service for saving/loading data</param>
        /// <param name="filePrefix">Prefix for data files (e.g., "glooko_batch")</param>
        /// <param name="since">Optional since parameter for data fetching</param>
        /// <returns>Processed glucose entries</returns>
        protected virtual async Task<IEnumerable<Entry>> FetchWithOptionalFileIOAsync<TData>(
            TConfig config,
            Func<DateTime?, Task<TData?>> dataFetcher,
            Func<TData, IEnumerable<Entry>> dataProcessor,
            IConnectorFileService<TData>? fileService,
            string filePrefix,
            DateTime? since = null
        )
            where TData : class
        {
            var entries = new List<Entry>();

            try
            {
                TData? data = null;

                // Check if we should load from file instead of fetching from API
                if (config.LoadFromFile && fileService != null)
                {
                    if (!string.IsNullOrEmpty(config.LoadFilePath))
                    {
                        Console.WriteLine(
                            $"Loading {filePrefix} data from specified file: {config.LoadFilePath}"
                        );
                        data = await fileService.LoadDataAsync(config.LoadFilePath);
                    }
                    else
                    {
                        // Load from most recent file in data directory
                        var mostRecentFile = fileService.GetMostRecentDataFile(
                            config.DataDirectory,
                            filePrefix
                        );
                        if (mostRecentFile != null)
                        {
                            Console.WriteLine(
                                $"Loading {filePrefix} data from most recent file: {mostRecentFile}"
                            );
                            data = await fileService.LoadDataAsync(mostRecentFile);
                        }
                        else
                        {
                            Console.WriteLine(
                                $"No saved {filePrefix} data files found in directory: {config.DataDirectory}"
                            );
                        }
                    }
                }

                // If no data loaded from file, fetch from API
                if (data == null)
                {
                    Console.WriteLine($"Fetching fresh {filePrefix} data from API");
                    data = await dataFetcher(since);

                    // Save the fetched data if SaveRawData is enabled and fileService is available
                    if (data != null && config.SaveRawData && fileService != null)
                    {
                        var savedPath = await fileService.SaveDataAsync(
                            data,
                            config.DataDirectory,
                            filePrefix
                        );
                        if (savedPath != null)
                        {
                            Console.WriteLine(
                                $"Saved {filePrefix} data for debugging: {savedPath}"
                            );
                        }
                    }
                }

                // Process data into glucose entries
                if (data != null)
                {
                    entries.AddRange(dataProcessor(data));
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error in FetchWithOptionalFileIOAsync: {ex.Message}");
                throw;
            }

            return entries;
        }

        /// <summary>
        /// Automatically saves all array properties from a batch data object using reflection
        /// This eliminates verbose if/null checks for each property
        /// </summary>
        protected async Task SaveBatchDataAsync<TBatch>(
            TBatch batchData,
            string connectorName,
            TConfig config,
            ILogger logger
        )
            where TBatch : class
        {
            if (!config.SaveRawData || batchData == null)
                return;

            try
            {
                var batchType = typeof(TBatch);
                var properties = batchType
                    .GetProperties()
                    .Where(p => p.PropertyType.IsArray && p.CanRead);

                foreach (var prop in properties)
                {
                    var dataArray = prop.GetValue(batchData) as Array;
                    if (dataArray != null && dataArray.Length > 0)
                    {
                        // Convert Array to typed array for serialization
                        var elementType = prop.PropertyType.GetElementType();
                        if (elementType != null)
                        {
                            var typedArray = Array.CreateInstance(elementType, dataArray.Length);
                            Array.Copy(dataArray, typedArray, dataArray.Length);

                            await SaveDataByTypeAsync(
                                typedArray,
                                prop.Name,
                                connectorName,
                                config,
                                logger
                            );
                        }
                    }
                }

                // Also save the complete batch data object
                await SaveDataByTypeAsync(
                    new[] { batchData },
                    "batch",
                    connectorName,
                    config,
                    logger
                );
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error saving batch data using reflection");
            }
        }

        /// <summary>
        /// Save raw data files by data type to organized folder structure
        /// </summary>
        protected async Task SaveDataByTypeAsync<T>(
            T[] data,
            string dataTypeName,
            string connectorName,
            TConfig config,
            ILogger logger
        )
        {
            if (data == null || data.Length == 0)
                return;

            try
            {
                var timestamp = DateTime.Now.ToString("yyyy-MM-dd");
                var connectorFolder = Path.Combine(
                    config.DataDirectory,
                    connectorName.ToLowerInvariant()
                );
                var fileName =
                    $"{connectorName.ToLowerInvariant()}-{timestamp}-{dataTypeName.ToLowerInvariant()}.json";

                // Ensure directory exists
                if (!Directory.Exists(connectorFolder))
                {
                    Directory.CreateDirectory(connectorFolder);
                    logger.LogInformation(
                        "Created connector directory: {ConnectorFolder}",
                        connectorFolder
                    );
                }

                var filePath = Path.Combine(connectorFolder, fileName);
                var json = JsonSerializer.Serialize(
                    data,
                    new JsonSerializerOptions
                    {
                        WriteIndented = true,
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    }
                );

                await File.WriteAllTextAsync(filePath, json);
                logger.LogInformation(
                    "Saved {Count} {DataType} entries to {FilePath}",
                    data.Length,
                    dataTypeName,
                    filePath
                );
            }
            catch (Exception ex)
            {
                logger.LogError(
                    "Error saving {DataType} data to file: {Error}",
                    dataTypeName,
                    ex.Message
                );
            }
        }

        /// <summary>
        /// Overload that accepts Array for use with reflection
        /// </summary>
        protected async Task SaveDataByTypeAsync(
            Array data,
            string dataTypeName,
            string connectorName,
            TConfig config,
            ILogger logger
        )
        {
            if (data == null || data.Length == 0)
                return;

            try
            {
                var timestamp = DateTime.Now.ToString("yyyy-MM-dd");
                var connectorFolder = Path.Combine(
                    config.DataDirectory,
                    connectorName.ToLowerInvariant()
                );
                var fileName =
                    $"{connectorName.ToLowerInvariant()}-{timestamp}-{dataTypeName.ToLowerInvariant()}.json";

                // Ensure directory exists
                if (!Directory.Exists(connectorFolder))
                {
                    Directory.CreateDirectory(connectorFolder);
                    logger.LogInformation(
                        "Created connector directory: {ConnectorFolder}",
                        connectorFolder
                    );
                }

                var filePath = Path.Combine(connectorFolder, fileName);
                var json = JsonSerializer.Serialize(
                    data,
                    new JsonSerializerOptions
                    {
                        WriteIndented = true,
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    }
                );

                await File.WriteAllTextAsync(filePath, json);
                logger.LogInformation(
                    "Saved {Count} {DataType} entries to {FilePath}",
                    data.Length,
                    dataTypeName,
                    filePath
                );
            }
            catch (Exception ex)
            {
                logger.LogError(
                    "Error saving {DataType} data to file: {Error}",
                    dataTypeName,
                    ex.Message
                );
            }
        }

        /// <summary>
        /// Save treatments to file with organized folder structure
        /// </summary>
        protected async Task SaveTreatmentsToFileAsync(
            IEnumerable<Treatment> treatments,
            string connectorName,
            TConfig config,
            ILogger logger
        )
        {
            if (treatments == null)
                return;

            var treatmentsList = treatments.ToList();
            if (treatmentsList.Count == 0)
                return;

            try
            {
                var timestamp = DateTime.Now.ToString("yyyy-MM-dd");
                var connectorFolder = Path.Combine(
                    config.DataDirectory,
                    connectorName.ToLowerInvariant()
                );
                var fileName = $"{connectorName.ToLowerInvariant()}-{timestamp}-treatments.json";

                // Ensure directory exists
                if (!Directory.Exists(connectorFolder))
                {
                    Directory.CreateDirectory(connectorFolder);
                    logger.LogInformation(
                        "Created connector directory: {ConnectorFolder}",
                        connectorFolder
                    );
                }

                var filePath = Path.Combine(connectorFolder, fileName);
                var json = JsonSerializer.Serialize(
                    treatmentsList,
                    new JsonSerializerOptions
                    {
                        WriteIndented = true,
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    }
                );

                await File.WriteAllTextAsync(filePath, json);
                logger.LogInformation(
                    "Saved {Count} treatments to {FilePath}",
                    treatmentsList.Count,
                    filePath
                );
            }
            catch (Exception ex)
            {
                logger.LogError("Error saving treatments to file: {Error}", ex.Message);
            }
        }

        /// <summary>
        /// Save glucose entries to file with organized folder structure
        /// </summary>
        protected async Task SaveGlucoseEntriesToFileAsync(
            IEnumerable<Entry> entries,
            string connectorName,
            TConfig config,
            ILogger logger
        )
        {
            if (entries == null)
                return;

            var entriesList = entries.ToList();
            if (entriesList.Count == 0)
                return;

            try
            {
                var timestamp = DateTime.Now.ToString("yyyy-MM-dd");
                var connectorFolder = Path.Combine(
                    config.DataDirectory,
                    connectorName.ToLowerInvariant()
                );
                var fileName = $"{connectorName.ToLowerInvariant()}-{timestamp}-glucose.json";

                // Ensure directory exists
                if (!Directory.Exists(connectorFolder))
                {
                    Directory.CreateDirectory(connectorFolder);
                    logger.LogInformation(
                        "Created connector directory: {ConnectorFolder}",
                        connectorFolder
                    );
                }

                var filePath = Path.Combine(connectorFolder, fileName);
                var json = JsonSerializer.Serialize(
                    entriesList,
                    new JsonSerializerOptions
                    {
                        WriteIndented = true,
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    }
                );

                await File.WriteAllTextAsync(filePath, json);
                logger.LogInformation(
                    "Saved {Count} glucose entries to {FilePath}",
                    entriesList.Count,
                    filePath
                );
            }
            catch (Exception ex)
            {
                logger.LogError("Error saving glucose entries to file: {Error}", ex.Message);
            }
        }

        private static bool IsRetryableStatusCode(HttpStatusCode statusCode)
        {
            return statusCode == HttpStatusCode.InternalServerError
                || statusCode == HttpStatusCode.BadGateway
                || statusCode == HttpStatusCode.ServiceUnavailable
                || statusCode == HttpStatusCode.GatewayTimeout
                || statusCode == HttpStatusCode.TooManyRequests
                || statusCode == HttpStatusCode.RequestTimeout;
        }

        protected virtual void Dispose(bool disposing)
        {
            // HttpClient is managed by IHttpClientFactory - do not dispose
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
    }
}
