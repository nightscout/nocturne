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

public class NightscoutConnectorServiceBase<TConfig> : BaseConnectorService<TConfig>
    where TConfig : NightscoutConnectorConfiguration
{
    private readonly IRetryDelayStrategy _retryDelayStrategy;
    private readonly IRateLimitingStrategy _rateLimitingStrategy;

    // Starts as the startup defaults (from IConnectorRegistration); replaced with the
    // per-tenant config when AuthenticateWithConfigAsync runs at the start of a sync.
    // Per-instance, no concurrency: connectors are resolved into a fresh DI scope per
    // tenant sync, and SyncDataAsync is not invoked concurrently on the same instance.
    private TConfig _currentConfig;
    private string? _apiSecretHash;
    private string? _resolvedBaseUrl;

    public NightscoutConnectorServiceBase(
        HttpClient httpClient,
        IConnectorServerResolver<TConfig> serverResolver,
        ILogger logger,
        IRetryDelayStrategy retryDelayStrategy,
        IRateLimitingStrategy rateLimitingStrategy,
        IConnectorRegistration<TConfig> registration,
        IConnectorPublisher? publisher = null
    )
        : base(httpClient, serverResolver, logger, publisher)
    {
        _retryDelayStrategy = retryDelayStrategy ?? throw new ArgumentNullException(nameof(retryDelayStrategy));
        _rateLimitingStrategy = rateLimitingStrategy ?? throw new ArgumentNullException(nameof(rateLimitingStrategy));
        _currentConfig = registration?.Defaults ?? throw new ArgumentNullException(nameof(registration));
    }

    protected override string ConnectorSource => DataSources.NightscoutConnector;
    public override string ServiceName => "Nightscout";

    // A Nightscout instance is a full data export, so the initial sync (no prior data) imports the
    // source's entire history rather than the default bounded window — capping the first backfill
    // would silently drop older records. Catch-up syncs still resume from each type's own cursor.
    protected override DateTime? InitialSyncFloor => null;

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
        // Legacy no-config overload; uses whatever config the service was last primed
        // with (startup defaults until AuthenticateWithConfigAsync replaces it).
        // Per-tenant sync uses AuthenticateWithConfigAsync instead.
        return await AuthenticateWithConfigAsync(_currentConfig);
    }

    private async Task<bool> AuthenticateWithConfigAsync(TConfig config)
    {
        _currentConfig = config;
        _resolvedBaseUrl = ResolveBaseUrl(config.Url);

        if (string.IsNullOrEmpty(config.ApiSecret))
        {
            _logger.LogError(
                "[{ConnectorSource}] API secret is not configured",
                ConnectorSource);
            TrackFailedRequest("API secret is not configured");
            return false;
        }

        _apiSecretHash = ComputeApiSecretHash(config.ApiSecret);

        _logger.LogDebug(
            "[{ConnectorSource}] Authenticating with Nightscout at {Url}",
            ConnectorSource,
            _resolvedBaseUrl);

        try
        {
            var headers = GetAuthHeaders();
            var response = await GetWithHeadersAsync(
                $"{_resolvedBaseUrl}/api/v1/entries.json?count=1", headers);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();

                // Detect Cloudflare/WAF challenge pages that block server-to-server requests
                if (IsWafChallengePage(response, body))
                {
                    _logger.LogError(
                        "[{ConnectorSource}] Nightscout instance at {Url} is behind a WAF (e.g. Cloudflare) that is blocking API requests",
                        ConnectorSource,
                        _resolvedBaseUrl);
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
                _resolvedBaseUrl);
            return false;
        }
    }

    public override async Task<SyncResult> SyncDataAsync(
        TConfig config,
        CancellationToken cancellationToken = default,
        DateTime? since = null,
        ISyncProgressReporter? progressReporter = null)
    {
        // _currentConfig starts as startup defaults (empty URL). Prime it with the
        // tenant config before base calls AuthenticateAsync(), which delegates to
        // AuthenticateWithConfigAsync(_currentConfig).
        _currentConfig = config;
        return await base.SyncDataAsync(config, cancellationToken, since, progressReporter);
    }

    public override async Task<SyncResult> SyncDataAsync(
        SyncRequest request,
        TConfig config,
        CancellationToken cancellationToken,
        ISyncProgressReporter? progressReporter = null)
    {
        if (!await AuthenticateWithConfigAsync(config))
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
        TConfig config,
        CancellationToken cancellationToken,
        ISyncProgressReporter? progressReporter = null)
    {
        var result = new SyncResult { StartTime = DateTimeOffset.UtcNow, Success = true };

        if (!request.DataTypes.Any())
            request.DataTypes = SupportedDataTypes;

        var enabledTypes = config.GetEnabledDataTypes(SupportedDataTypes);
        var activeTypes = request.DataTypes.Where(t => enabledTypes.Contains(t)).ToList();

        // For open-ended background catch-up (no explicit upper bound), each data type
        // resolves its own "since" from its OWN latest stored record rather than reusing
        // the glucose-derived request.From. Otherwise a single type that fell behind (or
        // failed once) would be permanently stranded behind the glucose cursor. Explicit
        // ranged syncs (request.To set, e.g. a manual re-import) honour request.From/To as-is.
        var openEnded = request.To is null;

        // Handle Glucose
        // Glucose keeps request.From — for background syncs the framework already derived
        // it from the latest glucose entry, so it is glucose's own independent cursor.
        //
        // Each data type below streams fetch-page → publish-page rather than accumulating
        // the whole range first: a multi-year backfill of a high-volume collection held in
        // one list has taken the process out with OutOfMemory, failing unrelated tenants'
        // publishes with it. Pages arrive newest first, and the resume cursor is the latest
        // STORED record, so anything unpublished below an already-published page sits under
        // the cursor forever — a background catch-up never returns for it. Every page is
        // therefore still attempted after a publish failure, bounding holes to the failed
        // pages (the pre-streaming behaviour bounded them to failed batches); only an
        // explicit ranged re-pull (cursor reset / migration) heals them, so the sync is
        // reported failed either way.
        if (activeTypes.Contains(SyncDataType.Glucose))
        {
            try
            {
                var count = 0;
                DateTime? lastTime = null;
                var publishSuccess = true;

                await foreach (var page in FetchGlucosePagesAsync(request.From, request.To))
                {
                    count += page.Length;
                    var pageMax = page.Max(e => e.Date);
                    if (lastTime is null || pageMax > lastTime)
                        lastTime = pageMax;

                    if (!await PublishGlucoseDataInBatchesAsync(page, config, cancellationToken))
                        publishSuccess = false;
                }

                _logger.LogInformation(
                    "[{ConnectorSource}] Retrieved {Count} glucose entries from Nightscout",
                    ConnectorSource,
                    count);

                result.ItemsSynced[SyncDataType.Glucose] = count;
                if (lastTime.HasValue)
                    result.LastEntryTimes[SyncDataType.Glucose] = lastTime.Value;
                if (!publishSuccess)
                {
                    result.Success = false;
                    result.Errors.Add("Glucose publish failed");
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
                // Treatments track their own cursor (latest treatment, else 6-month initial
                // backfill) so historical boluses/carbs are filled even once glucose is current.
                var treatmentFrom = openEnded
                    ? await CalculateTreatmentSinceTimestampAsync(config)
                    : request.From;

                var count = 0;
                DateTime? lastTime = null;
                var publishSuccess = true;

                await foreach (var page in FetchTreatmentPagesAsync(treatmentFrom, request.To))
                {
                    count += page.Length;
                    var pageMax = page
                        .Select(t => DateTime.TryParse(t.CreatedAt, out var dt) ? dt : (DateTime?)null)
                        .Where(dt => dt.HasValue)
                        .Max();
                    if (pageMax.HasValue && (lastTime is null || pageMax > lastTime))
                        lastTime = pageMax;

                    if (!await PublishTreatmentDataInBatchesAsync(page, config, cancellationToken))
                        publishSuccess = false;
                }

                _logger.LogInformation(
                    "[{ConnectorSource}] Retrieved {Count} treatments from Nightscout",
                    ConnectorSource,
                    count);

                if (count > 0)
                {
                    // Report count under each enabled treatment sub-type
                    foreach (var tt in treatmentTypes.Where(t => activeTypes.Contains(t)))
                    {
                        result.ItemsSynced[tt] = count;
                        result.LastEntryTimes[tt] = lastTime;
                    }
                }

                if (!publishSuccess)
                {
                    result.Success = false;
                    result.Errors.Add("Treatments publish failed");
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
                // Device status tracks its own cursor when a watermark is available; if none
                // exists yet it falls back to request.From (current behaviour) rather than
                // re-fetching the full initial window of this high-volume telemetry every sync.
                var deviceStatusFrom = openEnded
                    ? await CalculateDeviceStatusCatchUpSinceAsync(config) ?? request.From
                    : request.From;

                var count = 0;
                DateTime? lastTime = null;
                var publishSuccess = true;

                await foreach (var page in FetchDeviceStatusPagesAsync(deviceStatusFrom, request.To))
                {
                    count += page.Length;
                    var pageMax = page
                        .Select(d => DateTimeOffset.TryParse(d.CreatedAt, out var dto) ? dto.UtcDateTime : (DateTime?)null)
                        .Where(dt => dt.HasValue)
                        .Max();
                    if (pageMax.HasValue && (lastTime is null || pageMax > lastTime))
                        lastTime = pageMax;

                    if (!await PublishDeviceStatusAsync(page, config, cancellationToken))
                        publishSuccess = false;
                }

                _logger.LogInformation(
                    "[{ConnectorSource}] Retrieved {Count} device statuses from Nightscout",
                    ConnectorSource,
                    count);

                result.ItemsSynced[SyncDataType.DeviceStatus] = count;
                if (lastTime.HasValue)
                    result.LastEntryTimes[SyncDataType.DeviceStatus] = lastTime;
                if (!publishSuccess)
                {
                    result.Success = false;
                    result.Errors.Add("DeviceStatus publish failed");
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
                // Activity tracks its own cursor when a watermark is available, else falls
                // back to request.From.
                var activityFrom = openEnded
                    ? await CalculateActivityCatchUpSinceAsync(config) ?? request.From
                    : request.From;

                var count = 0;
                DateTime? lastTime = null;
                var publishSuccess = true;

                await foreach (var page in FetchActivityPagesAsync(activityFrom, request.To))
                {
                    count += page.Length;
                    var pageMax = page
                        .Select(a => DateTimeOffset.TryParse(a.CreatedAt, out var dto) ? dto.UtcDateTime : (DateTime?)null)
                        .Where(dt => dt.HasValue)
                        .Max();
                    if (pageMax.HasValue && (lastTime is null || pageMax > lastTime))
                        lastTime = pageMax;

                    if (!await PublishActivityDataAsync(page, config, cancellationToken))
                        publishSuccess = false;
                }

                _logger.LogInformation(
                    "[{ConnectorSource}] Retrieved {Count} activities from Nightscout",
                    ConnectorSource,
                    count);

                result.ItemsSynced[SyncDataType.Activity] = count;
                if (lastTime.HasValue)
                    result.LastEntryTimes[SyncDataType.Activity] = lastTime;
                if (!publishSuccess)
                {
                    result.Success = false;
                    result.Errors.Add("Activity publish failed");
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

    /// <summary>
    ///     Upper bound for the first page of a paginated fetch. Nightscout applies an
    ///     implicit recency window (roughly the last four days) to any query carrying no
    ///     date filter at all, so a fully unbounded first page silently truncates a
    ///     full-history backfill — the short page then reads as end-of-history to the
    ///     pagination loop. Anchoring the bound to "now" keeps every request explicitly
    ///     dated; requests that already carry a bound pass through unchanged.
    /// </summary>
    private static DateTime? AnchorUnboundedFetch(DateTime? from, DateTime? to) =>
        from is null && to is null ? DateTime.UtcNow : to;

    /// <summary>
    ///     Streams a paginated Nightscout collection newest-first, one page per iteration,
    ///     so callers never hold more than a page of a multi-year history in memory. Each
    ///     full page steps the upper bound just below its oldest record; a short page is
    ///     the end of the range.
    /// </summary>
    /// <param name="from">Optional inclusive lower bound.</param>
    /// <param name="to">Optional inclusive upper bound; anchored to now when both bounds are open.</param>
    /// <param name="buildUrl">Builds the request URL for the given bounds.</param>
    /// <param name="oldestOf">Extracts the oldest record time from a page, or null when the page has no usable times.</param>
    /// <param name="operationName">Operation label for fetch logging.</param>
    private async IAsyncEnumerable<T[]> FetchPagesAsync<T>(
        DateTime? from,
        DateTime? to,
        Func<DateTime?, DateTime?, string> buildUrl,
        Func<T[], DateTime?> oldestOf,
        string operationName)
    {
        var currentTo = AnchorUnboundedFetch(from, to);

        while (true)
        {
            var page = await FetchDataAsync<T[]>(buildUrl(from, currentTo), operationName);

            if (page == null || page.Length == 0)
                yield break;

            yield return page;

            // Fewer than MaxCount means we've fetched everything in this range
            if (page.Length < _currentConfig.MaxCount)
                yield break;

            var oldestDate = oldestOf(page);
            if (!oldestDate.HasValue)
                yield break;

            // Avoid an infinite loop if the oldest date hasn't moved
            if (currentTo.HasValue && oldestDate.Value >= currentTo.Value)
                yield break;

            // Next page: records older than the oldest we've seen
            currentTo = oldestDate.Value.AddMilliseconds(-1);

            if (from.HasValue && currentTo < from)
                yield break;

            _logger.LogDebug(
                "[{ConnectorSource}] Paginating {Operation}, next page before {Before:yyyy-MM-dd HH:mm:ss}",
                ConnectorSource,
                operationName,
                currentTo);
        }
    }

    private static DateTime? OldestEntryTime(Entry[] page)
    {
        var oldestMs = page.Min(e => e.Mills);
        return oldestMs > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(oldestMs).UtcDateTime
            : null;
    }

    /// <summary>
    ///     Oldest created_at on a page. Uses DateTimeOffset for consistent UTC comparison
    ///     regardless of system timezone.
    /// </summary>
    private static DateTime? OldestCreatedAt<T>(T[] page, Func<T, string?> createdAtOf)
    {
        return page
            .Select(item => DateTimeOffset.TryParse(createdAtOf(item), out var dto) ? dto.UtcDateTime : (DateTime?)null)
            .Where(dt => dt.HasValue)
            .Min();
    }

    private async IAsyncEnumerable<Entry[]> FetchGlucosePagesAsync(DateTime? from, DateTime? to)
    {
        await foreach (var page in FetchPagesAsync<Entry>(
            from, to, BuildEntriesUrl, OldestEntryTime, "FetchGlucoseData"))
        {
            foreach (var entry in page)
                entry.DataSource = ConnectorSource;
            yield return page;
        }
    }

    private async IAsyncEnumerable<Treatment[]> FetchTreatmentPagesAsync(DateTime? from, DateTime? to)
    {
        await foreach (var page in FetchPagesAsync<Treatment>(
            from, to, BuildTreatmentsUrl, p => OldestCreatedAt(p, t => t.CreatedAt), "FetchTreatments"))
        {
            foreach (var treatment in page)
                treatment.DataSource = ConnectorSource;
            yield return page;
        }
    }

    protected override async Task<IEnumerable<Entry>> FetchGlucoseDataRangeAsync(
        DateTime? from, DateTime? to)
    {
        var allEntries = new List<Entry>();
        await foreach (var page in FetchGlucosePagesAsync(from, to))
            allEntries.AddRange(page);

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
        await foreach (var page in FetchTreatmentPagesAsync(from, to))
            allTreatments.AddRange(page);

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

    private IAsyncEnumerable<DeviceStatus[]> FetchDeviceStatusPagesAsync(DateTime? from, DateTime? to) =>
        FetchPagesAsync<DeviceStatus>(
            from, to, BuildDeviceStatusUrl, p => OldestCreatedAt(p, d => d.CreatedAt), "FetchDeviceStatus");

    private async Task<IEnumerable<Food>> FetchFoodAsync()
    {
        var foods = await FetchDataAsync<Food[]>(
            $"/api/v1/food.json?count={_currentConfig.MaxCount}",
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

    private IAsyncEnumerable<Activity[]> FetchActivityPagesAsync(DateTime? from, DateTime? to) =>
        FetchPagesAsync<Activity>(
            from, to, BuildActivityUrl, p => OldestCreatedAt(p, a => a.CreatedAt), "FetchActivity");

    private async Task<T?> FetchDataAsync<T>(string url, string operationName) where T : class
    {
        await _rateLimitingStrategy.ApplyDelayAsync(0);

        return await ExecuteWithRetryAsync(
            async () => await FetchDataCoreAsync<T>(url),
            _retryDelayStrategy,
            maxRetries: _currentConfig.MaxRetryAttempts,
            operationName: operationName);
    }

    private async Task<T?> FetchDataCoreAsync<T>(string url) where T : class
    {
        var headers = GetAuthHeaders();
        var absoluteUrl = _resolvedBaseUrl != null ? $"{_resolvedBaseUrl}{url}" : url;
        var response = await GetWithHeadersAsync(absoluteUrl, headers);

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
        var url = $"/api/v1/entries.json?count={_currentConfig.MaxCount}";

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
        var url = $"/api/v1/treatments.json?count={_currentConfig.MaxCount}";

        if (from.HasValue)
            url += $"&find[created_at][$gte]={from.Value.ToUniversalTime():o}";

        if (to.HasValue)
            url += $"&find[created_at][$lte]={to.Value.ToUniversalTime():o}";

        return url;
    }

    private string BuildDeviceStatusUrl(DateTime? from, DateTime? to)
    {
        var url = $"/api/v1/devicestatus.json?count={_currentConfig.MaxCount}";

        if (from.HasValue)
            url += $"&find[created_at][$gte]={from.Value.ToUniversalTime():o}";

        if (to.HasValue)
            url += $"&find[created_at][$lte]={to.Value.ToUniversalTime():o}";

        return url;
    }

    private string BuildActivityUrl(DateTime? from, DateTime? to)
    {
        var url = $"/api/v1/activity.json?count={_currentConfig.MaxCount}";

        if (from.HasValue)
            url += $"&find[created_at][$gte]={from.Value.ToUniversalTime():o}";

        if (to.HasValue)
            url += $"&find[created_at][$lte]={to.Value.ToUniversalTime():o}";

        return url;
    }

    /// <summary>
    /// Normalises a tenant-configured Nightscout URL: supplies <c>https://</c> when the tenant
    /// stored a bare host, and trims any trailing slash so callers can append paths directly.
    /// </summary>
    /// <param name="configUrl">The URL as stored in the tenant's connector configuration.</param>
    /// <exception cref="InvalidOperationException">The configured URL is null or empty.</exception>
    public static string ResolveBaseUrl(string configUrl)
    {
        if (string.IsNullOrEmpty(configUrl))
            throw new InvalidOperationException("Nightscout URL is not configured");

        var url = configUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? configUrl
            : $"https://{configUrl}";

        return url.TrimEnd('/');
    }

    private Dictionary<string, string> GetAuthHeaders()
    {
        return new Dictionary<string, string>
        {
            ["api-secret"] = _apiSecretHash ?? ComputeApiSecretHash(_currentConfig.ApiSecret)
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

/// <summary>
/// Nightscout connector service for syncing data from a Nightscout instance.
/// </summary>
public class NightscoutConnectorService : NightscoutConnectorServiceBase<NightscoutConnectorConfiguration>
{
    public NightscoutConnectorService(
        HttpClient httpClient,
        IConnectorServerResolver<NightscoutConnectorConfiguration> serverResolver,
        ILogger<NightscoutConnectorService> logger,
        IRetryDelayStrategy retryDelayStrategy,
        IRateLimitingStrategy rateLimitingStrategy,
        IConnectorRegistration<NightscoutConnectorConfiguration> registration,
        IConnectorPublisher? publisher = null
    ) : base(httpClient, serverResolver, logger, retryDelayStrategy, rateLimitingStrategy, registration, publisher) { }
}
