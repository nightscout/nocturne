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
        /// Get the timestamp of the most recent entry from the Nocturne API
        /// This enables "catch up" functionality to fetch only new data since the last upload
        /// </summary>
        protected virtual async Task<DateTime?> FetchLatestEntryTimestampAsync(TConfig config)
        {
            if (_apiDataSubmitter == null)
            {
                _logger?.LogDebug("API data submitter not available, cannot fetch latest entry timestamp");
                return null;
            }

            try
            {
                var timestamp = await _apiDataSubmitter.GetLatestEntryTimestampAsync(ConnectorSource);
                if (timestamp.HasValue)
                {
                    _logger?.LogInformation(
                        "Latest entry timestamp from API for {ConnectorSource}: {Timestamp:yyyy-MM-dd HH:mm:ss} UTC",
                        ConnectorSource,
                        timestamp.Value
                    );
                }
                else
                {
                    _logger?.LogDebug("No existing entries found for {ConnectorSource}", ConnectorSource);
                }
                return timestamp;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to fetch latest entry timestamp for {ConnectorSource}", ConnectorSource);
                return null;
            }
        }

        /// <summary>
        /// Get the timestamp of the most recent treatment from the Nocturne API
        /// This enables "catch up" functionality to fetch only new data since the last upload
        /// </summary>
        protected virtual async Task<DateTime?> FetchLatestTreatmentTimestampAsync(TConfig config)
        {
            if (_apiDataSubmitter == null)
            {
                _logger?.LogDebug("API data submitter not available, cannot fetch latest treatment timestamp");
                return null;
            }

            try
            {
                var timestamp = await _apiDataSubmitter.GetLatestTreatmentTimestampAsync(ConnectorSource);
                if (timestamp.HasValue)
                {
                    _logger?.LogInformation(
                        "Latest treatment timestamp from API for {ConnectorSource}: {Timestamp:yyyy-MM-dd HH:mm:ss} UTC",
                        ConnectorSource,
                        timestamp.Value
                    );
                }
                else
                {
                    _logger?.LogDebug("No existing treatments found for {ConnectorSource}", ConnectorSource);
                }
                return timestamp;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to fetch latest treatment timestamp for {ConnectorSource}", ConnectorSource);
                return null;
            }
        }

        /// <summary>
        /// Calculate the optimal "since" timestamp for fetching glucose entries
        /// Uses catch-up logic to fetch from the most recent entry, or falls back to default lookback
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

            // Get the most recent entry timestamp from Nocturne API
            var latestEntryTimestamp = await FetchLatestEntryTimestampAsync(config);

            return CalculateSinceFromTimestamp(latestEntryTimestamp, "entries");
        }

        /// <summary>
        /// Calculate the optimal "since" timestamp for fetching treatments
        /// Uses catch-up logic to fetch from the most recent treatment, or falls back to default lookback
        /// </summary>
        protected virtual async Task<DateTime> CalculateTreatmentSinceTimestampAsync(
            TConfig config,
            DateTime? defaultSince = null
        )
        {
            if (defaultSince.HasValue)
            {
                return defaultSince.Value;
            }

            // Get the most recent treatment timestamp from Nocturne API
            var latestTreatmentTimestamp = await FetchLatestTreatmentTimestampAsync(config);

            return CalculateSinceFromTimestamp(latestTreatmentTimestamp, "treatments");
        }

        /// <summary>
        /// Calculate the optimal "since" timestamp for fetching ALL data types (entries AND treatments)
        /// Uses the EARLIER of the two timestamps to ensure we don't miss any data.
        /// Use this when a single API call fetches both entries and treatments together.
        /// </summary>
        protected virtual async Task<DateTime> CalculateComprehensiveSinceTimestampAsync(
            TConfig config,
            DateTime? defaultSince = null
        )
        {
            if (defaultSince.HasValue)
            {
                return defaultSince.Value;
            }

            // Get both timestamps
            var latestEntryTimestamp = await FetchLatestEntryTimestampAsync(config);
            var latestTreatmentTimestamp = await FetchLatestTreatmentTimestampAsync(config);

            // Use the EARLIER of the two timestamps to ensure we catch up on all data types
            DateTime? earliestTimestamp = null;
            if (latestEntryTimestamp.HasValue && latestTreatmentTimestamp.HasValue)
            {
                earliestTimestamp = latestEntryTimestamp.Value < latestTreatmentTimestamp.Value
                    ? latestEntryTimestamp.Value
                    : latestTreatmentTimestamp.Value;

                _logger?.LogDebug(
                    "Using earlier timestamp for comprehensive sync: entries={EntryTimestamp}, treatments={TreatmentTimestamp}, using={Using}",
                    latestEntryTimestamp.Value,
                    latestTreatmentTimestamp.Value,
                    earliestTimestamp.Value
                );
            }
            else
            {
                // Use whichever one is available
                earliestTimestamp = latestEntryTimestamp ?? latestTreatmentTimestamp;
            }

            return CalculateSinceFromTimestamp(earliestTimestamp, "entries and treatments");
        }

        /// <summary>
        /// Helper method to calculate the since timestamp from a latest timestamp
        /// </summary>
        private DateTime CalculateSinceFromTimestamp(DateTime? latestTimestamp, string dataType)
        {
            if (latestTimestamp.HasValue)
            {
                // Add a small overlap to ensure we don't miss any data due to clock drift
                var sinceWithOverlap = latestTimestamp.Value.AddMinutes(-5);

                // Maximum 7 days for safety to avoid overwhelming the source API
                var maxLookback = DateTime.UtcNow.AddDays(-7);
                if (sinceWithOverlap < maxLookback)
                {
                    _logger?.LogInformation(
                        "Last {DataType} sync was more than 7 days ago, limiting lookback to 7 days for {ConnectorSource}",
                        dataType,
                        ConnectorSource
                    );
                    return maxLookback;
                }

                _logger?.LogInformation(
                    "Starting catch-up sync for {DataType} from {ConnectorSource} since {Since:yyyy-MM-dd HH:mm:ss} UTC",
                    dataType,
                    ConnectorSource,
                    sinceWithOverlap
                );
                return sinceWithOverlap;
            }

            // Fallback to 24 hours if no existing data found (first sync)
            var fallbackSince = DateTime.UtcNow.AddHours(-24);
            _logger?.LogInformation(
                "No existing {DataType} found for {ConnectorSource}, performing initial sync from {Since:yyyy-MM-dd HH:mm:ss} UTC",
                dataType,
                ConnectorSource,
                fallbackSince
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

        /// <inheritdoc/>
        public virtual List<SyncDataType> SupportedDataTypes => new() { SyncDataType.Glucose };

        public abstract Task<bool> AuthenticateAsync();
        public abstract Task<IEnumerable<Entry>> FetchGlucoseDataAsync(DateTime? since = null);

        /// <inheritdoc/>
        public virtual async Task<SyncResult> SyncDataAsync(
            SyncRequest request,
            TConfig config,
            CancellationToken cancellationToken
        )
        {
            return await PerformSyncInternalAsync(request, config, cancellationToken);
        }

        /// <summary>
        /// Core synchronization logic that processes data types in parallel.
        /// Shared between manual and background sync flows.
        /// </summary>
        protected virtual async Task<SyncResult> PerformSyncInternalAsync(
            SyncRequest request,
            TConfig config,
            CancellationToken cancellationToken
        )
        {
            var result = new SyncResult
            {
                StartTime = DateTimeOffset.UtcNow,
                Success = true
            };

            if (request.DataTypes == null || !request.DataTypes.Any())
            {
                request.DataTypes = SupportedDataTypes;
            }

            var tasks = request.DataTypes
                .Where(type => SupportedDataTypes.Contains(type))
                .Select(async type =>
                {
                    try
                    {
                        int count = 0;
                        DateTime? lastTime = null;

                        switch (type)
                        {
                            case SyncDataType.Glucose:
                                var entries = await FetchGlucoseDataRangeAsync(request.From, request.To);
                                var entryList = entries.ToList();
                                count = entryList.Count;
                                if (count > 0)
                                {
                                    lastTime = entryList.Max(e => e.Date);
                                }
                                await PublishGlucoseDataInBatchesAsync(
                                    entryList,
                                    config,
                                    cancellationToken
                                );
                                break;

                            case SyncDataType.Treatments:
                                var treatments = await FetchTreatmentsAsync(request.From, request.To);
                                var treatmentList = treatments.ToList();
                                count = treatmentList.Count;
                                if (count > 0)
                                {
                                    lastTime = treatmentList
                                        .Select(t => DateTime.TryParse(t.CreatedAt, out var dt) ? dt : (DateTime?)null)
                                        .Where(dt => dt.HasValue)
                                        .Max();
                                }
                                await PublishTreatmentDataInBatchesAsync(
                                    treatmentList,
                                    config,
                                    cancellationToken
                                );
                                break;

                            case SyncDataType.Profiles:
                                var profiles = await FetchProfilesAsync(request.From, request.To);
                                var profileList = profiles.ToList();
                                count = profileList.Count;
                                if (count > 0)
                                {
                                    lastTime = profileList
                                        .Select(p => DateTime.TryParse(p.StartDate, out var dt) ? dt : (DateTime?)null)
                                        .Where(dt => dt.HasValue)
                                        .Max();
                                }
                                await PublishProfileDataAsync(profileList, config, cancellationToken);
                                break;

                            case SyncDataType.DeviceStatus:
                                var statuses = await FetchDeviceStatusAsync(request.From, request.To);
                                var statusList = statuses.ToList();
                                count = statusList.Count;
                                if (count > 0)
                                {
                                    lastTime = statusList
                                        .Select(s => DateTime.TryParse(s.CreatedAt, out var dt) ? dt : (DateTime?)null)
                                        .Where(dt => dt.HasValue)
                                        .Max();
                                }
                                await PublishDeviceStatusAsync(statusList, config, cancellationToken);
                                break;

                            case SyncDataType.Activity:
                                var activities = await FetchActivitiesAsync(request.From, request.To);
                                var activityList = activities.ToList();
                                count = activityList.Count;
                                if (count > 0)
                                {
                                    lastTime = activityList
                                        .Select(a => DateTime.TryParse(a.CreatedAt, out var dt) ? dt : (DateTime?)null)
                                        .Where(dt => dt.HasValue)
                                        .Max();
                                }
                                await PublishActivityDataAsync(activityList, config, cancellationToken);
                                break;

                            case SyncDataType.Food:
                                var foods = await FetchFoodsAsync(request.From, request.To);
                                var foodList = foods.ToList();
                                count = foodList.Count;
                                // Food items generally don't have a timestamp in the model, skipping lastTime update
                                await PublishFoodDataAsync(foodList, config, cancellationToken);
                                break;
                        }

                        lock (result)
                        {
                            result.ItemsSynced[type] = count;
                            result.LastEntryTimes[type] = lastTime;
                        }
                    }
                    catch (Exception ex)
                    {
                        lock (result)
                        {
                            result.Success = false;
                            result.Errors.Add($"Failed to sync {type}: {ex.Message}");
                        }
                        _logger?.LogError(
                            ex,
                            "Failed to sync {DataType} for {Connector}",
                            type,
                            ConnectorSource
                        );
                    }
                });

            await Task.WhenAll(tasks);

            result.EndTime = DateTimeOffset.UtcNow;
            return result;
        }

        protected virtual Task<IEnumerable<Entry>> FetchGlucoseDataRangeAsync(
            DateTime? from,
            DateTime? to
        )
        {
            return FetchGlucoseDataAsync(from);
        }

        protected virtual Task<IEnumerable<Treatment>> FetchTreatmentsAsync(
            DateTime? from,
            DateTime? to
        )
        {
            return Task.FromResult(Enumerable.Empty<Treatment>());
        }

        protected virtual Task<IEnumerable<Profile>> FetchProfilesAsync(
            DateTime? from,
            DateTime? to
        )
        {
            return Task.FromResult(Enumerable.Empty<Profile>());
        }

        protected virtual Task<IEnumerable<DeviceStatus>> FetchDeviceStatusAsync(
            DateTime? from,
            DateTime? to
        )
        {
            return Task.FromResult(Enumerable.Empty<DeviceStatus>());
        }

        protected virtual Task<IEnumerable<Activity>> FetchActivitiesAsync(
            DateTime? from,
            DateTime? to
        )
        {
            return Task.FromResult(Enumerable.Empty<Activity>());
        }

        protected virtual Task<IEnumerable<Food>> FetchFoodsAsync(DateTime? from, DateTime? to)
        {
            return Task.FromResult(Enumerable.Empty<Food>());
        }

        /// <summary>
        /// Helper method to track metrics for any data type
        /// </summary>
        protected void TrackMetrics<T>(IEnumerable<T> items)
        {
            if (_metricsTracker == null)
                return;

            // To avoid multiple enumerations if not a collection
            var itemList = items as ICollection<T> ?? items.ToList();
            var count = itemList.Count;

            if (count == 0)
                return;

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
                    p.Name.Equals("CreatedAt", StringComparison.OrdinalIgnoreCase)
                    || p.Name.Equals("EventTime", StringComparison.OrdinalIgnoreCase)
                    || p.Name.Equals("Timestamp", StringComparison.OrdinalIgnoreCase)
                    || p.Name.Equals("StartDate", StringComparison.OrdinalIgnoreCase)
                    || // For Profile
                    p.Name.Equals("Date", StringComparison.OrdinalIgnoreCase)
                );

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
        /// Submits state span data directly to the API via HTTP
        /// </summary>
        protected virtual async Task<bool> PublishStateSpanDataAsync(
            IEnumerable<StateSpan> stateSpans,
            TConfig config,
            CancellationToken cancellationToken = default
        )
        {
            if (_apiDataSubmitter == null)
            {
                _logger?.LogWarning(
                    "API data submitter not available for state span submission"
                );
                return false;
            }

            var stateSpansArray = stateSpans.ToArray();
            if (stateSpansArray.Length == 0)
            {
                _logger?.LogInformation("No state spans to submit");
                return true;
            }

            try
            {
                var success = await _apiDataSubmitter.SubmitStateSpansAsync(
                    stateSpansArray,
                    ConnectorSource,
                    cancellationToken
                );

                if (success)
                {
                    _logger?.LogInformation(
                        "Successfully submitted {Count} state spans",
                        stateSpansArray.Length
                    );
                }

                return success;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to submit state span data");
                return false;
            }
        }

        /// <summary>
        /// Submits system event data directly to the API via HTTP
        /// </summary>
        protected virtual async Task<bool> PublishSystemEventDataAsync(
            IEnumerable<SystemEvent> systemEvents,
            TConfig config,
            CancellationToken cancellationToken = default
        )
        {
            if (_apiDataSubmitter == null)
            {
                _logger?.LogWarning(
                    "API data submitter not available for system event submission"
                );
                return false;
            }

            var eventsArray = systemEvents.ToArray();
            if (eventsArray.Length == 0)
            {
                _logger?.LogInformation("No system events to submit");
                return true;
            }

            try
            {
                var success = await _apiDataSubmitter.SubmitSystemEventsAsync(
                    eventsArray,
                    ConnectorSource,
                    cancellationToken
                );

                if (success)
                {
                    _logger?.LogInformation(
                        "Successfully submitted {Count} system events",
                        eventsArray.Length
                    );
                }

                return success;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to submit system event data");
                return false;
            }
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
                    await Task.Delay(10, cancellationToken);
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
                    _logger?.LogWarning(
                        "Failed to publish treatment batch {BatchNumber}",
                        batchNumber
                    );
                }

                batchNumber++;

                // Small delay between batches to avoid overwhelming the message bus
                if (batchNumber > 1)
                {
                    await Task.Delay(10, cancellationToken);
                }
            }

            return allSuccessful;
        }

        /// <summary>
        /// Main sync method that handles data synchronization based on connector mode
        /// </summary>
        /// <summary>
        /// Main sync method for background synchronization.
        /// Uses PerformSyncInternalAsync for parallelized processing.
        /// </summary>
        public virtual async Task<bool> SyncDataAsync(
            TConfig config,
            CancellationToken cancellationToken = default,
            DateTime? since = null
        )
        {
            _logger.LogInformation("Starting background data sync for {ConnectorSource}", ConnectorSource);
            _stateService?.SetState(ConnectorState.Syncing, "Syncing data...");

            try
            {
                // Authenticate if needed
                if (!await AuthenticateAsync())
                {
                    _logger.LogError("Authentication failed for {ConnectorSource}", ConnectorSource);
                    _stateService?.SetState(ConnectorState.Error, "Authentication failed");
                    return false;
                }

                // Determine catch-up timestamp
                var sinceTimestamp = since ?? await CalculateSinceTimestampAsync(config);

                var request = new SyncRequest
                {
                    From = sinceTimestamp,
                    To = null, // Open-ended for background sync
                    DataTypes = SupportedDataTypes
                };

                var result = await PerformSyncInternalAsync(request, config, cancellationToken);

                if (result.Success)
                {
                    _logger.LogInformation("Background sync completed successfully for {ConnectorSource}", ConnectorSource);

                    // Log details of what was synced
                    foreach (var type in result.ItemsSynced.Keys)
                    {
                        if (result.ItemsSynced[type] > 0)
                        {
                            _logger.LogInformation("Synced {Count} {Type} items", result.ItemsSynced[type], type);
                        }
                    }

                    _stateService?.SetState(ConnectorState.Idle, "Sync completed successfully");
                    return true;
                }
                else
                {
                    _logger.LogError(
                        "Background sync for {ConnectorSource} failed or had errors: {Errors}",
                        ConnectorSource,
                        string.Join("; ", result.Errors)
                    );
                    _stateService?.SetState(ConnectorState.Error, "Sync completed with errors");
                    return false;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error in background SyncDataAsync for {ConnectorSource}", ConnectorSource);
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
