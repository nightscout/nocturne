using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Utilities;
using Nocturne.Core.Models;

namespace Nocturne.Connectors.Core.Services;

/// <summary>
///     Base implementation for connector services with common Nightscout upload functionality
/// </summary>
/// <typeparam name="TConfig">The connector-specific configuration type</typeparam>
public abstract class BaseConnectorService<TConfig> : IConnectorService<TConfig>
    where TConfig : IConnectorConfiguration
{
    protected readonly HttpClient _httpClient;
    protected readonly ILogger _logger;
    private readonly IConnectorPublisher? _publisher;

    /// <summary>
    ///     Base constructor for connector services using IHttpClientFactory pattern
    /// </summary>
    /// <param name="httpClient">HttpClient instance from IHttpClientFactory (will not be disposed)</param>
    /// <param name="logger">Logger instance for this connector</param>
    /// <param name="publisher">Optional publisher for Nocturne mode</param>
    /// <param name="metricsTracker">Optional metrics tracker</param>
    /// <param name="stateService">Optional state service for tracking connector state</param>
    protected BaseConnectorService(
        HttpClient httpClient,
        ILogger logger,
        IConnectorPublisher? publisher = null
    )
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _publisher = publisher;
    }

    /// <summary>
    ///     Unique identifier for this connector service type
    /// </summary>
    protected abstract string ConnectorSource { get; }

    public abstract string ServiceName { get; }

    /// <inheritdoc />
    public virtual List<SyncDataType> SupportedDataTypes => [SyncDataType.Glucose];

    public abstract Task<bool> AuthenticateAsync();
    public abstract Task<IEnumerable<Entry>> FetchGlucoseDataAsync(DateTime? since = null);

    /// <inheritdoc />
    public virtual async Task<SyncResult> SyncDataAsync(
        SyncRequest request,
        TConfig config,
        CancellationToken cancellationToken
    )
    {
        return await PerformSyncInternalAsync(request, config, cancellationToken);
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    ///     Get the timestamp of the most recent entry from the Nocturne API
    ///     This enables "catch up" functionality to fetch only new data since the last upload
    /// </summary>
    private async Task<DateTime?> FetchLatestEntryTimestampAsync(TConfig config)
    {
        if (_publisher is not { IsAvailable: true })
        {
            _logger.LogDebug(
                "API data submitter not available, cannot fetch latest entry timestamp"
            );
            return null;
        }

        try
        {
            var timestamp = await _publisher.GetLatestEntryTimestampAsync(
                ConnectorSource
            );
            if (timestamp.HasValue)
                _logger.LogInformation(
                    "Latest entry timestamp from API for {ConnectorSource}: {Timestamp:yyyy-MM-dd HH:mm:ss} UTC",
                    ConnectorSource,
                    timestamp.Value
                );
            else
                _logger.LogDebug(
                    "No existing entries found for {ConnectorSource}",
                    ConnectorSource
                );
            return timestamp;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to fetch latest entry timestamp for {ConnectorSource}",
                ConnectorSource
            );
            return null;
        }
    }

    /// <summary>
    ///     Get the timestamp of the most recent treatment from the Nocturne API
    ///     This enables "catch up" functionality to fetch only new data since the last upload
    /// </summary>
    private async Task<DateTime?> FetchLatestTreatmentTimestampAsync(TConfig config)
    {
        if (_publisher is not { IsAvailable: true })
        {
            _logger.LogDebug(
                "API data submitter not available, cannot fetch latest treatment timestamp"
            );
            return null;
        }

        try
        {
            var timestamp = await _publisher.GetLatestTreatmentTimestampAsync(
                ConnectorSource
            );
            if (timestamp.HasValue)
                _logger.LogInformation(
                    "Latest treatment timestamp from API for {ConnectorSource}: {Timestamp:yyyy-MM-dd HH:mm:ss} UTC",
                    ConnectorSource,
                    timestamp.Value
                );
            else
                _logger.LogDebug(
                    "No existing treatments found for {ConnectorSource}",
                    ConnectorSource
                );
            return timestamp;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to fetch latest treatment timestamp for {ConnectorSource}",
                ConnectorSource
            );
            return null;
        }
    }

    /// <summary>
    ///     Calculate the optimal "since" timestamp for fetching glucose entries
    ///     Uses catch-up logic to fetch from the most recent entry, or falls back to default lookback
    /// </summary>
    protected async Task<DateTime> CalculateSinceTimestampAsync(
        TConfig config,
        DateTime? defaultSince = null
    )
    {
        if (defaultSince.HasValue) return defaultSince.Value;

        // Get the most recent entry timestamp from Nocturne API
        var latestEntryTimestamp = await FetchLatestEntryTimestampAsync(config);

        return CalculateSinceFromTimestamp(latestEntryTimestamp, "entries");
    }

    /// <summary>
    ///     Calculate the optimal "since" timestamp for fetching treatments
    ///     Uses catch-up logic to fetch from the most recent treatment, or falls back to default lookback
    /// </summary>
    protected async Task<DateTime> CalculateTreatmentSinceTimestampAsync(
        TConfig config,
        DateTime? defaultSince = null
    )
    {
        if (defaultSince.HasValue) return defaultSince.Value;

        // Get the most recent treatment timestamp from Nocturne API
        var latestTreatmentTimestamp = await FetchLatestTreatmentTimestampAsync(config);

        return CalculateSinceFromTimestamp(latestTreatmentTimestamp, "treatments");
    }

    /// <summary>
    ///     Helper method to calculate the since timestamp from a latest timestamp
    /// </summary>
    private DateTime CalculateSinceFromTimestamp(DateTime? latestTimestamp, string dataType)
    {
        if (latestTimestamp.HasValue)
        {
            // Add a small overlap to ensure we don't miss any data due to clock drift
            var sinceWithOverlap = latestTimestamp.Value.AddMinutes(-5);

            _logger?.LogInformation(
                "Starting catch-up sync for {DataType} from {ConnectorSource} since {Since:yyyy-MM-dd HH:mm:ss} UTC",
                dataType,
                ConnectorSource,
                sinceWithOverlap
            );
            return sinceWithOverlap;
        }

        // Fallback to 6 months for initial sync if no existing data found
        var fallbackSince = DateTime.UtcNow.AddMonths(-6);
        _logger?.LogInformation(
            "No existing {DataType} found for {ConnectorSource}, performing initial sync from {Since:yyyy-MM-dd HH:mm:ss} UTC",
            dataType,
            ConnectorSource,
            fallbackSince
        );
        return fallbackSince;
    }

    /// <summary>
    ///     Core synchronization logic that processes data types in parallel.
    ///     Shared between manual and background sync flows.
    /// </summary>
    protected virtual async Task<SyncResult> PerformSyncInternalAsync(
        SyncRequest request,
        TConfig config,
        CancellationToken cancellationToken
    )
    {
        var result = new SyncResult { StartTime = DateTimeOffset.UtcNow, Success = true };

        if (request.DataTypes == null || !request.DataTypes.Any()) request.DataTypes = SupportedDataTypes;

        var tasks = request
            .DataTypes.Where(type => SupportedDataTypes.Contains(type))
            .Select(async type =>
            {
                try
                {
                    var count = 0;
                    DateTime? lastTime = null;
                    var publishSuccess = true;

                    switch (type)
                    {
                        case SyncDataType.Glucose:
                            var entries = await FetchGlucoseDataRangeAsync(
                                request.From,
                                request.To
                            );
                            var entryList = entries.ToList();
                            count = entryList.Count;
                            if (count > 0) lastTime = entryList.Max(e => e.Date);
                            publishSuccess = await PublishGlucoseDataInBatchesAsync(
                                entryList,
                                config,
                                cancellationToken
                            );
                            break;

                        case SyncDataType.Treatments:
                            var treatments = await FetchTreatmentsAsync(
                                request.From,
                                request.To
                            );
                            var treatmentList = treatments.ToList();
                            count = treatmentList.Count;
                            if (count > 0)
                                lastTime = treatmentList
                                    .Select(t =>
                                        DateTime.TryParse(t.CreatedAt, out var dt)
                                            ? dt
                                            : (DateTime?)null
                                    )
                                    .Where(dt => dt.HasValue)
                                    .Max();
                            publishSuccess = await PublishTreatmentDataInBatchesAsync(
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
                                lastTime = profileList
                                    .Select(p =>
                                        DateTime.TryParse(p.StartDate, out var dt)
                                            ? dt
                                            : (DateTime?)null
                                    )
                                    .Where(dt => dt.HasValue)
                                    .Max();
                            publishSuccess = await PublishProfileDataAsync(
                                profileList,
                                config,
                                cancellationToken
                            );
                            break;

                        case SyncDataType.DeviceStatus:
                            var statuses = await FetchDeviceStatusAsync(
                                request.From,
                                request.To
                            );
                            var statusList = statuses.ToList();
                            count = statusList.Count;
                            if (count > 0)
                                lastTime = statusList
                                    .Select(s =>
                                        DateTime.TryParse(s.CreatedAt, out var dt)
                                            ? dt
                                            : (DateTime?)null
                                    )
                                    .Where(dt => dt.HasValue)
                                    .Max();
                            publishSuccess = await PublishDeviceStatusAsync(
                                statusList,
                                config,
                                cancellationToken
                            );
                            break;

                        case SyncDataType.Activity:
                            var activities = await FetchActivitiesAsync(
                                request.From,
                                request.To
                            );
                            var activityList = activities.ToList();
                            count = activityList.Count;
                            if (count > 0)
                                lastTime = activityList
                                    .Select(a =>
                                        DateTime.TryParse(a.CreatedAt, out var dt)
                                            ? dt
                                            : (DateTime?)null
                                    )
                                    .Where(dt => dt.HasValue)
                                    .Max();
                            publishSuccess = await PublishActivityDataAsync(
                                activityList,
                                config,
                                cancellationToken
                            );
                            break;

                        case SyncDataType.Food:
                            var foods = await FetchFoodsAsync(request.From, request.To);
                            var foodList = foods.ToList();
                            count = foodList.Count;
                            // Food items generally don't have a timestamp in the model, skipping lastTime update
                            publishSuccess = await PublishFoodDataAsync(
                                foodList,
                                config,
                                cancellationToken
                            );
                            break;
                    }

                    lock (result)
                    {
                        result.ItemsSynced[type] = count;
                        result.LastEntryTimes[type] = lastTime;
                        if (!publishSuccess)
                        {
                            result.Success = false;
                            result.Errors.Add($"{type} publish failed");
                        }
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
    ///     Helper method to track metrics for any data type
    /// </summary>
    /// <summary>
    ///     Submits glucose data directly to the API via HTTP
    /// </summary>
    protected virtual async Task<bool> PublishGlucoseDataAsync(
        IEnumerable<Entry> entries,
        TConfig config,
        CancellationToken cancellationToken = default
    )
    {
        if (_publisher == null || !_publisher.IsAvailable)
        {
            _logger?.LogWarning("Publisher not available for glucose data submission");
            return false;
        }

        return await _publisher.PublishEntriesAsync(entries, ConnectorSource, cancellationToken);
    }

    /// <summary>
    ///     Submits treatment data directly to the API via HTTP
    /// </summary>
    protected virtual async Task<bool> PublishTreatmentDataAsync(
        IEnumerable<Treatment> treatments,
        TConfig config,
        CancellationToken cancellationToken = default
    )
    {
        if (_publisher == null || !_publisher.IsAvailable)
        {
            _logger?.LogWarning("Publisher not available for treatment data submission");
            return false;
        }

        return await _publisher.PublishTreatmentsAsync(
            treatments,
            ConnectorSource,
            cancellationToken
        );
    }

    /// <summary>
    ///     Submits device status data directly to the API via HTTP
    /// </summary>
    protected virtual async Task<bool> PublishDeviceStatusAsync(
        IEnumerable<DeviceStatus> deviceStatuses,
        TConfig config,
        CancellationToken cancellationToken = default
    )
    {
        if (_publisher == null || !_publisher.IsAvailable)
        {
            _logger?.LogWarning(
                "Publisher not available for device status submission"
            );
            return false;
        }

        return await _publisher.PublishDeviceStatusAsync(
            deviceStatuses,
            ConnectorSource,
            cancellationToken
        );
    }

    /// <summary>
    ///     Submits profile data directly to the API via HTTP
    /// </summary>
    protected virtual async Task<bool> PublishProfileDataAsync(
        IEnumerable<Profile> profiles,
        TConfig config,
        CancellationToken cancellationToken = default
    )
    {
        if (_publisher == null || !_publisher.IsAvailable)
        {
            _logger?.LogWarning("Publisher not available for profile data submission");
            return false;
        }

        return await _publisher.PublishProfilesAsync(
            profiles,
            ConnectorSource,
            cancellationToken
        );
    }

    /// <summary>
    ///     Submits food data directly to the API via HTTP
    /// </summary>
    protected virtual async Task<bool> PublishFoodDataAsync(
        IEnumerable<Food> foods,
        TConfig config,
        CancellationToken cancellationToken = default
    )
    {
        if (_publisher == null || !_publisher.IsAvailable)
        {
            _logger?.LogWarning("Publisher not available for food data submission");
            return false;
        }

        return await _publisher.PublishFoodAsync(foods, ConnectorSource, cancellationToken);
    }

    /// <summary>
    ///     Submits activity data directly to the API via HTTP
    /// </summary>
    protected virtual async Task<bool> PublishActivityDataAsync(
        IEnumerable<Activity> activities,
        TConfig config,
        CancellationToken cancellationToken = default
    )
    {
        if (_publisher == null || !_publisher.IsAvailable)
        {
            _logger?.LogWarning(
                "Publisher not available for activity data submission"
            );
            return false;
        }

        return await _publisher.PublishActivityAsync(
            activities,
            ConnectorSource,
            cancellationToken
        );
    }

    /// <summary>
    ///     Submits state span data directly to the API via HTTP
    /// </summary>
    protected virtual async Task<bool> PublishStateSpanDataAsync(
        IEnumerable<StateSpan> stateSpans,
        TConfig config,
        CancellationToken cancellationToken = default
    )
    {
        if (_publisher == null || !_publisher.IsAvailable)
        {
            _logger?.LogWarning("Publisher not available for state span submission");
            return false;
        }

        return await _publisher.PublishStateSpansAsync(
            stateSpans,
            ConnectorSource,
            cancellationToken
        );
    }

    /// <summary>
    ///     Submits system event data directly to the API via HTTP
    /// </summary>
    protected virtual async Task<bool> PublishSystemEventDataAsync(
        IEnumerable<SystemEvent> systemEvents,
        TConfig config,
        CancellationToken cancellationToken = default
    )
    {
        if (_publisher == null || !_publisher.IsAvailable)
        {
            _logger?.LogWarning("Publisher not available for system event submission");
            return false;
        }

        return await _publisher.PublishSystemEventsAsync(
            systemEvents,
            ConnectorSource,
            cancellationToken
        );
    }

    /// <summary>
    ///     Publishes messages in batches to optimize throughput
    /// </summary>
    protected virtual async Task<bool> PublishGlucoseDataInBatchesAsync(
        IEnumerable<Entry> entries,
        TConfig config,
        CancellationToken cancellationToken = default
    )
    {
        var entriesArray = entries.ToArray();
        if (entriesArray.Length == 0) return true;

        var batchSize = Math.Max(1, config.BatchSize);
        var batches = entriesArray
            .Select((entry, index) => new { entry, index })
            .GroupBy(x => x.index / batchSize)
            .Select(g => g.Select(x => x.entry).ToArray());

        var allSuccessful = true;
        var batchNumber = 1;

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
            if (batchNumber > 1) await Task.Delay(10, cancellationToken);
        }

        return allSuccessful;
    }

    /// <summary>
    ///     Publishes treatment messages in batches to optimize throughput
    /// </summary>
    protected virtual async Task<bool> PublishTreatmentDataInBatchesAsync(
        IEnumerable<Treatment> treatments,
        TConfig config,
        CancellationToken cancellationToken = default
    )
    {
        var treatmentsArray = treatments.ToArray();
        if (treatmentsArray.Length == 0) return true;

        var batchSize = Math.Max(1, config.BatchSize);
        var batches = treatmentsArray
            .Select((treatment, index) => new { treatment, index })
            .GroupBy(x => x.index / batchSize)
            .Select(g => g.Select(x => x.treatment).ToArray());

        var allSuccessful = true;
        var batchNumber = 1;

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
            if (batchNumber > 1) await Task.Delay(10, cancellationToken);
        }

        return allSuccessful;
    }

    /// <summary>
    ///     Main sync method that handles data synchronization based on connector mode
    /// </summary>
    /// <summary>
    ///     Main sync method for background synchronization.
    ///     Uses PerformSyncInternalAsync for parallelized processing.
    /// </summary>
    public virtual async Task<bool> SyncDataAsync(
        TConfig config,
        CancellationToken cancellationToken = default,
        DateTime? since = null
    )
    {
        _logger.LogInformation(
            "Starting background data sync for {ConnectorSource}",
            ConnectorSource
        );
        try
        {
            // Authenticate if needed
            if (!await AuthenticateAsync())
            {
                _logger.LogError(
                    "Authentication failed for {ConnectorSource}",
                    ConnectorSource
                );
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
                _logger.LogInformation(
                    "Background sync completed successfully for {ConnectorSource}",
                    ConnectorSource
                );

                // Log details of what was synced
                foreach (var type in result.ItemsSynced.Keys)
                    if (result.ItemsSynced[type] > 0)
                        _logger.LogInformation(
                            "Synced {Count} {Type} items",
                            result.ItemsSynced[type],
                            type
                        );

                return true;
            }

            _logger.LogError(
                "Background sync for {ConnectorSource} failed or had errors: {Errors}",
                ConnectorSource,
                string.Join("; ", result.Errors)
            );
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Unexpected error in background SyncDataAsync for {ConnectorSource}",
                ConnectorSource
            );
            return false;
        }
    }

    protected virtual void Dispose(bool disposing)
    {
        // HttpClient is managed by IHttpClientFactory - do not dispose
    }

    #region Health Tracking

    /// <summary>
    ///     Tracks consecutive failed requests for health monitoring.
    ///     Automatically incremented on failures and reset on success.
    /// </summary>
    protected int _failedRequestCount;

    /// <summary>
    ///     Maximum failed requests before connector is considered unhealthy.
    ///     Override in derived classes to customize threshold.
    /// </summary>
    protected virtual int MaxFailedRequestsBeforeUnhealthy => 5;

    /// <summary>
    ///     Gets whether the connector is in a healthy state based on recent request failures.
    ///     Returns false if consecutive failures exceed MaxFailedRequestsBeforeUnhealthy.
    /// </summary>
    public virtual bool IsHealthy => _failedRequestCount < MaxFailedRequestsBeforeUnhealthy;

    /// <summary>
    ///     Gets the number of consecutive failed requests.
    /// </summary>
    public int FailedRequestCount => _failedRequestCount;

    /// <summary>
    ///     Resets the failed request counter. Call this after successful recovery.
    /// </summary>
    public virtual void ResetFailedRequestCount()
    {
        _failedRequestCount = 0;
        _logger.LogInformation(
            "[{ConnectorSource}] Failed request count reset",
            ConnectorSource
        );
    }

    /// <summary>
    ///     Increments the failed request count and logs the failure.
    /// </summary>
    protected void TrackFailedRequest(string? reason = null)
    {
        _failedRequestCount++;
        _logger.LogWarning(
            "[{ConnectorSource}] Request failed (count: {FailedCount}/{MaxAllowed}){Reason}",
            ConnectorSource,
            _failedRequestCount,
            MaxFailedRequestsBeforeUnhealthy,
            reason != null ? $": {reason}" : ""
        );
    }

    /// <summary>
    ///     Resets the failed request count on success.
    /// </summary>
    protected void TrackSuccessfulRequest()
    {
        if (_failedRequestCount > 0)
        {
            _logger.LogInformation(
                "[{ConnectorSource}] Request succeeded, resetting failed count from {PreviousCount}",
                ConnectorSource,
                _failedRequestCount
            );
            _failedRequestCount = 0;
        }
    }

    #endregion

    #region Retry and HTTP Helpers

    /// <summary>
    ///     Executes an async operation with retry logic and exponential backoff.
    ///     Automatically tracks success/failure for health monitoring.
    /// </summary>
    /// <typeparam name="T">The return type of the operation</typeparam>
    /// <param name="operation">The async operation to execute</param>
    /// <param name="retryStrategy">Strategy for calculating retry delays</param>
    /// <param name="reAuthenticateOnUnauthorized">Optional callback to re-authenticate on 401 responses</param>
    /// <param name="maxRetries">Maximum number of retry attempts (default: 3)</param>
    /// <param name="operationName">Name of the operation for logging</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result of the operation, or default(T) on failure</returns>
    protected async Task<T?> ExecuteWithRetryAsync<T>(
        Func<Task<T?>> operation,
        IRetryDelayStrategy retryStrategy,
        Func<Task<bool>>? reAuthenticateOnUnauthorized = null,
        int maxRetries = 3,
        string? operationName = null,
        CancellationToken cancellationToken = default
    )
    {
        var opName = operationName ?? "operation";
        HttpRequestException? lastException = null;

        for (var attempt = 0; attempt < maxRetries; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                _logger.LogDebug(
                    "[{ConnectorSource}] Executing {Operation} (attempt {Attempt}/{MaxRetries})",
                    ConnectorSource,
                    opName,
                    attempt + 1,
                    maxRetries
                );

                var result = await operation();

                // Success - track it and return
                TrackSuccessfulRequest();
                return result;
            }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
            {
                _logger.LogWarning(
                    "[{ConnectorSource}] Unauthorized response during {Operation}, attempting re-authentication",
                    ConnectorSource,
                    opName
                );

                if (reAuthenticateOnUnauthorized != null)
                {
                    var reAuthSuccess = await reAuthenticateOnUnauthorized();
                    if (reAuthSuccess)
                    {
                        _logger.LogInformation(
                            "[{ConnectorSource}] Re-authentication successful, retrying {Operation}",
                            ConnectorSource,
                            opName
                        );
                        continue; // Retry with new credentials
                    }
                }

                TrackFailedRequest("Unauthorized and re-authentication failed");
                return default;
            }
            catch (HttpRequestException ex) when (IsRetryableStatusCode(ex.StatusCode))
            {
                lastException = ex;
                _logger.LogWarning(
                    "[{ConnectorSource}] Retryable error during {Operation} (attempt {Attempt}): {StatusCode}",
                    ConnectorSource,
                    opName,
                    attempt + 1,
                    ex.StatusCode
                );

                if (attempt < maxRetries - 1) await retryStrategy.ApplyRetryDelayAsync(attempt);
            }
            catch (HttpRequestException ex)
            {
                // Non-retryable HTTP error
                _logger.LogError(
                    ex,
                    "[{ConnectorSource}] Non-retryable HTTP error during {Operation}: {StatusCode}",
                    ConnectorSource,
                    opName,
                    ex.StatusCode
                );
                TrackFailedRequest($"HTTP {ex.StatusCode}");
                return default;
            }
            catch (JsonException ex)
            {
                _logger.LogError(
                    ex,
                    "[{ConnectorSource}] JSON parsing error during {Operation}",
                    ConnectorSource,
                    opName
                );
                TrackFailedRequest("JSON parsing error");
                return default;
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation(
                    "[{ConnectorSource}] {Operation} was cancelled",
                    ConnectorSource,
                    opName
                );
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "[{ConnectorSource}] Unexpected error during {Operation}",
                    ConnectorSource,
                    opName
                );
                TrackFailedRequest($"Unexpected error: {ex.Message}");
                return default;
            }
        }

        // All retries exhausted
        TrackFailedRequest($"All {maxRetries} attempts failed");
        _logger.LogError(
            "[{ConnectorSource}] {Operation} failed after {MaxRetries} attempts",
            ConnectorSource,
            opName,
            maxRetries
        );

        if (lastException != null) throw lastException;

        return default;
    }

    /// <summary>
    ///     Sends an HTTP request with optional custom headers.
    ///     Useful for APIs that require per-request headers like Account-Id.
    /// </summary>
    /// <param name="method">HTTP method</param>
    /// <param name="url">Request URL</param>
    /// <param name="additionalHeaders">Optional headers to add to the request</param>
    /// <param name="content">Optional request content</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>HTTP response message</returns>
    protected async Task<HttpResponseMessage> SendWithHeadersAsync(
        HttpMethod method,
        string url,
        Dictionary<string, string>? additionalHeaders = null,
        HttpContent? content = null,
        CancellationToken cancellationToken = default
    )
    {
        using var request = new HttpRequestMessage(method, url);

        if (additionalHeaders != null)
            foreach (var header in additionalHeaders)
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);

        if (content != null) request.Content = content;

        return await _httpClient.SendAsync(request, cancellationToken);
    }

    /// <summary>
    ///     Sends a GET request with optional custom headers.
    /// </summary>
    protected Task<HttpResponseMessage> GetWithHeadersAsync(
        string url,
        Dictionary<string, string>? additionalHeaders = null,
        CancellationToken cancellationToken = default
    )
    {
        return SendWithHeadersAsync(HttpMethod.Get, url, additionalHeaders, null, cancellationToken);
    }

    /// <summary>
    ///     Sends a POST request with optional custom headers and content.
    /// </summary>
    protected Task<HttpResponseMessage> PostWithHeadersAsync(
        string url,
        HttpContent? content = null,
        Dictionary<string, string>? additionalHeaders = null,
        CancellationToken cancellationToken = default
    )
    {
        return SendWithHeadersAsync(
            HttpMethod.Post,
            url,
            additionalHeaders,
            content,
            cancellationToken
        );
    }

    /// <summary>
    ///     Determines if an HTTP status code is retryable.
    /// </summary>
    private static bool IsRetryableStatusCode(HttpStatusCode? statusCode)
    {
        return statusCode
            is HttpStatusCode.TooManyRequests
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.InternalServerError
            or HttpStatusCode.BadGateway
            or HttpStatusCode.GatewayTimeout
            or HttpStatusCode.RequestTimeout;
    }

    /// <summary>
    ///     Deserializes JSON content from an HTTP response using case-insensitive options.
    /// </summary>
    protected async Task<T?> DeserializeResponseAsync<T>(
        HttpResponseMessage response,
        CancellationToken cancellationToken = default
    )
    {
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize<T>(content, JsonDefaults.CaseInsensitive);
    }

    #endregion
}
