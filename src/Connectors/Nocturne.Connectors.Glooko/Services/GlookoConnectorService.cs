using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.Core.Utilities;
using Nocturne.Connectors.Glooko.Configurations;
using Nocturne.Connectors.Glooko.Mappers;
using Nocturne.Connectors.Glooko.Models;
using Nocturne.Connectors.Glooko.Utilities;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.Connectors.Glooko.Services;

/// <summary>
///     Connector service for Glooko data source.
///     Based on the original nightscout-connect Glooko implementation.
/// </summary>
public class GlookoConnectorService : BaseConnectorService<GlookoConnectorConfiguration>
{
    private readonly GlookoConnectorConfiguration _config;
    private readonly IConnectorPublisher? _connectorPublisher;
    private readonly IMealMatchingService? _mealMatchingService;
    private readonly IRateLimitingStrategy _rateLimitingStrategy;
    private readonly IRetryDelayStrategy _retryDelayStrategy;
    private readonly GlookoProfileMapper _profileMapper;
    private readonly GlookoSensorGlucoseMapper _sensorGlucoseMapper;
    private readonly GlookoStateSpanMapper _stateSpanMapper;
    private readonly GlookoSystemEventMapper _systemEventMapper;
    private readonly GlookoTempBasalMapper _tempBasalMapper;
    private readonly GlookoTimeMapper _timeMapper;
    private readonly GlookoAuthTokenProvider _tokenProvider;
    private readonly GlookoV4TreatmentMapper _v4TreatmentMapper;

    public GlookoConnectorService(
        HttpClient httpClient,
        IOptions<GlookoConnectorConfiguration> config,
        ILogger<GlookoConnectorService> logger,
        IRetryDelayStrategy retryDelayStrategy,
        IRateLimitingStrategy rateLimitingStrategy,
        GlookoAuthTokenProvider tokenProvider,
        IConnectorPublisher? publisher = null,
        IMealMatchingService? mealMatchingService = null
    )
        : base(httpClient, logger, publisher)
    {
        _config = config?.Value ?? throw new ArgumentNullException(nameof(config));
        _connectorPublisher = publisher;
        _mealMatchingService = mealMatchingService;
        _retryDelayStrategy = retryDelayStrategy ?? throw new ArgumentNullException(nameof(retryDelayStrategy));
        _rateLimitingStrategy = rateLimitingStrategy ?? throw new ArgumentNullException(nameof(rateLimitingStrategy));
        _tokenProvider = tokenProvider ?? throw new ArgumentNullException(nameof(tokenProvider));
        _timeMapper = new GlookoTimeMapper(_config, logger);
        _sensorGlucoseMapper = new GlookoSensorGlucoseMapper(_config, ConnectorSource, _timeMapper, logger);
        _v4TreatmentMapper = new GlookoV4TreatmentMapper(ConnectorSource, _timeMapper, logger);
        _stateSpanMapper = new GlookoStateSpanMapper(ConnectorSource, _timeMapper, logger);
        _tempBasalMapper = new GlookoTempBasalMapper(ConnectorSource, _timeMapper, logger);
        _systemEventMapper = new GlookoSystemEventMapper(ConnectorSource, _timeMapper, logger);
        _profileMapper = new GlookoProfileMapper(ConnectorSource, logger);
    }

    public override string ServiceName => "Glooko";
    protected override string ConnectorSource => DataSources.GlookoConnector;

    public override List<SyncDataType> SupportedDataTypes =>
    [
        SyncDataType.Glucose,
        SyncDataType.Boluses,
        SyncDataType.CarbIntake,
        SyncDataType.StateSpans,
        SyncDataType.DeviceEvents,
        SyncDataType.Profiles
    ];

    // ── Authentication ──────────────────────────────────────────────────

    public override async Task<bool> AuthenticateAsync()
    {
        var token = await _tokenProvider.GetValidTokenAsync();
        if (token == null)
        {
            TrackFailedRequest("Failed to get valid token");
            return false;
        }

        TrackSuccessfulRequest();
        return true;
    }

    /// <summary>
    ///     Validates that the session is active and the Glooko user code is available.
    ///     Throws <see cref="InvalidOperationException"/> if not authenticated.
    ///     Returns null and logs a warning if the user code is missing.
    /// </summary>
    private string? EnsureAuthenticatedAndGetCode()
    {
        if (string.IsNullOrEmpty(_tokenProvider.SessionCookie))
            throw new InvalidOperationException(
                "Not authenticated with Glooko. Call AuthenticateAsync first.");

        var code = _tokenProvider.UserData?.GlookoCode;
        if (code == null)
            _logger.LogWarning("Missing Glooko user code, cannot fetch data");

        return code;
    }

    private bool IsSessionExpired() => string.IsNullOrEmpty(_tokenProvider.SessionCookie);

    // ── HTTP helpers ────────────────────────────────────────────────────

    /// <summary>
    ///     Sends a GET request to a Glooko API endpoint with standard headers.
    ///     Relative paths are resolved against the configured server region.
    /// </summary>
    private async Task<JsonElement?> FetchFromGlookoEndpoint(string url)
    {
        var baseUrl = GlookoConstants.ResolveBaseUrl(_config.Server);
        var webOrigin = GlookoConstants.ResolveWebOrigin(_config.Server);
        var absoluteUrl = url.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? url
            : $"{baseUrl}{url}";

        _logger.LogDebug("GLOOKO FETCHER LOADING {Url}", absoluteUrl);

        var request = new HttpRequestMessage(HttpMethod.Get, absoluteUrl);
        GlookoHttpHelper.ApplyStandardHeaders(request, webOrigin, _tokenProvider.SessionCookie);

        var response = await _httpClient.SendAsync(request);

        if (response.IsSuccessStatusCode)
        {
            var json = await GlookoHttpHelper.ReadResponseAsync(response);
            _logger.LogDebug("[{ConnectorSource}] Response {StatusCode} from {Url}: {Json}",
                ConnectorSource, (int)response.StatusCode, absoluteUrl, json);
            return JsonSerializer.Deserialize<JsonElement>(json);
        }

        if (response.StatusCode == HttpStatusCode.UnprocessableEntity)
        {
            _logger.LogWarning("Rate limited (422) fetching from {Url}", absoluteUrl);
            throw new HttpRequestException("422 UnprocessableEntity - Rate limited");
        }

        _logger.LogWarning("Failed to fetch from {Url}: {StatusCode}", absoluteUrl, response.StatusCode);
        throw new HttpRequestException($"HTTP {(int)response.StatusCode} {response.StatusCode}");
    }

    /// <summary>
    ///     Fetches from a Glooko endpoint with retry logic and exponential backoff.
    /// </summary>
    private async Task<JsonElement?> FetchFromGlookoEndpointWithRetry(string url, int maxRetries = 3)
    {
        HttpRequestException? lastException = null;

        for (var attempt = 0; attempt < maxRetries; attempt++)
        {
            try
            {
                var result = await FetchFromGlookoEndpoint(url);
                if (result.HasValue) return result;

                _logger.LogWarning("Attempt {AttemptNumber} failed for {Url}", attempt + 1, url);
            }
            catch (HttpRequestException ex) when (ex.Message.Contains("422"))
            {
                lastException = ex;
                _logger.LogWarning("Rate limited (422) on attempt {AttemptNumber} for {Url}", attempt + 1, url);
            }
            catch (HttpRequestException ex)
            {
                lastException = ex;
                _logger.LogError(ex, "Attempt {AttemptNumber} failed for {Url}", attempt + 1, url);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Attempt {AttemptNumber} failed for {Url}", attempt + 1, url);
                lastException = new HttpRequestException($"Request failed: {ex.Message}", ex);
            }

            if (attempt < maxRetries - 1)
            {
                _logger.LogInformation("Applying retry backoff before retry {RetryNumber}", attempt + 2);
                await _retryDelayStrategy.ApplyRetryDelayAsync(attempt);
            }
        }

        _logger.LogError("All {MaxRetries} attempts failed for {Url}", maxRetries, url);
        if (lastException != null) throw lastException;
        throw new HttpRequestException($"All {maxRetries} attempts failed for {url}");
    }

    // ── URL construction ────────────────────────────────────────────────

    private string ConstructV2Url(string endpoint, DateTime startDate, DateTime endDate)
    {
        var patientCode = _tokenProvider.UserData?.GlookoCode;
        var maxCount = Math.Max(1, (int)Math.Ceiling((endDate - startDate).TotalMinutes / 5));

        return $"{endpoint}?patient={patientCode}"
             + $"&startDate={startDate:yyyy-MM-ddTHH:mm:ss.fffZ}"
             + $"&endDate={endDate:yyyy-MM-ddTHH:mm:ss.fffZ}"
             + $"&lastGuid={GlookoConstants.LegacyLastGuid}"
             + $"&lastUpdatedAt={startDate:yyyy-MM-ddTHH:mm:ss.fffZ}"
             + $"&limit={maxCount}";
    }

    private string ConstructV3GraphUrl(DateTime startDate, DateTime endDate)
    {
        var patientCode = _tokenProvider.UserData?.GlookoCode;

        var series = _config.V3IncludeCgmBackfill
            ? GlookoConstants.V3GraphSeries.Concat(GlookoConstants.V3CgmBackfillSeries)
            : GlookoConstants.V3GraphSeries;

        var seriesParams = string.Join("&", series.Select(s => $"series[]={s}"));

        return $"{GlookoConstants.V3GraphDataPath}?patient={patientCode}"
             + $"&startDate={startDate:yyyy-MM-ddTHH:mm:ss.fffZ}"
             + $"&endDate={endDate:yyyy-MM-ddTHH:mm:ss.fffZ}"
             + $"&{seriesParams}"
             + "&locale=en&insulinTooltips=false&filterBgReadings=false&splitByDay=false";
    }

    // ── Sync orchestration ──────────────────────────────────────────────

    protected override async Task<SyncResult> PerformSyncInternalAsync(
        SyncRequest request,
        GlookoConnectorConfiguration config,
        CancellationToken cancellationToken,
        ISyncProgressReporter? progressReporter = null
    )
    {
        var result = new SyncResult
        {
            Success = true,
            Message = "Sync completed successfully",
            StartTime = DateTime.UtcNow
        };

        try
        {
            await ReportMessageAsync(progressReporter, SyncMessageType.Authenticating, null, cancellationToken);

            if (IsSessionExpired())
                if (!await AuthenticateAsync())
                {
                    result.Success = false;
                    result.Message = "Authentication failed";
                    result.Errors.Add("Authentication failed");
                    return result;
                }

            if (!request.DataTypes.Any())
                request.DataTypes = SupportedDataTypes;
            var enabledTypes = config.GetEnabledDataTypes(SupportedDataTypes);
            var activeTypes = request.DataTypes.Where(t => enabledTypes.Contains(t)).ToHashSet();

            var from = request.From.HasValue
                ? _timeMapper.ToGlookoTime(request.From.Value)
                : _timeMapper.ToGlookoTime(DateTime.UtcNow.AddMonths(-6));
            var to = _timeMapper.ToGlookoTime(DateTime.UtcNow);

            var chunks = DateChunker.Chunk(from, to, TimeSpan.FromDays(14)).ToList();

            _logger.LogInformation(
                "[{ConnectorSource}] Syncing {From:yyyy-MM-dd} to {To:yyyy-MM-dd} in {ChunkCount} chunk(s)",
                ConnectorSource, from, to, chunks.Count);

            for (var i = 0; i < chunks.Count; i++)
            {
                var (chunkFrom, chunkTo) = chunks[i];

                await ReportMessageAsync(progressReporter, SyncMessageType.FetchingData,
                    new()
                    {
                        ["from"] = chunkFrom.ToString("MMM dd"),
                        ["to"] = chunkTo.ToString("MMM dd"),
                        ["chunk"] = $"{i + 1}/{chunks.Count}",
                    },
                    cancellationToken);

                var chunkSuccess = _config.UseV3Api
                    ? await FetchAndMapViaV3Async(chunkFrom, chunkTo, activeTypes, result, config, cancellationToken)
                    : await FetchAndMapViaV2Async(chunkFrom, chunkTo, activeTypes, result, config, cancellationToken);

                if (!chunkSuccess)
                {
                    _logger.LogWarning(
                        "[{ConnectorSource}] Chunk {Chunk}/{Total} ({From:yyyy-MM-dd} to {To:yyyy-MM-dd}) failed, stopping sync",
                        ConnectorSource, i + 1, chunks.Count, chunkFrom, chunkTo);
                    result.Success = false;
                    result.Message = "Sync failed during data fetch";
                    result.Errors.Add($"Chunk {i + 1}/{chunks.Count} failed ({chunkFrom:yyyy-MM-dd} to {chunkTo:yyyy-MM-dd})");
                    break;
                }

                _logger.LogInformation(
                    "[{ConnectorSource}] Completed chunk {Chunk}/{Total} ({From:yyyy-MM-dd} to {To:yyyy-MM-dd})",
                    ConnectorSource, i + 1, chunks.Count, chunkFrom, chunkTo);
            }

            // Profiles (V3 device settings — used in both modes, no V2 equivalent)
            await ReportMessageAsync(progressReporter, SyncMessageType.ProcessingDataType,
                new() { ["dataType"] = SyncDataType.Profiles.ToString() }, cancellationToken);

            if (activeTypes.Contains(SyncDataType.Profiles))
            {
                try
                {
                    var deviceSettings = await FetchV3DeviceSettingsAsync();
                    if (deviceSettings != null)
                    {
                        var profiles = _profileMapper.TransformDeviceSettingsToProfiles(deviceSettings);
                        if (profiles.Any() && await PublishProfileDataAsync(profiles, config, cancellationToken))
                        {
                            result.ItemsSynced[SyncDataType.Profiles] = profiles.Count;
                            _logger.LogInformation("[{ConnectorSource}] Published {Count} profiles from device settings",
                                ConnectorSource, profiles.Count);
                        }
                    }
                }
                catch (Exception profileEx)
                {
                    _logger.LogWarning(profileEx, "[{ConnectorSource}] Failed to fetch/publish profile data", ConnectorSource);
                }
            }

            await ReportMessageAsync(progressReporter,
                result.Success ? SyncMessageType.SyncComplete : SyncMessageType.SyncFailed,
                null, cancellationToken);

            result.EndTime = DateTime.UtcNow;
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during Glooko batch sync");
            result.Success = false;
            result.Message = "Sync failed with exception";
            result.Errors.Add(ex.Message);
            await ReportMessageAsync(progressReporter, SyncMessageType.SyncFailed, null, cancellationToken);
            result.EndTime = DateTime.UtcNow;
            return result;
        }
    }

    // ── V2 fetch + map ──────────────────────────────────────────────────

    /// <summary>
    ///     Fetches from all V2 endpoints, maps each record type, and publishes inline.
    /// </summary>
    private async Task<bool> FetchAndMapViaV2Async(
        DateTime fromDate,
        DateTime toDate,
        HashSet<SyncDataType> activeTypes,
        SyncResult result,
        GlookoConnectorConfiguration config,
        CancellationToken cancellationToken)
    {
        var batchData = await FetchBatchDataAsync(fromDate, toDate);
        if (batchData == null) return false;

        // 1. Glucose
        var sensorGlucose = _sensorGlucoseMapper.TransformBatchDataToSensorGlucose(batchData).ToList();
        await PublishRecordTypeAsync(result, SyncDataType.Glucose, activeTypes,
            sensorGlucose, PublishSensorGlucoseDataAsync, config, cancellationToken);
        UpdateLastEntryTime(result, SyncDataType.Glucose, sensorGlucose);

        var bgChecks = _sensorGlucoseMapper.TransformBatchDataToBGChecks(batchData).ToList();
        await PublishRecordTypeAsync(result, SyncDataType.ManualBG, activeTypes,
            bgChecks, PublishBGCheckDataAsync, config, cancellationToken);

        // 2. Treatments (FK order: batches → boluses → carbs+foods)
        var (boluses, carbs, batches) = _v4TreatmentMapper.MapBatchData(batchData);

        if (batches.Count > 0)
            await PublishDecompositionBatchesAsync(batches, config, cancellationToken);

        await PublishRecordTypeAsync(result, SyncDataType.Boluses, activeTypes,
            boluses, PublishBolusDataAsync, config, cancellationToken);

        await PublishRecordTypeAsync(result, SyncDataType.CarbIntake, activeTypes,
            carbs, PublishCarbIntakeDataAsync, config, cancellationToken);

        // 3. Foods + attribution (coupled with carbs)
        var foodEntryImports = batchData.Foods is { Length: > 0 }
            ? _v4TreatmentMapper.MapFoodsToConnectorEntries(batchData) : [];
        Func<string, string?> foodResolver = externalEntryId => $"glooko_food_{externalEntryId}";
        await PublishFoodEntriesAndAttributeAsync(
            foodEntryImports, carbs, foodResolver, config, cancellationToken);

        // 4. State spans + temp basals (old code only counted temp basals in ItemsSynced)
        if (activeTypes.Contains(SyncDataType.StateSpans))
        {
            var stateSpans = _stateSpanMapper.TransformV2ToStateSpans(batchData);
            if (stateSpans.Count > 0)
                await PublishStateSpanDataAsync(stateSpans, config, cancellationToken);

            var tempBasals = _tempBasalMapper.TransformV2ToTempBasals(batchData);
            if (tempBasals.Count > 0 && await PublishTempBasalDataAsync(tempBasals, config, cancellationToken))
                result.ItemsSynced[SyncDataType.StateSpans] = tempBasals.Count;
        }

        return true;
    }

    // ── V3 fetch + map ──────────────────────────────────────────────────

    /// <summary>
    ///     Fetches from V3 graph/data and histories endpoints, maps each record type, and publishes inline.
    /// </summary>
    private async Task<bool> FetchAndMapViaV3Async(
        DateTime fromDate,
        DateTime toDate,
        HashSet<SyncDataType> activeTypes,
        SyncResult result,
        GlookoConnectorConfiguration config,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("[{ConnectorSource}] Fetching data from v3 API...", ConnectorSource);

        var v3Data = await FetchV3GraphDataAsync(fromDate, toDate);
        if (v3Data == null) return false;

        GlookoV3HistoriesResponse? v3Histories = null;
        try { v3Histories = await FetchV3HistoriesAsync(fromDate, toDate); }
        catch (Exception histEx)
        {
            _logger.LogWarning(histEx, "[{ConnectorSource}] V3 histories fetch failed, meal data will be unavailable", ConnectorSource);
        }

        // 1. Glucose
        if (_config.V3IncludeCgmBackfill)
        {
            var sensorGlucose = _sensorGlucoseMapper.TransformV3ToSensorGlucose(v3Data, _meterUnits).ToList();
            await PublishRecordTypeAsync(result, SyncDataType.Glucose, activeTypes,
                sensorGlucose, PublishSensorGlucoseDataAsync, config, cancellationToken);
            UpdateLastEntryTime(result, SyncDataType.Glucose, sensorGlucose);
        }

        var bgChecks = _sensorGlucoseMapper.TransformV3ToBGChecks(v3Data, _meterUnits).ToList();
        await PublishRecordTypeAsync(result, SyncDataType.ManualBG, activeTypes,
            bgChecks, PublishBGCheckDataAsync, config, cancellationToken);

        // 2. Treatments (FK order: batches → boluses → carbs+foods)
        var (v3Boluses, v3BolusCarbIntakes, v3Batches) = _v4TreatmentMapper.MapV3Boluses(v3Data);

        // Carbs: bolus wizard + history meals (preferred) or carbAll (fallback)
        var allCarbs = new List<CarbIntake>(v3BolusCarbIntakes);
        var historyMealCarbs = v3Histories?.Histories != null
            ? _v4TreatmentMapper.MapV3HistoryMealsToCarbIntakes(v3Histories) : [];

        if (historyMealCarbs.Count > 0)
            allCarbs.AddRange(historyMealCarbs);
        else
            allCarbs.AddRange(_v4TreatmentMapper.MapV3CarbAll(v3Data));

        if (v3Batches.Count > 0)
            await PublishDecompositionBatchesAsync(v3Batches, config, cancellationToken);

        await PublishRecordTypeAsync(result, SyncDataType.Boluses, activeTypes,
            v3Boluses, PublishBolusDataAsync, config, cancellationToken);

        await PublishRecordTypeAsync(result, SyncDataType.CarbIntake, activeTypes,
            allCarbs, PublishCarbIntakeDataAsync, config, cancellationToken);

        // 3. Foods + attribution (coupled with carbs)
        GlookoFood[]? v2Foods = null;
        if (historyMealCarbs.Count > 0)
        {
            try { v2Foods = await FetchV2FoodsAsync(fromDate, toDate); }
            catch (Exception v2Ex)
            {
                _logger.LogWarning(v2Ex, "[{ConnectorSource}] V2 foods fetch failed, food entries will lack externalId/brand metadata", ConnectorSource);
            }
        }

        var foodEntryImports = historyMealCarbs.Count > 0 && v3Histories?.Histories != null
            ? _v4TreatmentMapper.MapV3HistoryMealsToConnectorEntries(v3Histories, v2Foods) : [];

        // Build food resolver
        Func<string, string?>? foodResolver = null;
        if (historyMealCarbs.Count > 0 && v3Histories?.Histories != null)
        {
            var foodGuidToMealGuid = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var meal in GlookoV4TreatmentMapper.ExtractMeals(v3Histories))
            {
                if (meal.SoftDeleted == true || string.IsNullOrEmpty(meal.Guid) || meal.Foods == null) continue;
                foreach (var food in meal.Foods)
                {
                    if (food.SoftDeleted != true && !string.IsNullOrEmpty(food.Guid))
                        foodGuidToMealGuid.TryAdd(food.Guid, meal.Guid!);
                }
            }

            foodResolver = externalEntryId =>
                foodGuidToMealGuid.TryGetValue(externalEntryId, out var mealGuid)
                    ? $"glooko_v3meal_{mealGuid}" : null;
        }

        await PublishFoodEntriesAndAttributeAsync(
            foodEntryImports, allCarbs, foodResolver, config, cancellationToken);

        // 4. State spans + temp basals (old code only counted temp basals in ItemsSynced)
        if (activeTypes.Contains(SyncDataType.StateSpans))
        {
            var stateSpans = _stateSpanMapper.TransformV3ToStateSpans(v3Data);
            if (stateSpans.Count > 0)
                await PublishStateSpanDataAsync(stateSpans, config, cancellationToken);

            var tempBasals = _tempBasalMapper.TransformV3ToTempBasals(v3Data);
            if (tempBasals.Count > 0 && await PublishTempBasalDataAsync(tempBasals, config, cancellationToken))
                result.ItemsSynced[SyncDataType.StateSpans] = tempBasals.Count;
        }

        // 5. Device events + system events (summed into single ItemsSynced entry)
        if (activeTypes.Contains(SyncDataType.DeviceEvents))
        {
            var deviceEventCount = 0;

            var deviceEvents = _v4TreatmentMapper.MapV3DeviceEvents(v3Data);
            if (deviceEvents.Count > 0 && await PublishDeviceEventDataAsync(deviceEvents, config, cancellationToken))
                deviceEventCount += deviceEvents.Count;

            var systemEvents = _systemEventMapper.TransformV3ToSystemEvents(v3Data);
            if (systemEvents.Count > 0 && await PublishSystemEventDataAsync(systemEvents, config, cancellationToken))
                deviceEventCount += systemEvents.Count;

            if (deviceEventCount > 0)
                result.ItemsSynced[SyncDataType.DeviceEvents] = deviceEventCount;
        }

        return true;
    }

    // ── Food attribution helper ────────────────────────────────────────

    /// <summary>
    ///     Publishes food catalog entries and attributes them to carb intakes via the meal matching service.
    /// </summary>
    private async Task PublishFoodEntriesAndAttributeAsync(
        List<ConnectorFoodEntryImport> foodEntryImports,
        List<CarbIntake> carbIntakes,
        Func<string, string?>? foodEntryToCarbLegacyId,
        GlookoConnectorConfiguration config,
        CancellationToken cancellationToken)
    {
        if (foodEntryImports.Count == 0 || _connectorPublisher is not { IsAvailable: true })
            return;

        var importedEntries = await _connectorPublisher.Metadata.PublishConnectorFoodEntriesAsync(
            foodEntryImports, ConnectorSource, cancellationToken);

        if (importedEntries is not { Count: > 0 })
            return;

        _logger.LogInformation("[{ConnectorSource}] Published {Count} food entries to connector food catalog",
            ConnectorSource, importedEntries.Count);

        if (_mealMatchingService == null || carbIntakes.Count == 0 || foodEntryToCarbLegacyId == null)
            return;

        var pendingEntries = importedEntries
            .Where(e => e.Status == ConnectorFoodEntryStatus.Pending)
            .ToList();

        if (pendingEntries.Count == 0) return;

        var carbsByLegacyId = carbIntakes
            .Where(ci => ci.LegacyId != null)
            .ToDictionary(ci => ci.LegacyId!, StringComparer.OrdinalIgnoreCase);

        var attributedCount = 0;

        foreach (var entry in pendingEntries)
        {
            var legacyKey = foodEntryToCarbLegacyId(entry.ExternalEntryId);
            if (legacyKey == null || !carbsByLegacyId.TryGetValue(legacyKey, out var carbIntake))
                continue;

            try
            {
                await _mealMatchingService.AcceptMatchAsync(
                    entry.Id, carbIntake.Id, entry.Carbs, timeOffsetMinutes: 0, cancellationToken);
                attributedCount++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "[{ConnectorSource}] Failed to attribute food entry {FoodEntryId} to CarbIntake {CarbIntakeId}",
                    ConnectorSource, entry.Id, carbIntake.Id);
            }
        }

        _logger.LogInformation("[{ConnectorSource}] Attributed {Count}/{Total} food entries to carb intakes",
            ConnectorSource, attributedCount, pendingEntries.Count);
    }

    /// <summary>
    ///     Updates <see cref="SyncResult.LastEntryTimes"/> with the most recent glucose timestamp,
    ///     keeping the max across multiple chunks.
    /// </summary>
    private static void UpdateLastEntryTime(SyncResult result, SyncDataType dataType, List<SensorGlucose> records)
    {
        if (records.Count == 0) return;
        var maxTime = DateTimeOffset.FromUnixTimeMilliseconds(records.Max(s => s.Mills)).UtcDateTime;
        if (!result.LastEntryTimes.TryGetValue(dataType, out var existing) || maxTime > existing)
            result.LastEntryTimes[dataType] = maxTime;
    }

    // ── V2 batch data fetching ──────────────────────────────────────────

    /// <summary>
    ///     Fetches comprehensive batch data from all v2 Glooko endpoints.
    /// </summary>
    public async Task<GlookoBatchData?> FetchBatchDataAsync(DateTime fromDate, DateTime toDate)
    {
        try
        {
            var patientCode = EnsureAuthenticatedAndGetCode();
            if (patientCode == null) return null;

            _logger.LogInformation("Fetching comprehensive Glooko data from {From:yyyy-MM-dd} to {To:yyyy-MM-dd}", fromDate, toDate);

            var batchData = new GlookoBatchData();

            var endpointDefinitions = new (string Endpoint, Action<JsonElement> Handler)[]
            {
                (GlookoConstants.FoodsPath, json =>
                {
                    if (json.TryGetProperty("foods", out var el))
                        batchData.Foods = JsonSerializer.Deserialize<GlookoFood[]>(el.GetRawText()) ?? [];
                }),
                (GlookoConstants.ScheduledBasalsPath, json =>
                {
                    if (json.TryGetProperty("scheduledBasals", out var el))
                        batchData.ScheduledBasals = JsonSerializer.Deserialize<GlookoBasal[]>(el.GetRawText()) ?? [];
                }),
                (GlookoConstants.NormalBolusesPath, json =>
                {
                    if (json.TryGetProperty("normalBoluses", out var el))
                        batchData.NormalBoluses = JsonSerializer.Deserialize<GlookoBolus[]>(el.GetRawText()) ?? [];
                }),
                (GlookoConstants.CgmReadingsPath, json =>
                {
                    if (json.TryGetProperty("readings", out var el))
                        batchData.Readings = JsonSerializer.Deserialize<GlookoCgmReading[]>(el.GetRawText()) ?? [];
                }),
                (GlookoConstants.MeterReadingsPath, json =>
                {
                    if (json.TryGetProperty("readings", out var el))
                        batchData.MeterReadings = JsonSerializer.Deserialize<GlookoMeterReading[]>(el.GetRawText()) ?? [];
                }),
                (GlookoConstants.SuspendBasalsPath, json =>
                {
                    if (json.TryGetProperty("suspendBasals", out var el))
                        batchData.SuspendBasals = JsonSerializer.Deserialize<GlookoSuspendBasal[]>(el.GetRawText()) ?? [];
                }),
                (GlookoConstants.TemporaryBasalsPath, json =>
                {
                    if (json.TryGetProperty("temporaryBasals", out var el))
                        batchData.TempBasals = JsonSerializer.Deserialize<GlookoTempBasal[]>(el.GetRawText()) ?? [];
                }),
            };

            for (var i = 0; i < endpointDefinitions.Length; i++)
            {
                var (endpoint, handler) = endpointDefinitions[i];
                var url = ConstructV2Url(endpoint, fromDate, toDate);

                await _rateLimitingStrategy.ApplyDelayAsync(i);

                try
                {
                    var fetchResult = await FetchFromGlookoEndpointWithRetry(url);
                    if (fetchResult.HasValue)
                    {
                        try { handler(fetchResult.Value); }
                        catch (Exception ex) { _logger.LogWarning(ex, "Error parsing data from {Endpoint}", endpoint); }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to fetch from {Endpoint}. Continuing with other endpoints.", endpoint);
                }
            }

            _logger.LogInformation(
                "[{ConnectorSource}] Fetched Glooko batch data summary: "
                + "Readings={ReadingsCount}, MeterReadings={MeterReadingsCount}, Foods={FoodsCount}, "
                + "NormalBoluses={BolusCount}, TempBasals={TempBasalCount}, "
                + "ScheduledBasals={ScheduledBasalCount}, Suspends={SuspendCount}",
                ConnectorSource,
                batchData.Readings?.Length ?? 0,
                batchData.MeterReadings?.Length ?? 0,
                batchData.Foods?.Length ?? 0,
                batchData.NormalBoluses?.Length ?? 0,
                batchData.TempBasals?.Length ?? 0,
                batchData.ScheduledBasals?.Length ?? 0,
                batchData.SuspendBasals?.Length ?? 0);

            return batchData;
        }
        catch (InvalidOperationException) { throw; }
        catch (HttpRequestException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching Glooko batch data");
            return null;
        }
    }

    // ── V3 data fetching ────────────────────────────────────────────────

    /// <summary>
    ///     Fetches only the V2 foods endpoint. Used by the V3 sync path to get
    ///     rich food metadata (externalId, brand) that V3 histories doesn't provide.
    /// </summary>
    public async Task<GlookoFood[]?> FetchV2FoodsAsync(DateTime fromDate, DateTime toDate)
    {
        try
        {
            var patientCode = EnsureAuthenticatedAndGetCode();
            if (patientCode == null) return null;

            var url = ConstructV2Url(GlookoConstants.FoodsPath, fromDate, toDate);
            var result = await FetchFromGlookoEndpointWithRetry(url);
            if (!result.HasValue) return null;

            if (result.Value.TryGetProperty("foods", out var el))
            {
                var foods = JsonSerializer.Deserialize<GlookoFood[]>(el.GetRawText()) ?? [];
                _logger.LogInformation("[{ConnectorSource}] Fetched {Count} V2 food records for metadata enrichment",
                    ConnectorSource, foods.Length);
                return foods;
            }

            return [];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[{ConnectorSource}] Failed to fetch V2 foods for metadata enrichment", ConnectorSource);
            return null;
        }
    }

    private string? _meterUnits;

    /// <summary>
    ///     Fetches user profile from v3 API to get meter units setting.
    /// </summary>
    public async Task<GlookoV3UsersResponse?> FetchV3UserProfileAsync()
    {
        try
        {
            EnsureAuthenticatedAndGetCode();

            var result = await FetchFromGlookoEndpoint(GlookoConstants.V3UsersPath);
            if (!result.HasValue) return null;

            var profile = JsonSerializer.Deserialize<GlookoV3UsersResponse>(result.Value.GetRawText());
            if (profile?.CurrentUser != null)
            {
                _meterUnits = profile.CurrentUser.MeterUnits;
                _logger.LogInformation("[{ConnectorSource}] User profile loaded. MeterUnits: {Units}",
                    ConnectorSource, _meterUnits);
            }

            return profile;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching Glooko v3 user profile");
            return null;
        }
    }

    /// <summary>
    ///     Fetches data from v3 graph/data API — single call for all data types.
    /// </summary>
    public async Task<GlookoV3GraphResponse?> FetchV3GraphDataAsync(DateTime fromDate, DateTime toDate)
    {
        try
        {
            var patientCode = EnsureAuthenticatedAndGetCode();
            if (patientCode == null) return null;

            if (string.IsNullOrEmpty(_meterUnits)) await FetchV3UserProfileAsync();

            var url = ConstructV3GraphUrl(fromDate, toDate);
            _logger.LogInformation("[{ConnectorSource}] Fetching v3 graph data from {StartDate:yyyy-MM-dd} to {EndDate:yyyy-MM-dd}",
                ConnectorSource, fromDate, toDate);

            var result = await FetchFromGlookoEndpointWithRetry(url);
            if (!result.HasValue) return null;

            var graphData = JsonSerializer.Deserialize<GlookoV3GraphResponse>(result.Value.GetRawText());

            if (graphData?.Series != null)
            {
                var s = graphData.Series;
                _logger.LogInformation(
                    "[{ConnectorSource}] Fetched v3 graph data: "
                    + "Cgm={Cgm}, Bg={Bg}, "
                    + "DeliveredBolus={DeliveredBolus}, AutomaticBolus={AutoBolus}, InjectionBolus={InjectionBolus}, "
                    + "GkInsulinBasal={GkBasal}, GkInsulinBolus={GkBolus}, "
                    + "CarbAll={Carbs}, "
                    + "ScheduledBasal={SchedBasal}, TemporaryBasal={TempBasal}, SuspendBasal={Suspend}, LgsPlgs={LgsPlgs}, "
                    + "PumpAlarm={Alarms}, ReservoirChange={Reservoir}, SetSiteChange={SetSite}, ProfileChange={Profile}",
                    ConnectorSource,
                    (s.CgmHigh?.Length ?? 0) + (s.CgmNormal?.Length ?? 0) + (s.CgmLow?.Length ?? 0),
                    (s.BgHigh?.Length ?? 0) + (s.BgNormal?.Length ?? 0) + (s.BgLow?.Length ?? 0),
                    s.DeliveredBolus?.Length ?? 0,
                    s.AutomaticBolus?.Length ?? 0,
                    s.InjectionBolus?.Length ?? 0,
                    s.GkInsulinBasal?.Length ?? 0,
                    s.GkInsulinBolus?.Length ?? 0,
                    s.CarbAll?.Length ?? 0,
                    s.ScheduledBasal?.Length ?? 0,
                    s.TemporaryBasal?.Length ?? 0,
                    s.SuspendBasal?.Length ?? 0,
                    s.LgsPlgs?.Length ?? 0,
                    s.PumpAlarm?.Length ?? 0,
                    s.ReservoirChange?.Length ?? 0,
                    s.SetSiteChange?.Length ?? 0,
                    s.ProfileChange?.Length ?? 0);
            }

            return graphData;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching Glooko v3 graph data");
            return null;
        }
    }

    /// <summary>
    ///     Fetches pump device settings from the v3 devices_and_settings API.
    /// </summary>
    public async Task<GlookoV3DeviceSettingsResponse?> FetchV3DeviceSettingsAsync()
    {
        try
        {
            var patientCode = EnsureAuthenticatedAndGetCode();
            if (patientCode == null) return null;

            var url = $"{GlookoConstants.V3DeviceSettingsPath}?patient={patientCode}";
            _logger.LogInformation("[{ConnectorSource}] Fetching device settings from v3 API", ConnectorSource);

            var result = await FetchFromGlookoEndpointWithRetry(url);
            if (!result.HasValue) return null;

            var settings = JsonSerializer.Deserialize<GlookoV3DeviceSettingsResponse>(result.Value.GetRawText());

            var pumpCount = settings?.DeviceSettings?.Pumps?.Count ?? 0;
            var snapshotCount = settings?.DeviceSettings?.Pumps?.Values.Sum(p => p.Count) ?? 0;

            _logger.LogInformation("[{ConnectorSource}] Fetched device settings: {PumpCount} pumps, {SnapshotCount} settings snapshots",
                ConnectorSource, pumpCount, snapshotCount);

            return settings;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching Glooko v3 device settings");
            return null;
        }
    }

    /// <summary>
    ///     Fetches rich history data from the v3 users/summary/histories API.
    ///     Contains meals with per-food nutritional data, medications, exercises, etc.
    /// </summary>
    public async Task<GlookoV3HistoriesResponse?> FetchV3HistoriesAsync(DateTime fromDate, DateTime toDate)
    {
        try
        {
            var patientCode = EnsureAuthenticatedAndGetCode();
            if (patientCode == null) return null;

            var url = $"{GlookoConstants.V3HistoriesPath}?patient={patientCode}"
                    + $"&startDate={fromDate:yyyy-MM-ddTHH:mm:ss.fffZ}"
                    + $"&endDate={toDate:yyyy-MM-ddTHH:mm:ss.fffZ}";

            _logger.LogInformation("[{ConnectorSource}] Fetching v3 histories from {StartDate:yyyy-MM-dd} to {EndDate:yyyy-MM-dd}",
                ConnectorSource, fromDate, toDate);

            var result = await FetchFromGlookoEndpointWithRetry(url);
            if (!result.HasValue) return null;

            var historiesData = JsonSerializer.Deserialize<GlookoV3HistoriesResponse>(result.Value.GetRawText());

            var entryCount = historiesData?.Histories?.Length ?? 0;
            var meals = GlookoV4TreatmentMapper.ExtractMeals(historiesData!).ToList();
            var mealCount = meals.Count;
            var foodCount = meals.Sum(m => m.Foods?.Length ?? 0);
            var mealsWithCarbs = meals.Count(m => (m.Carbs ?? 0) > 0);

            _logger.LogInformation(
                "[{ConnectorSource}] Fetched v3 histories: {EntryCount} entries, {MealCount} meals ({MealsWithCarbs} with carbs), {FoodCount} food items",
                ConnectorSource, entryCount, mealCount, mealsWithCarbs, foodCount);

            return historiesData;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching Glooko v3 histories");
            return null;
        }
    }

    // ── Progress reporting ──────────────────────────────────────────────

    private Task ReportMessageAsync(
        ISyncProgressReporter? reporter,
        SyncMessageType messageType,
        Dictionary<string, string>? messageParams,
        CancellationToken ct)
    {
        if (reporter == null) return Task.CompletedTask;
        return reporter.ReportProgressAsync(new SyncProgressEvent
        {
            ConnectorId = ConnectorSource,
            ConnectorName = ServiceName,
            Phase = SyncPhase.Syncing,
            MessageType = messageType,
            MessageParams = messageParams,
        }, ct);
    }
}
