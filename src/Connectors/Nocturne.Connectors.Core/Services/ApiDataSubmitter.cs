using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Core.Models;
using Polly;
using Polly.Retry;

namespace Nocturne.Connectors.Core.Services;

/// <summary>
/// Service for submitting data directly to the Nocturne API via HTTP with retry logic
/// </summary>
public class ApiDataSubmitter : IApiDataSubmitter
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<ApiDataSubmitter>? _logger;
    private readonly ResiliencePipeline _retryPipeline;
    private readonly string? _apiSecretHash;
    private readonly string _baseUrl;

    public ApiDataSubmitter(
        HttpClient httpClient,
        string baseUrl,
        string? apiSecret = null,
        ILogger<ApiDataSubmitter>? logger = null
    )
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _baseUrl = baseUrl?.TrimEnd('/') ?? throw new ArgumentNullException(nameof(baseUrl));
        // Pre-compute the SHA1 hash of the API secret (Nightscout expects hashed secrets)
        _apiSecretHash = !string.IsNullOrEmpty(apiSecret) ? ComputeSha1Hash(apiSecret) : null;
        _logger = logger;

        // Create retry pipeline with exponential backoff
        _retryPipeline = new ResiliencePipelineBuilder()
            .AddRetry(
                new RetryStrategyOptions
                {
                    MaxRetryAttempts = 3,
                    Delay = TimeSpan.FromSeconds(2),
                    BackoffType = DelayBackoffType.Exponential,
                    UseJitter = true,
                    ShouldHandle = new PredicateBuilder().Handle<HttpRequestException>(),
                    OnRetry = args =>
                    {
                        _logger?.LogWarning(
                            "Retry attempt {AttemptNumber} after {Delay}ms due to: {Exception}",
                            args.AttemptNumber,
                            args.RetryDelay.TotalMilliseconds,
                            args.Outcome.Exception?.Message ?? "Unknown error"
                        );
                        return ValueTask.CompletedTask;
                    },
                }
            )
            .Build();
    }

    /// <inheritdoc />
    public async Task<bool> SubmitEntriesAsync(
        IEnumerable<Entry> entries,
        string source,
        CancellationToken cancellationToken = default
    )
    {
        var entriesArray = entries.ToArray();
        if (entriesArray.Length == 0)
        {
            _logger?.LogDebug("No entries to submit");
            return true;
        }

        // Ensure DataSource is set on all entries for proper source tracking
        foreach (var entry in entriesArray)
        {
            if (string.IsNullOrEmpty(entry.DataSource))
            {
                entry.DataSource = source;
            }
        }

        return await _retryPipeline.ExecuteAsync(
            async ct =>
            {
                var url = $"{_baseUrl}/api/v1/entries";
                var request = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = JsonContent.Create(entriesArray),
                };

                AddAuthenticationHeader(request);

                _logger?.LogInformation(
                    "Submitting {Count} entries from {Source} to {Url}",
                    entriesArray.Length,
                    source,
                    url
                );

                var response = await _httpClient.SendAsync(request, ct);

                if (response.IsSuccessStatusCode)
                {
                    _logger?.LogInformation(
                        "Successfully submitted {Count} entries from {Source}",
                        entriesArray.Length,
                        source
                    );
                    return true;
                }

                var errorContent = await response.Content.ReadAsStringAsync(ct);
                _logger?.LogError(
                    "Failed to submit entries. Status: {StatusCode}, Response: {Response}",
                    response.StatusCode,
                    errorContent
                );

                // Throw exception for retryable errors (5xx, network issues)
                if (
                    (int)response.StatusCode >= 500
                    || response.StatusCode == HttpStatusCode.RequestTimeout
                )
                {
                    throw new HttpRequestException(
                        $"Server error {response.StatusCode}: {errorContent}"
                    );
                }

                // Don't retry for client errors (4xx)
                return false;
            },
            cancellationToken
        );
    }

    /// <inheritdoc />
    public async Task<bool> SubmitTreatmentsAsync(
        IEnumerable<Treatment> treatments,
        string source,
        CancellationToken cancellationToken = default
    )
    {
        var treatmentsArray = treatments.ToArray();
        if (treatmentsArray.Length == 0)
        {
            _logger?.LogDebug("No treatments to submit");
            return true;
        }

        // Ensure DataSource is set on all treatments for proper source tracking
        foreach (var treatment in treatmentsArray)
        {
            if (string.IsNullOrEmpty(treatment.DataSource))
            {
                treatment.DataSource = source;
            }
        }

        return await _retryPipeline.ExecuteAsync(
            async ct =>
            {
                var url = $"{_baseUrl}/api/v1/treatments";
                var request = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = JsonContent.Create(treatmentsArray),
                };

                AddAuthenticationHeader(request);

                _logger?.LogInformation(
                    "Submitting {Count} treatments from {Source} to {Url}",
                    treatmentsArray.Length,
                    source,
                    url
                );

                var response = await _httpClient.SendAsync(request, ct);

                if (response.IsSuccessStatusCode)
                {
                    _logger?.LogInformation(
                        "Successfully submitted {Count} treatments from {Source}",
                        treatmentsArray.Length,
                        source
                    );
                    return true;
                }

                var errorContent = await response.Content.ReadAsStringAsync(ct);
                _logger?.LogError(
                    "Failed to submit treatments. Status: {StatusCode}, Response: {Response}",
                    response.StatusCode,
                    errorContent
                );

                // Throw exception for retryable errors
                if (
                    (int)response.StatusCode >= 500
                    || response.StatusCode == HttpStatusCode.RequestTimeout
                )
                {
                    throw new HttpRequestException(
                        $"Server error {response.StatusCode}: {errorContent}"
                    );
                }

                return false;
            },
            cancellationToken
        );
    }

    /// <inheritdoc />
    public async Task<bool> SubmitDeviceStatusAsync(
        IEnumerable<DeviceStatus> deviceStatuses,
        string source,
        CancellationToken cancellationToken = default
    )
    {
        var statusesArray = deviceStatuses.ToArray();
        if (statusesArray.Length == 0)
        {
            _logger?.LogDebug("No device statuses to submit");
            return true;
        }

        return await _retryPipeline.ExecuteAsync(
            async ct =>
            {
                var url = $"{_baseUrl}/api/v1/devicestatus";
                var request = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = JsonContent.Create(statusesArray),
                };

                AddAuthenticationHeader(request);

                _logger?.LogInformation(
                    "Submitting {Count} device statuses from {Source} to {Url}",
                    statusesArray.Length,
                    source,
                    url
                );

                var response = await _httpClient.SendAsync(request, ct);

                if (response.IsSuccessStatusCode)
                {
                    _logger?.LogInformation(
                        "Successfully submitted {Count} device statuses from {Source}",
                        statusesArray.Length,
                        source
                    );
                    return true;
                }

                var errorContent = await response.Content.ReadAsStringAsync(ct);
                _logger?.LogError(
                    "Failed to submit device statuses. Status: {StatusCode}, Response: {Response}",
                    response.StatusCode,
                    errorContent
                );

                // Throw exception for retryable errors
                if (
                    (int)response.StatusCode >= 500
                    || response.StatusCode == HttpStatusCode.RequestTimeout
                )
                {
                    throw new HttpRequestException(
                        $"Server error {response.StatusCode}: {errorContent}"
                    );
                }

                return false;
            },
            cancellationToken
        );
    }

    /// <inheritdoc />
    public async Task<bool> SubmitProfilesAsync(
        IEnumerable<Profile> profiles,
        string source,
        CancellationToken cancellationToken = default
    )
    {
        var profilesArray = profiles.ToArray();
        if (profilesArray.Length == 0)
        {
            _logger?.LogDebug("No profiles to submit");
            return true;
        }

        return await _retryPipeline.ExecuteAsync(
            async ct =>
            {
                var url = $"{_baseUrl}/api/v1/profile";
                var request = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = JsonContent.Create(profilesArray),
                };

                AddAuthenticationHeader(request);

                _logger?.LogInformation(
                    "Submitting {Count} profiles from {Source} to {Url}",
                    profilesArray.Length,
                    source,
                    url
                );

                var response = await _httpClient.SendAsync(request, ct);

                if (response.IsSuccessStatusCode)
                {
                    _logger?.LogInformation(
                        "Successfully submitted {Count} profiles from {Source}",
                        profilesArray.Length,
                        source
                    );
                    return true;
                }

                var errorContent = await response.Content.ReadAsStringAsync(ct);
                _logger?.LogError(
                    "Failed to submit profiles. Status: {StatusCode}, Response: {Response}",
                    response.StatusCode,
                    errorContent
                );

                if (
                    (int)response.StatusCode >= 500
                    || response.StatusCode == HttpStatusCode.RequestTimeout
                )
                {
                    throw new HttpRequestException(
                        $"Server error {response.StatusCode}: {errorContent}"
                    );
                }

                return false;
            },
            cancellationToken
        );
    }

    /// <inheritdoc />
    public async Task<bool> SubmitFoodAsync(
        IEnumerable<Food> foods,
        string source,
        CancellationToken cancellationToken = default
    )
    {
        // Filter out food entries without a name as Nightscout requires it
        var foodsArray = foods
            .Where(f => !string.IsNullOrWhiteSpace(f.Name))
            .ToArray();

        if (foodsArray.Length == 0)
        {
            _logger?.LogDebug("No valid food entries to submit (all entries missing name)");
            return true;
        }

        var totalCount = foods.Count();
        var filteredCount = totalCount - foodsArray.Length;
        if (filteredCount > 0)
        {
            _logger?.LogWarning(
                "Filtered out {FilteredCount} food entries without names (total: {TotalCount})",
                filteredCount,
                totalCount
            );
        }

        return await _retryPipeline.ExecuteAsync(
            async ct =>
            {
                var url = $"{_baseUrl}/api/v1/food";
                var request = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = JsonContent.Create(foodsArray),
                };

                AddAuthenticationHeader(request);

                _logger?.LogInformation(
                    "Submitting {Count} food entries from {Source} to {Url}",
                    foodsArray.Length,
                    source,
                    url
                );

                var response = await _httpClient.SendAsync(request, ct);

                if (response.IsSuccessStatusCode)
                {
                    _logger?.LogInformation(
                        "Successfully submitted {Count} food entries from {Source}",
                        foodsArray.Length,
                        source
                    );
                    return true;
                }

                var errorContent = await response.Content.ReadAsStringAsync(ct);
                _logger?.LogError(
                    "Failed to submit food entries. Status: {StatusCode}, Response: {Response}",
                    response.StatusCode,
                    errorContent
                );

                if (
                    (int)response.StatusCode >= 500
                    || response.StatusCode == HttpStatusCode.RequestTimeout
                )
                {
                    throw new HttpRequestException(
                        $"Server error {response.StatusCode}: {errorContent}"
                    );
                }

                return false;
            },
            cancellationToken
        );
    }

    /// <inheritdoc />
    public async Task<bool> SubmitActivityAsync(
        IEnumerable<Activity> activities,
        string source,
        CancellationToken cancellationToken = default
    )
    {
        var activitiesArray = activities.ToArray();
        if (activitiesArray.Length == 0)
        {
            _logger?.LogDebug("No activities to submit");
            return true;
        }

        return await _retryPipeline.ExecuteAsync(
            async ct =>
            {
                var url = $"{_baseUrl}/api/v1/activity";
                var request = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = JsonContent.Create(activitiesArray),
                };

                AddAuthenticationHeader(request);

                _logger?.LogInformation(
                    "Submitting {Count} activities from {Source} to {Url}",
                    activitiesArray.Length,
                    source,
                    url
                );

                var response = await _httpClient.SendAsync(request, ct);

                if (response.IsSuccessStatusCode)
                {
                    _logger?.LogInformation(
                        "Successfully submitted {Count} activities from {Source}",
                        activitiesArray.Length,
                        source
                    );
                    return true;
                }

                var errorContent = await response.Content.ReadAsStringAsync(ct);
                _logger?.LogError(
                    "Failed to submit activities. Status: {StatusCode}, Response: {Response}",
                    response.StatusCode,
                    errorContent
                );

                if (
                    (int)response.StatusCode >= 500
                    || response.StatusCode == HttpStatusCode.RequestTimeout
                )
                {
                    throw new HttpRequestException(
                        $"Server error {response.StatusCode}: {errorContent}"
                    );
                }

                return false;
            },
            cancellationToken
        );
    }

    /// <inheritdoc />
    public async Task<DateTime?> GetLatestEntryTimestampAsync(
        string source,
        CancellationToken cancellationToken = default
    )
    {
        var connectorId = MapSourceToConnectorId(source);
        var syncStatus = await GetSyncStatusAsync(connectorId, cancellationToken);
        return syncStatus?.LatestEntryTimestamp;
    }

    /// <inheritdoc />
    public async Task<DateTime?> GetLatestTreatmentTimestampAsync(
        string source,
        CancellationToken cancellationToken = default
    )
    {
        var connectorId = MapSourceToConnectorId(source);
        var syncStatus = await GetSyncStatusAsync(connectorId, cancellationToken);
        return syncStatus?.LatestTreatmentTimestamp;
    }

    /// <inheritdoc />
    public async Task<bool> SubmitStateSpansAsync(
        IEnumerable<StateSpan> stateSpans,
        string source,
        CancellationToken cancellationToken = default
    )
    {
        var stateSpansArray = stateSpans.ToArray();
        if (stateSpansArray.Length == 0)
        {
            _logger?.LogDebug("No state spans to submit");
            return true;
        }

        // Ensure Source is set on all state spans
        foreach (var span in stateSpansArray)
        {
            if (string.IsNullOrEmpty(span.Source))
            {
                span.Source = source;
            }
        }

        return await _retryPipeline.ExecuteAsync(
            async ct =>
            {
                // Submit each state span individually via POST
                var successCount = 0;
                foreach (var span in stateSpansArray)
                {
                    var url = $"{_baseUrl}/api/v4/state-spans";
                    var request = new HttpRequestMessage(HttpMethod.Post, url)
                    {
                        Content = JsonContent.Create(span),
                    };

                    AddAuthenticationHeader(request);

                    var response = await _httpClient.SendAsync(request, ct);

                    if (response.IsSuccessStatusCode)
                    {
                        successCount++;
                    }
                    else
                    {
                        var errorContent = await response.Content.ReadAsStringAsync(ct);
                        _logger?.LogWarning(
                            "Failed to submit state span. Status: {StatusCode}, Response: {Response}",
                            response.StatusCode,
                            errorContent
                        );
                    }
                }

                _logger?.LogInformation(
                    "Successfully submitted {SuccessCount}/{TotalCount} state spans from {Source}",
                    successCount,
                    stateSpansArray.Length,
                    source
                );

                return successCount == stateSpansArray.Length;
            },
            cancellationToken
        );
    }

    /// <inheritdoc />
    public async Task<bool> SubmitSystemEventsAsync(
        IEnumerable<SystemEvent> systemEvents,
        string source,
        CancellationToken cancellationToken = default
    )
    {
        var eventsArray = systemEvents.ToArray();
        if (eventsArray.Length == 0)
        {
            _logger?.LogDebug("No system events to submit");
            return true;
        }

        // Ensure Source is set on all events
        foreach (var evt in eventsArray)
        {
            if (string.IsNullOrEmpty(evt.Source))
            {
                evt.Source = source;
            }
        }

        return await _retryPipeline.ExecuteAsync(
            async ct =>
            {
                // Submit each event individually via POST
                var successCount = 0;
                foreach (var evt in eventsArray)
                {
                    var url = $"{_baseUrl}/api/v4/system-events";
                    var request = new HttpRequestMessage(HttpMethod.Post, url)
                    {
                        Content = JsonContent.Create(evt),
                    };

                    AddAuthenticationHeader(request);

                    var response = await _httpClient.SendAsync(request, ct);

                    if (response.IsSuccessStatusCode)
                    {
                        successCount++;
                    }
                    else
                    {
                        var errorContent = await response.Content.ReadAsStringAsync(ct);
                        _logger?.LogWarning(
                            "Failed to submit system event. Status: {StatusCode}, Response: {Response}",
                            response.StatusCode,
                            errorContent
                        );
                    }
                }

                _logger?.LogInformation(
                    "Successfully submitted {SuccessCount}/{TotalCount} system events from {Source}",
                    successCount,
                    eventsArray.Length,
                    source
                );

                return successCount == eventsArray.Length;
            },
            cancellationToken
        );
    }

    /// <summary>
    /// Gets the full sync status for a connector from the API
    /// </summary>
    private async Task<SyncStatusResponse?> GetSyncStatusAsync(
        string connectorId,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var url = $"{_baseUrl}/api/v4/services/connectors/{connectorId}/sync-status";
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            AddAuthenticationHeader(request);

            _logger?.LogDebug("Fetching sync status from {Url}", url);

            var response = await _httpClient.SendAsync(request, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger?.LogWarning(
                    "Failed to get sync status for {ConnectorId}. Status: {StatusCode}",
                    connectorId,
                    response.StatusCode
                );
                return null;
            }

            var jsonOptions = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                Converters = { new JsonStringEnumConverter() }
            };

            var syncStatus = await response.Content.ReadFromJsonAsync<SyncStatusResponse>(
                jsonOptions,
                cancellationToken
            );

            _logger?.LogDebug(
                "Sync status for {ConnectorId}: LatestEntry={LatestEntry}, LatestTreatment={LatestTreatment}",
                connectorId,
                syncStatus?.LatestEntryTimestamp,
                syncStatus?.LatestTreatmentTimestamp
            );

            return syncStatus;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Error fetching sync status for {ConnectorId}", connectorId);
            return null;
        }
    }

    /// <summary>
    /// Maps a data source name (e.g., "dexcom-connector") to a connector ID (e.g., "dexcom")
    /// </summary>
    private static string MapSourceToConnectorId(string source)
    {
        // Remove "-connector" suffix if present
        if (source.EndsWith("-connector", StringComparison.OrdinalIgnoreCase))
        {
            return source[..^"-connector".Length].ToLowerInvariant();
        }
        return source.ToLowerInvariant();
    }

    private void AddAuthenticationHeader(HttpRequestMessage request)
    {
        if (!string.IsNullOrEmpty(_apiSecretHash))
        {
            request.Headers.Add("api-secret", _apiSecretHash);
        }
    }

    /// <summary>
    /// Compute SHA1 hash of a string (lowercase hex) - matches Nightscout's expected format
    /// </summary>
    private static string ComputeSha1Hash(string input)
    {
        var bytes = Encoding.UTF8.GetBytes(input);
        var hash = SHA1.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Response model for sync status API endpoint
    /// </summary>
    private class SyncStatusResponse
    {
        public string ConnectorId { get; set; } = string.Empty;
        public string DataSource { get; set; } = string.Empty;
        public DateTime? LatestEntryTimestamp { get; set; }
        public DateTime? LatestTreatmentTimestamp { get; set; }
        public bool HasEntries { get; set; }
        public bool HasTreatments { get; set; }
        public ConnectorState State { get; set; } = ConnectorState.Unknown;
        public string? StateMessage { get; set; }
        public bool IsHealthy { get; set; }
        public DateTime QueriedAt { get; set; }
    }
}
