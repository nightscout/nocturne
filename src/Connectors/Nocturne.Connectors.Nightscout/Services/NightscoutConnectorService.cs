using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.Nightscout.Configurations;
using Nocturne.Core.Constants;
using Nocturne.Core.Models;

namespace Nocturne.Connectors.Nightscout.Services;

public class NightscoutConnectorService : BaseConnectorService<NightscoutConnectorConfiguration>
{
    private readonly IRetryDelayStrategy _retryDelayStrategy;
    private readonly IRateLimitingStrategy _rateLimitingStrategy;
    private readonly NightscoutConnectorConfiguration _config;
    private string? _apiSecretHash;

    public NightscoutConnectorService(
        HttpClient httpClient,
        ILogger<NightscoutConnectorService> logger,
        IRetryDelayStrategy retryDelayStrategy,
        IRateLimitingStrategy rateLimitingStrategy,
        NightscoutConnectorConfiguration config,
        IConnectorPublisher? publisher = null
    )
        : base(httpClient, logger, publisher)
    {
        _retryDelayStrategy = retryDelayStrategy ?? throw new ArgumentNullException(nameof(retryDelayStrategy));
        _rateLimitingStrategy = rateLimitingStrategy ?? throw new ArgumentNullException(nameof(rateLimitingStrategy));
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    protected override string ConnectorSource => DataSources.NightscoutConnector;
    public override string ServiceName => "Nightscout";

    public override List<SyncDataType> SupportedDataTypes =>
    [
        SyncDataType.Glucose,
        SyncDataType.ManualBG,
        SyncDataType.Boluses,
        SyncDataType.CarbIntake,
        SyncDataType.BolusCalculations,
        SyncDataType.Notes,
        SyncDataType.DeviceEvents,
        SyncDataType.Profiles,
        SyncDataType.DeviceStatus,
        SyncDataType.Food,
        SyncDataType.Activity
    ];

    public override async Task<bool> AuthenticateAsync()
    {
        EnsureBaseAddress();

        if (string.IsNullOrEmpty(_config.ApiSecret))
        {
            _logger.LogError(
                "[{ConnectorSource}] API secret is not configured",
                ConnectorSource);
            TrackFailedRequest("API secret is not configured");
            return false;
        }

        _apiSecretHash = ComputeApiSecretHash(_config.ApiSecret);

        _logger.LogDebug(
            "[{ConnectorSource}] Authenticating with Nightscout at {Url}",
            ConnectorSource,
            _httpClient.BaseAddress);

        try
        {
            var headers = GetAuthHeaders();
            var response = await GetWithHeadersAsync("/api/v1/entries.json?count=1", headers);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();

                // Detect Cloudflare/WAF challenge pages that block server-to-server requests
                if (IsWafChallengePage(response, body))
                {
                    _logger.LogError(
                        "[{ConnectorSource}] Nightscout instance at {Url} is behind a WAF (e.g. Cloudflare) that is blocking API requests",
                        ConnectorSource,
                        _httpClient.BaseAddress);
                    TrackFailedRequest(
                        "Your Nightscout instance is behind a firewall (e.g. Cloudflare) that is blocking Nocturne from syncing. " +
                        "Please add a WAF bypass rule for API paths (e.g. /api/*) or allowlist the Nocturne server IP.");
                    return false;
                }

                _logger.LogError(
                    "[{ConnectorSource}] Nightscout auth check returned HTTP {StatusCode}: {Body}",
                    ConnectorSource,
                    (int)response.StatusCode,
                    body);
                TrackFailedRequest($"Nightscout auth check failed: HTTP {(int)response.StatusCode}");
                return false;
            }

            TrackSuccessfulRequest();
            _logger.LogInformation(
                "[{ConnectorSource}] Successfully authenticated with Nightscout instance",
                ConnectorSource);
            return true;
        }
        catch (Exception ex)
        {
            TrackFailedRequest($"Nightscout authentication failed: {ex.Message}");
            _logger.LogError(ex,
                "[{ConnectorSource}] Failed to connect to Nightscout instance at {Url}",
                ConnectorSource,
                _httpClient.BaseAddress);
            return false;
        }
    }

    public override async Task<SyncResult> SyncDataAsync(
        SyncRequest request,
        NightscoutConnectorConfiguration config,
        CancellationToken cancellationToken,
        ISyncProgressReporter? progressReporter = null)
    {
        if (!await AuthenticateAsync())
        {
            return new SyncResult
            {
                Success = false,
                Message = "Authentication failed"
            };
        }

        return await base.SyncDataAsync(request, config, cancellationToken, progressReporter);
    }

    protected override async Task<SyncResult> PerformSyncInternalAsync(
        SyncRequest request,
        NightscoutConnectorConfiguration config,
        CancellationToken cancellationToken,
        ISyncProgressReporter? progressReporter = null)
    {
        var result = new SyncResult { StartTime = DateTimeOffset.UtcNow, Success = true };

        if (!request.DataTypes.Any())
            request.DataTypes = SupportedDataTypes;

        var enabledTypes = config.GetEnabledDataTypes(SupportedDataTypes);
        var activeTypes = request.DataTypes.Where(t => enabledTypes.Contains(t)).ToList();

        // Handle Glucose
        if (activeTypes.Contains(SyncDataType.Glucose))
        {
            try
            {
                var entries = await FetchGlucoseDataRangeAsync(request.From, request.To);
                var entryList = entries.ToList();
                result.ItemsSynced[SyncDataType.Glucose] = entryList.Count;
                if (entryList.Count > 0)
                {
                    result.LastEntryTimes[SyncDataType.Glucose] = entryList.Max(e => e.Date);
                    var publishSuccess = await PublishGlucoseDataInBatchesAsync(
                        entryList, config, cancellationToken);
                    if (!publishSuccess)
                    {
                        result.Success = false;
                        result.Errors.Add("Glucose publish failed");
                    }
                }
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Errors.Add($"Failed to sync Glucose: {ex.Message}");
                _logger.LogError(ex, "Failed to sync Glucose for {Connector}", ConnectorSource);
            }
        }

        // Handle Treatments — Nightscout fetches all treatment types as one batch
        SyncDataType[] treatmentTypes =
        [
            SyncDataType.Boluses, SyncDataType.CarbIntake, SyncDataType.ManualBG,
            SyncDataType.BolusCalculations, SyncDataType.Notes, SyncDataType.DeviceEvents
        ];
        if (activeTypes.Any(t => treatmentTypes.Contains(t)))
        {
            try
            {
                var treatments = await FetchTreatmentsAsync(request.From, request.To);
                var treatmentList = treatments.ToList();
                if (treatmentList.Count > 0)
                {
                    var lastTime = treatmentList
                        .Select(t => DateTime.TryParse(t.CreatedAt, out var dt) ? dt : (DateTime?)null)
                        .Where(dt => dt.HasValue)
                        .Max();
                    var publishSuccess = await PublishTreatmentDataInBatchesAsync(
                        treatmentList, config, cancellationToken);

                    // Report count under each enabled treatment sub-type
                    foreach (var tt in treatmentTypes.Where(t => activeTypes.Contains(t)))
                    {
                        result.ItemsSynced[tt] = treatmentList.Count;
                        result.LastEntryTimes[tt] = lastTime;
                    }

                    if (!publishSuccess)
                    {
                        result.Success = false;
                        result.Errors.Add("Treatments publish failed");
                    }
                }
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Errors.Add($"Failed to sync Treatments: {ex.Message}");
                _logger.LogError(ex, "Failed to sync Treatments for {Connector}", ConnectorSource);
            }
        }

        // Handle Profiles
        if (activeTypes.Contains(SyncDataType.Profiles))
        {
            try
            {
                var profiles = await FetchProfilesAsync();
                var profileList = profiles.ToList();
                result.ItemsSynced[SyncDataType.Profiles] = profileList.Count;
                if (profileList.Count > 0)
                {
                    result.LastEntryTimes[SyncDataType.Profiles] = profileList
                        .Where(p => p.Mills > 0)
                        .Select(p => DateTimeOffset.FromUnixTimeMilliseconds(p.Mills).UtcDateTime)
                        .DefaultIfEmpty()
                        .Max();
                    var publishSuccess = await PublishProfileDataAsync(
                        profileList, config, cancellationToken);
                    if (!publishSuccess)
                    {
                        result.Success = false;
                        result.Errors.Add("Profiles publish failed");
                    }
                }
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Errors.Add($"Failed to sync Profiles: {ex.Message}");
                _logger.LogError(ex, "Failed to sync Profiles for {Connector}", ConnectorSource);
            }
        }

        // Handle DeviceStatus
        if (activeTypes.Contains(SyncDataType.DeviceStatus))
        {
            try
            {
                var deviceStatuses = await FetchDeviceStatusAsync(request.From, request.To);
                var deviceStatusList = deviceStatuses.ToList();
                result.ItemsSynced[SyncDataType.DeviceStatus] = deviceStatusList.Count;
                if (deviceStatusList.Count > 0)
                {
                    var lastTime = deviceStatusList
                        .Select(d => DateTimeOffset.TryParse(d.CreatedAt, out var dto) ? dto.UtcDateTime : (DateTime?)null)
                        .Where(dt => dt.HasValue)
                        .Max();
                    result.LastEntryTimes[SyncDataType.DeviceStatus] = lastTime;
                    var publishSuccess = await PublishDeviceStatusAsync(
                        deviceStatusList, config, cancellationToken);
                    if (!publishSuccess)
                    {
                        result.Success = false;
                        result.Errors.Add("DeviceStatus publish failed");
                    }
                }
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Errors.Add($"Failed to sync DeviceStatus: {ex.Message}");
                _logger.LogError(ex, "Failed to sync DeviceStatus for {Connector}", ConnectorSource);
            }
        }

        // Handle Food
        if (activeTypes.Contains(SyncDataType.Food))
        {
            try
            {
                var foods = await FetchFoodAsync();
                var foodList = foods.ToList();
                result.ItemsSynced[SyncDataType.Food] = foodList.Count;
                if (foodList.Count > 0)
                {
                    var publishSuccess = await PublishFoodDataAsync(
                        foodList, config, cancellationToken);
                    if (!publishSuccess)
                    {
                        result.Success = false;
                        result.Errors.Add("Food publish failed");
                    }
                }
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Errors.Add($"Failed to sync Food: {ex.Message}");
                _logger.LogError(ex, "Failed to sync Food for {Connector}", ConnectorSource);
            }
        }

        // Handle Activity
        if (activeTypes.Contains(SyncDataType.Activity))
        {
            try
            {
                var activities = await FetchActivityAsync(request.From, request.To);
                var activityList = activities.ToList();
                result.ItemsSynced[SyncDataType.Activity] = activityList.Count;
                if (activityList.Count > 0)
                {
                    var lastTime = activityList
                        .Select(a => DateTimeOffset.TryParse(a.CreatedAt, out var dto) ? dto.UtcDateTime : (DateTime?)null)
                        .Where(dt => dt.HasValue)
                        .Max();
                    result.LastEntryTimes[SyncDataType.Activity] = lastTime;
                    var publishSuccess = await PublishActivityDataAsync(
                        activityList, config, cancellationToken);
                    if (!publishSuccess)
                    {
                        result.Success = false;
                        result.Errors.Add("Activity publish failed");
                    }
                }
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Errors.Add($"Failed to sync Activity: {ex.Message}");
                _logger.LogError(ex, "Failed to sync Activity for {Connector}", ConnectorSource);
            }
        }

        result.EndTime = DateTimeOffset.UtcNow;
        return result;
    }

    public override async Task<IEnumerable<Entry>> FetchGlucoseDataAsync(DateTime? since = null)
    {
        return await FetchGlucoseDataRangeAsync(since, null);
    }

    protected override async Task<IEnumerable<Entry>> FetchGlucoseDataRangeAsync(
        DateTime? from, DateTime? to)
    {
        var allEntries = new List<Entry>();
        var currentTo = to;

        while (true)
        {
            var entries = await FetchDataAsync<Entry[]>(
                BuildEntriesUrl(from, currentTo),
                "FetchGlucoseData");

            if (entries == null || entries.Length == 0)
                break;

            foreach (var entry in entries)
                entry.DataSource = ConnectorSource;

            allEntries.AddRange(entries);

            // If we got fewer than MaxCount, we've fetched everything in this range
            if (entries.Length < _config.MaxCount)
                break;

            // Find the oldest entry's date and paginate backwards
            var oldestMs = entries.Min(e => e.Mills);
            if (oldestMs <= 0)
                break;

            var oldestDate = DateTimeOffset.FromUnixTimeMilliseconds(oldestMs).UtcDateTime;

            // Avoid infinite loop if the oldest date hasn't changed
            if (currentTo.HasValue && oldestDate >= currentTo.Value)
                break;

            // Next page: fetch entries older than the oldest we've seen
            currentTo = oldestDate.AddMilliseconds(-1);

            // If we've gone past the requested 'from', stop
            if (from.HasValue && currentTo < from)
                break;

            _logger.LogDebug(
                "[{ConnectorSource}] Paginating glucose entries, fetched {Count} so far, next page before {Before:yyyy-MM-dd HH:mm:ss}",
                ConnectorSource,
                allEntries.Count,
                currentTo);
        }

        _logger.LogInformation(
            "[{ConnectorSource}] Retrieved {Count} glucose entries from Nightscout",
            ConnectorSource,
            allEntries.Count);

        return allEntries;
    }

    protected override async Task<IEnumerable<Treatment>> FetchTreatmentsAsync(
        DateTime? from, DateTime? to)
    {
        var allTreatments = new List<Treatment>();
        var currentTo = to;

        while (true)
        {
            var treatments = await FetchDataAsync<Treatment[]>(
                BuildTreatmentsUrl(from, currentTo),
                "FetchTreatments");

            if (treatments == null || treatments.Length == 0)
                break;

            foreach (var treatment in treatments)
                treatment.DataSource = ConnectorSource;

            allTreatments.AddRange(treatments);

            if (treatments.Length < _config.MaxCount)
                break;

            // Find the oldest treatment's created_at and paginate backwards.
            // Use DateTimeOffset to ensure consistent UTC comparison regardless of system timezone.
            var oldestDate = treatments
                .Select(t => DateTimeOffset.TryParse(t.CreatedAt, out var dto) ? dto.UtcDateTime : (DateTime?)null)
                .Where(dt => dt.HasValue)
                .Min();

            if (!oldestDate.HasValue)
                break;

            if (currentTo.HasValue && oldestDate.Value >= currentTo.Value)
                break;

            currentTo = oldestDate.Value.AddMilliseconds(-1);

            if (from.HasValue && currentTo < from)
                break;

            _logger.LogDebug(
                "[{ConnectorSource}] Paginating treatments, fetched {Count} so far, next page before {Before:yyyy-MM-dd HH:mm:ss}",
                ConnectorSource,
                allTreatments.Count,
                currentTo);
        }

        _logger.LogInformation(
            "[{ConnectorSource}] Retrieved {Count} treatments from Nightscout",
            ConnectorSource,
            allTreatments.Count);

        return allTreatments;
    }

    protected override async Task<IEnumerable<Profile>> FetchProfilesAsync()
    {
        var profiles = await FetchDataAsync<Profile[]>(
            "/api/v1/profile.json",
            "FetchProfiles");

        if (profiles == null || profiles.Length == 0)
        {
            _logger.LogInformation(
                "[{ConnectorSource}] No profiles found on Nightscout instance",
                ConnectorSource);
            return [];
        }

        _logger.LogInformation(
            "[{ConnectorSource}] Retrieved {Count} profiles from Nightscout",
            ConnectorSource,
            profiles.Length);

        return profiles;
    }

    private async Task<IEnumerable<DeviceStatus>> FetchDeviceStatusAsync(
        DateTime? from, DateTime? to)
    {
        var allStatuses = new List<DeviceStatus>();
        var currentTo = to;

        while (true)
        {
            var statuses = await FetchDataAsync<DeviceStatus[]>(
                BuildDeviceStatusUrl(from, currentTo),
                "FetchDeviceStatus");

            if (statuses == null || statuses.Length == 0)
                break;

            allStatuses.AddRange(statuses);

            if (statuses.Length < _config.MaxCount)
                break;

            var oldestDate = statuses
                .Select(d => DateTimeOffset.TryParse(d.CreatedAt, out var dto) ? dto.UtcDateTime : (DateTime?)null)
                .Where(dt => dt.HasValue)
                .Min();

            if (!oldestDate.HasValue)
                break;

            if (currentTo.HasValue && oldestDate.Value >= currentTo.Value)
                break;

            currentTo = oldestDate.Value.AddMilliseconds(-1);

            if (from.HasValue && currentTo < from)
                break;

            _logger.LogDebug(
                "[{ConnectorSource}] Paginating device statuses, fetched {Count} so far, next page before {Before:yyyy-MM-dd HH:mm:ss}",
                ConnectorSource,
                allStatuses.Count,
                currentTo);
        }

        _logger.LogInformation(
            "[{ConnectorSource}] Retrieved {Count} device statuses from Nightscout",
            ConnectorSource,
            allStatuses.Count);

        return allStatuses;
    }

    private async Task<IEnumerable<Food>> FetchFoodAsync()
    {
        var foods = await FetchDataAsync<Food[]>(
            $"/api/v1/food.json?count={_config.MaxCount}",
            "FetchFood");

        if (foods == null || foods.Length == 0)
        {
            _logger.LogInformation(
                "[{ConnectorSource}] No food records found on Nightscout instance",
                ConnectorSource);
            return [];
        }

        _logger.LogInformation(
            "[{ConnectorSource}] Retrieved {Count} food records from Nightscout",
            ConnectorSource,
            foods.Length);

        return foods;
    }

    private async Task<IEnumerable<Activity>> FetchActivityAsync(
        DateTime? from, DateTime? to)
    {
        var allActivities = new List<Activity>();
        var currentTo = to;

        while (true)
        {
            var activities = await FetchDataAsync<Activity[]>(
                BuildActivityUrl(from, currentTo),
                "FetchActivity");

            if (activities == null || activities.Length == 0)
                break;

            allActivities.AddRange(activities);

            if (activities.Length < _config.MaxCount)
                break;

            var oldestDate = activities
                .Select(a => DateTimeOffset.TryParse(a.CreatedAt, out var dto) ? dto.UtcDateTime : (DateTime?)null)
                .Where(dt => dt.HasValue)
                .Min();

            if (!oldestDate.HasValue)
                break;

            if (currentTo.HasValue && oldestDate.Value >= currentTo.Value)
                break;

            currentTo = oldestDate.Value.AddMilliseconds(-1);

            if (from.HasValue && currentTo < from)
                break;

            _logger.LogDebug(
                "[{ConnectorSource}] Paginating activities, fetched {Count} so far, next page before {Before:yyyy-MM-dd HH:mm:ss}",
                ConnectorSource,
                allActivities.Count,
                currentTo);
        }

        _logger.LogInformation(
            "[{ConnectorSource}] Retrieved {Count} activities from Nightscout",
            ConnectorSource,
            allActivities.Count);

        return allActivities;
    }

    private async Task<T?> FetchDataAsync<T>(string url, string operationName) where T : class
    {
        await _rateLimitingStrategy.ApplyDelayAsync(0);

        return await ExecuteWithRetryAsync(
            async () => await FetchDataCoreAsync<T>(url),
            _retryDelayStrategy,
            operationName: operationName);
    }

    private async Task<T?> FetchDataCoreAsync<T>(string url) where T : class
    {
        var headers = GetAuthHeaders();
        var response = await GetWithHeadersAsync(url, headers);

        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync();
            throw new HttpRequestException(
                $"HTTP {(int)response.StatusCode} {response.StatusCode}: {errorContent}",
                null,
                response.StatusCode);
        }

        return await DeserializeResponseAsync<T>(response);
    }

    private string BuildEntriesUrl(DateTime? from, DateTime? to)
    {
        var url = $"/api/v1/entries.json?count={_config.MaxCount}";

        if (from.HasValue)
        {
            var fromMs = new DateTimeOffset(from.Value, TimeSpan.Zero).ToUnixTimeMilliseconds();
            url += $"&find[date][$gte]={fromMs}";
        }

        if (to.HasValue)
        {
            var toMs = new DateTimeOffset(to.Value, TimeSpan.Zero).ToUnixTimeMilliseconds();
            url += $"&find[date][$lte]={toMs}";
        }

        return url;
    }

    private string BuildTreatmentsUrl(DateTime? from, DateTime? to)
    {
        var url = $"/api/v1/treatments.json?count={_config.MaxCount}";

        if (from.HasValue)
            url += $"&find[created_at][$gte]={from.Value.ToUniversalTime():o}";

        if (to.HasValue)
            url += $"&find[created_at][$lte]={to.Value.ToUniversalTime():o}";

        return url;
    }

    private string BuildDeviceStatusUrl(DateTime? from, DateTime? to)
    {
        var url = $"/api/v1/devicestatus.json?count={_config.MaxCount}";

        if (from.HasValue)
            url += $"&find[created_at][$gte]={from.Value.ToUniversalTime():o}";

        if (to.HasValue)
            url += $"&find[created_at][$lte]={to.Value.ToUniversalTime():o}";

        return url;
    }

    private string BuildActivityUrl(DateTime? from, DateTime? to)
    {
        var url = $"/api/v1/activity.json?count={_config.MaxCount}";

        if (from.HasValue)
            url += $"&find[created_at][$gte]={from.Value.ToUniversalTime():o}";

        if (to.HasValue)
            url += $"&find[created_at][$lte]={to.Value.ToUniversalTime():o}";

        return url;
    }

    private void EnsureBaseAddress()
    {
        if (_httpClient.BaseAddress != null)
            return;

        if (string.IsNullOrEmpty(_config.Url))
            throw new InvalidOperationException("Nightscout URL is not configured");

        var url = _config.Url.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? _config.Url
            : $"https://{_config.Url}";

        _httpClient.BaseAddress = new Uri(url);
    }

    private Dictionary<string, string> GetAuthHeaders()
    {
        return new Dictionary<string, string>
        {
            ["api-secret"] = _apiSecretHash ?? ComputeApiSecretHash(_config.ApiSecret)
        };
    }

    internal static string ComputeApiSecretHash(string apiSecret)
    {
        if (IsAlreadySha1Hash(apiSecret))
            return apiSecret.ToLowerInvariant();

        var bytes = SHA1.HashData(Encoding.UTF8.GetBytes(apiSecret));
        return Convert.ToHexStringLower(bytes);
    }

    private static bool IsAlreadySha1Hash(string value)
    {
        return value.Length == 40 && value.All(c => char.IsAsciiHexDigit(c));
    }

    /// <summary>
    ///     Detects WAF challenge pages (Cloudflare, Akamai, etc.) that block server-to-server API requests.
    ///     These return HTML instead of JSON and typically include challenge scripts.
    /// </summary>
    private static bool IsWafChallengePage(HttpResponseMessage response, string body)
    {
        // Check for Cloudflare server header
        if (response.Headers.TryGetValues("server", out var serverValues) &&
            serverValues.Any(v => v.Contains("cloudflare", StringComparison.OrdinalIgnoreCase)))
        {
            // Cloudflare returning non-JSON (challenge page) for an API request
            var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
            if (contentType.Contains("html", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        // Check for cf-ray header (Cloudflare) with HTML body containing challenge markers
        if (response.Headers.Contains("cf-ray") &&
            body.Contains("challenge-platform", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }
}
