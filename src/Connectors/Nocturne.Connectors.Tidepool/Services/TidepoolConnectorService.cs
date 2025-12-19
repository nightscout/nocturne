using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nocturne.Connectors.Configurations;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.Tidepool.Constants;
using Nocturne.Connectors.Tidepool.Models;
using Nocturne.Core.Constants;
using Nocturne.Core.Models;

#nullable enable

namespace Nocturne.Connectors.Tidepool.Services;

/// <summary>
/// Connector service for Tidepool data source
/// Fetches CGM, bolus, food, and exercise data from Tidepool API
/// </summary>
public class TidepoolConnectorService : BaseConnectorService<TidepoolConnectorConfiguration>
{
    private readonly TidepoolConnectorConfiguration _config;
    private readonly IRetryDelayStrategy _retryDelayStrategy;
    private readonly IRateLimitingStrategy _rateLimitingStrategy;
    private string? _sessionToken;
    private string? _userId;
    private DateTime _sessionExpiresAt;
    private int _failedRequestCount = 0;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Gets whether the connector is in a healthy state based on recent request failures
    /// </summary>
    public bool IsHealthy => _failedRequestCount < 5;

    /// <summary>
    /// Gets the source identifier for this connector
    /// </summary>
    public override string ConnectorSource => DataSources.TidepoolConnector;

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
        _logger.LogInformation("Tidepool connector failed request count reset");
    }

    public override string ServiceName => "Tidepool";

    public TidepoolConnectorService(
        HttpClient httpClient,
        IOptions<TidepoolConnectorConfiguration> config,
        ILogger<TidepoolConnectorService> logger,
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
            rateLimitingStrategy ?? throw new ArgumentNullException(nameof(rateLimitingStrategy));
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
                    "Authenticating with Tidepool for account: {Username} (attempt {Attempt}/{MaxRetries})",
                    _config.TidepoolUsername,
                    attempt + 1,
                    maxRetries
                );

                // Tidepool uses Basic authentication for login
                var authString = Convert.ToBase64String(
                    Encoding.UTF8.GetBytes($"{_config.TidepoolUsername}:{_config.TidepoolPassword}")
                );

                var request = new HttpRequestMessage(
                    HttpMethod.Post,
                    TidepoolConstants.Endpoints.Login
                );
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic", authString);

                var response = await _httpClient.SendAsync(request);

                if (!response.IsSuccessStatusCode)
                {
                    var errorContent = await response.Content.ReadAsStringAsync();

                    // Check for rate limiting or temporary errors
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
                            "Tidepool authentication failed with retryable error on attempt {Attempt}: {StatusCode} - {Error}",
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
                        // Non-retryable error (e.g., invalid credentials)
                        _logger.LogError(
                            "Tidepool authentication failed with non-retryable error: {StatusCode} - {Error}",
                            response.StatusCode,
                            errorContent
                        );
                        _failedRequestCount++;
                        return false;
                    }
                }
                else
                {
                    // Extract session token from response header
                    if (
                        response.Headers.TryGetValues(
                            TidepoolConstants.Headers.SessionToken,
                            out var tokenValues
                        )
                    )
                    {
                        _sessionToken = tokenValues.FirstOrDefault();
                    }

                    if (string.IsNullOrEmpty(_sessionToken))
                    {
                        _logger.LogError("Tidepool authentication returned empty session token");
                        _failedRequestCount++;
                        return false;
                    }

                    // Parse response body for user ID
                    var jsonContent = await response.Content.ReadAsStringAsync();
                    var authResponse = JsonSerializer.Deserialize<TidepoolAuthResponse>(
                        jsonContent,
                        JsonOptions
                    );

                    if (authResponse == null || string.IsNullOrEmpty(authResponse.UserId))
                    {
                        _logger.LogError("Tidepool authentication returned empty user ID");
                        _failedRequestCount++;
                        return false;
                    }

                    _userId = authResponse.UserId;

                    // Set session expiration (Tidepool sessions typically last 24 hours)
                    _sessionExpiresAt = DateTime.UtcNow.AddHours(23);

                    // Reset failed request count on successful authentication
                    _failedRequestCount = 0;

                    _logger.LogInformation(
                        "Tidepool authentication successful for user {UserId}, session expires at {ExpiresAt}",
                        _userId,
                        _sessionExpiresAt
                    );
                    return true;
                }
            }
            catch (HttpRequestException ex)
            {
                lastException = ex;
                _logger.LogWarning(
                    ex,
                    "HTTP error during Tidepool authentication attempt {Attempt}: {Message}",
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
                    "Unexpected error during Tidepool authentication attempt {Attempt}",
                    attempt + 1
                );
                _failedRequestCount++;
                return false;
            }
        }

        // All attempts failed
        _failedRequestCount++;
        _logger.LogError("Tidepool authentication failed after {MaxRetries} attempts", maxRetries);

        if (lastException != null)
        {
            throw lastException;
        }

        return false;
    }

    private bool IsSessionExpired()
    {
        return string.IsNullOrEmpty(_sessionToken)
            || string.IsNullOrEmpty(_userId)
            || DateTime.UtcNow >= _sessionExpiresAt;
    }

    public override async Task<IEnumerable<Entry>> FetchGlucoseDataAsync(DateTime? since = null)
    {
        try
        {
            // Check if session is expired and re-authenticate if needed
            if (IsSessionExpired())
            {
                _logger.LogInformation(
                    "[{ConnectorSource}] Session expired, attempting to re-authenticate",
                    ConnectorSource
                );
                if (!await AuthenticateAsync())
                {
                    _failedRequestCount++;
                    return Enumerable.Empty<Entry>();
                }
            }

            // Apply rate limiting
            await _rateLimitingStrategy.ApplyDelayAsync(0);

            var bgValues = await FetchBgValuesAsync(since);
            var entries = TransformBgValuesToEntries(bgValues);

            _logger.LogInformation(
                "[{ConnectorSource}] Retrieved {Count} glucose entries from Tidepool",
                ConnectorSource,
                entries.Count()
            );

            return entries;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "[{ConnectorSource}] Error fetching glucose data from Tidepool",
                ConnectorSource
            );
            _failedRequestCount++;
            return Enumerable.Empty<Entry>();
        }
    }

    private async Task<List<TidepoolBgValue>> FetchBgValuesAsync(DateTime? since = null)
    {
        var results = new List<TidepoolBgValue>();
        const int maxRetries = 3;

        for (int attempt = 0; attempt < maxRetries; attempt++)
        {
            try
            {
                var start = since ?? DateTime.UtcNow.AddDays(-1);
                var end = DateTime.UtcNow;

                // Fetch both CGM and SMBG readings
                var types = $"{TidepoolConstants.DataTypes.Cbg},{TidepoolConstants.DataTypes.Smbg}";

                var url =
                    $"{string.Format(TidepoolConstants.Endpoints.DataFormat, _userId)}?type={types}&startDate={start:o}&endDate={end:o}";

                var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Add(TidepoolConstants.Headers.SessionToken, _sessionToken);

                var response = await _httpClient.SendAsync(request);

                if (!response.IsSuccessStatusCode)
                {
                    var errorContent = await response.Content.ReadAsStringAsync();

                    if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                    {
                        _logger.LogWarning(
                            "Tidepool session expired, attempting re-authentication"
                        );
                        _sessionToken = null;
                        _sessionExpiresAt = DateTime.MinValue;

                        if (await AuthenticateAsync())
                        {
                            continue; // Retry with new session
                        }
                        else
                        {
                            _logger.LogError("Failed to re-authenticate with Tidepool");
                            _failedRequestCount++;
                            return results;
                        }
                    }

                    if (
                        response.StatusCode == System.Net.HttpStatusCode.TooManyRequests
                        || response.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable
                    )
                    {
                        _logger.LogWarning(
                            "Tidepool data fetch failed with retryable error: {StatusCode}",
                            response.StatusCode
                        );
                        await _retryDelayStrategy.ApplyRetryDelayAsync(attempt);
                        continue;
                    }

                    _logger.LogError(
                        "Tidepool data fetch failed: {StatusCode} - {Error}",
                        response.StatusCode,
                        errorContent
                    );
                    _failedRequestCount++;
                    return results;
                }

                var jsonContent = await response.Content.ReadAsStringAsync();
                var bgValues = JsonSerializer.Deserialize<List<TidepoolBgValue>>(
                    jsonContent,
                    JsonOptions
                );

                if (bgValues != null)
                {
                    results.AddRange(bgValues);
                }

                _failedRequestCount = 0;
                break;
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning(
                    ex,
                    "HTTP error during Tidepool data fetch attempt {Attempt}",
                    attempt + 1
                );

                if (attempt < maxRetries - 1)
                {
                    await _retryDelayStrategy.ApplyRetryDelayAsync(attempt);
                }
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "JSON parsing error during Tidepool data fetch");
                _failedRequestCount++;
                return results;
            }
        }

        return results;
    }

    private IEnumerable<Entry> TransformBgValuesToEntries(List<TidepoolBgValue> bgValues)
    {
        return bgValues
            .Where(bg => bg.Time.HasValue && bg.Value > 0)
            .Select(bg =>
            {
                // Convert mmol/L to mg/dL if needed
                var sgvValue = bg.Value;
                if (bg.Units?.ToLower() is "mmol/l" or "mmol")
                {
                    sgvValue = bg.Value * 18.01559;
                }

                return new Entry
                {
                    Date = bg.Time!.Value,
                    Sgv = (int)Math.Round(sgvValue),
                    Type = "sgv",
                    Device = $"tidepool-{bg.DeviceId ?? "unknown"}",
                    Direction = "Flat", // Tidepool doesn't provide trend direction
                };
            })
            .OrderBy(e => e.Date)
            .ToList();
    }

    /// <summary>
    /// Fetch bolus data from Tidepool
    /// </summary>
    public async Task<List<TidepoolBolus>> FetchBolusDataAsync(DateTime? since = null)
    {
        if (IsSessionExpired() && !await AuthenticateAsync())
        {
            return new List<TidepoolBolus>();
        }

        return await FetchDataAsync<TidepoolBolus>(TidepoolConstants.DataTypes.Bolus, since);
    }

    /// <summary>
    /// Fetch food/carb data from Tidepool
    /// </summary>
    public async Task<List<TidepoolFood>> FetchFoodDataAsync(DateTime? since = null)
    {
        if (IsSessionExpired() && !await AuthenticateAsync())
        {
            return new List<TidepoolFood>();
        }

        return await FetchDataAsync<TidepoolFood>(TidepoolConstants.DataTypes.Food, since);
    }

    /// <summary>
    /// Fetch physical activity data from Tidepool
    /// </summary>
    public async Task<List<TidepoolPhysicalActivity>> FetchPhysicalActivityAsync(
        DateTime? since = null
    )
    {
        if (IsSessionExpired() && !await AuthenticateAsync())
        {
            return new List<TidepoolPhysicalActivity>();
        }

        return await FetchDataAsync<TidepoolPhysicalActivity>(
            TidepoolConstants.DataTypes.PhysicalActivity,
            since
        );
    }

    private async Task<List<T>> FetchDataAsync<T>(string dataType, DateTime? since = null)
    {
        var results = new List<T>();

        try
        {
            var start = since ?? DateTime.UtcNow.AddDays(-1);
            var end = DateTime.UtcNow;

            var url =
                $"{string.Format(TidepoolConstants.Endpoints.DataFormat, _userId)}?type={dataType}&startDate={start:o}&endDate={end:o}";

            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add(TidepoolConstants.Headers.SessionToken, _sessionToken);

            var response = await _httpClient.SendAsync(request);

            if (response.IsSuccessStatusCode)
            {
                var jsonContent = await response.Content.ReadAsStringAsync();
                var items = JsonSerializer.Deserialize<List<T>>(jsonContent, JsonOptions);
                if (items != null)
                {
                    results.AddRange(items);
                }
            }
            else
            {
                _logger.LogWarning(
                    "Failed to fetch {DataType} from Tidepool: {StatusCode}",
                    dataType,
                    response.StatusCode
                );
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching {DataType} from Tidepool", dataType);
        }

        return results;
    }

    /// <summary>
    /// Syncs Tidepool data including treatments
    /// </summary>
    public async Task<bool> SyncTidepoolDataAsync(
        TidepoolConnectorConfiguration config,
        CancellationToken cancellationToken = default,
        DateTime? since = null
    )
    {
        try
        {
            _logger.LogInformation("Starting Tidepool data sync");

            // Sync glucose data using base class
            var success = await SyncDataAsync(config, cancellationToken, since);

            // Optionally sync treatments
            if (success && config.SyncTreatments)
            {
                await SyncTreatmentsAsync(cancellationToken, since);
            }

            if (success)
            {
                _logger.LogInformation("Tidepool data sync completed successfully");
            }
            else
            {
                _logger.LogWarning("Tidepool data sync failed");
            }

            return success;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during Tidepool data sync");
            return false;
        }
    }

    private async Task SyncTreatmentsAsync(
        CancellationToken cancellationToken,
        DateTime? since = null
    )
    {
        try
        {
            _stateService?.SetState(ConnectorState.Syncing, "Downloading treatments...");
            since ??= DateTime.UtcNow.AddDays(-1);

            // Fetch bolus data
            var boluses = await FetchBolusDataAsync(since);
            var treatments = new List<Treatment>();

            foreach (var bolus in boluses.Where(b => b.Time.HasValue))
            {
                treatments.Add(
                    new Treatment
                    {
                        Created_at = bolus.Time!.Value.ToString("o"),
                        EventType = "Bolus",
                        Insulin = bolus.TotalInsulin,
                        Duration = bolus.Duration?.TotalMinutes,
                        EnteredBy = "Tidepool",
                    }
                );
            }

            // Fetch food data
            var foods = await FetchFoodDataAsync(since);
            foreach (var food in foods.Where(f => f.Time.HasValue))
            {
                var carbs = food.Nutrition?.Carbohydrate?.Net;
                if (carbs.HasValue && carbs > 0)
                {
                    treatments.Add(
                        new Treatment
                        {
                            Created_at = food.Time!.Value.ToString("o"),
                            EventType = "Meal Bolus",
                            Carbs = carbs,
                            EnteredBy = "Tidepool",
                        }
                    );
                }
            }

            if (treatments.Count > 0)
            {
                _stateService?.SetState(ConnectorState.Syncing, "Uploading treatments...");
                await PublishTreatmentDataAsync(treatments, _config, cancellationToken);
                _logger.LogInformation("Synced {Count} treatments from Tidepool", treatments.Count);
            }
            else
            {
                _stateService?.SetState(ConnectorState.Syncing, "No new treatments found");
            }

            // Fetch and sync physical activity data to Activity table
            await SyncPhysicalActivityAsync(since.Value, cancellationToken);

            _stateService?.SetState(ConnectorState.Idle, "Tidepool sync complete");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error syncing treatments from Tidepool");
            _stateService?.SetState(ConnectorState.Error, "Error syncing treatments");
        }
    }

    private async Task SyncPhysicalActivityAsync(
        DateTime since,
        CancellationToken cancellationToken
    )
    {
        try
        {
            _stateService?.SetState(ConnectorState.Syncing, "Downloading physical activity...");
            var physicalActivities = await FetchPhysicalActivityAsync(since);
            var activities = new List<Activity>();

            foreach (var pa in physicalActivities.Where(a => a.Time.HasValue))
            {
                var activity = new Activity
                {
                    CreatedAt = pa.Time!.Value.ToString("o"),
                    Mills = new DateTimeOffset(pa.Time.Value).ToUnixTimeMilliseconds(),
                    Name = pa.Name,
                    Type = "physicalActivity",
                    Description = pa.Name,
                    EnteredBy = "Tidepool",
                };

                // Convert duration from milliseconds to minutes
                if (pa.Duration?.Value.HasValue == true)
                {
                    activity.Duration = pa.Duration.Value / 60000;
                }

                // Add distance if available
                if (pa.Distance?.Value.HasValue == true)
                {
                    activity.Distance = pa.Distance.Value;
                    activity.DistanceUnits = pa.Distance.Units;
                }

                // Add energy if available
                if (pa.Energy?.Value.HasValue == true)
                {
                    activity.Energy = pa.Energy.Value;
                    activity.EnergyUnits = pa.Energy.Units;
                }

                activities.Add(activity);
            }

            if (activities.Count > 0)
            {
                await PublishActivityDataAsync(activities, _config, cancellationToken);
                _logger.LogInformation(
                    "Synced {Count} physical activities from Tidepool",
                    activities.Count
                );
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error syncing physical activities from Tidepool");
        }
    }
}
