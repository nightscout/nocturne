using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Services;
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
///     All mapped data produced by a V2 or V3 fetch-and-transform pass.
///     Populated by <see cref="GlookoConnectorService.FetchAndMapViaV2Async"/> or
///     <see cref="GlookoConnectorService.FetchAndMapViaV3Async"/>, then consumed
///     by the shared <see cref="GlookoConnectorService.PublishAllAsync"/> pipeline.
/// </summary>
internal sealed class GlookoSyncData
{
    public List<SensorGlucose> SensorGlucose { get; init; } = [];
    public List<BGCheck> BgChecks { get; init; } = [];
    public List<Bolus> Boluses { get; init; } = [];
    public List<CarbIntake> CarbIntakes { get; init; } = [];
    public List<DecompositionBatch> Batches { get; init; } = [];
    public List<StateSpan> StateSpans { get; init; } = [];
    public List<TempBasal> TempBasals { get; init; } = [];
    public List<DeviceEvent> DeviceEvents { get; init; } = [];
    public List<SystemEvent> SystemEvents { get; init; } = [];
    public List<ConnectorFoodEntryImport> FoodEntryImports { get; init; } = [];

    /// <summary>
    ///     Resolves a food entry's ExternalEntryId to the LegacyId of the CarbIntake it belongs to.
    ///     V2: direct mapping (glooko_food_{externalEntryId}).
    ///     V3: food guid → meal guid → glooko_v3meal_{mealGuid}.
    /// </summary>
    public Func<string, string?>? FoodEntryToCarbLegacyId { get; set; }
}

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
                : _timeMapper.ToGlookoTime(DateTime.UtcNow.AddDays(-1));
            var to = _timeMapper.ToGlookoTime(DateTime.UtcNow);

            await ReportMessageAsync(progressReporter, SyncMessageType.FetchingData,
                new() { ["from"] = from.ToString("MMM dd"), ["to"] = to.ToString("MMM dd") },
                cancellationToken);

            // Fetch + map: V2 or V3 path fills the same data structure
            var syncData = _config.UseV3Api
                ? await FetchAndMapViaV3Async(from, to)
                : await FetchAndMapViaV2Async(from, to);

            if (syncData == null)
            {
                result.Success = false;
                result.Message = "Failed to fetch data";
                result.Errors.Add("No data returned from Glooko");
                return result;
            }

            // Single publish pipeline for all data types
            await PublishAllAsync(syncData, activeTypes, result, config, progressReporter, cancellationToken);

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
    ///     Fetches from all V2 endpoints and maps to the common <see cref="GlookoSyncData"/> structure.
    /// </summary>
    private async Task<GlookoSyncData?> FetchAndMapViaV2Async(DateTime fromDate, DateTime toDate)
    {
        var batchData = await FetchBatchDataAsync(fromDate, toDate);
        if (batchData == null) return null;

        var (boluses, carbs, batches) = _v4TreatmentMapper.MapBatchData(batchData);

        var data = new GlookoSyncData
        {
            SensorGlucose = _sensorGlucoseMapper.TransformBatchDataToSensorGlucose(batchData).ToList(),
            BgChecks = _sensorGlucoseMapper.TransformBatchDataToBGChecks(batchData).ToList(),
            Boluses = boluses,
            CarbIntakes = carbs,
            Batches = batches,
            StateSpans = _stateSpanMapper.TransformV2ToStateSpans(batchData),
            TempBasals = _tempBasalMapper.TransformV2ToTempBasals(batchData),
            FoodEntryImports = batchData.Foods is { Length: > 0 }
                ? _v4TreatmentMapper.MapFoodsToConnectorEntries(batchData)
                : [],
            // V2: direct guid correlation — food.Guid → CarbIntake.LegacyId "glooko_food_{guid}"
            FoodEntryToCarbLegacyId = externalEntryId => $"glooko_food_{externalEntryId}",
        };

        return data;
    }

    // ── V3 fetch + map ──────────────────────────────────────────────────

    /// <summary>
    ///     Fetches from V3 graph/data and histories endpoints, maps to the common <see cref="GlookoSyncData"/> structure.
    /// </summary>
    private async Task<GlookoSyncData?> FetchAndMapViaV3Async(DateTime fromDate, DateTime toDate)
    {
        _logger.LogInformation("[{ConnectorSource}] Fetching data from v3 API...", ConnectorSource);

        var v3Data = await FetchV3GraphDataAsync(fromDate, toDate);
        if (v3Data == null) return null;

        GlookoV3HistoriesResponse? v3Histories = null;
        try
        {
            v3Histories = await FetchV3HistoriesAsync(fromDate, toDate);
        }
        catch (Exception histEx)
        {
            _logger.LogWarning(histEx, "[{ConnectorSource}] V3 histories fetch failed, meal data will be unavailable", ConnectorSource);
        }

        var (v3Boluses, v3BolusCarbIntakes, v3Batches) = _v4TreatmentMapper.MapV3Boluses(v3Data);

        // Carbs: from bolus wizard + from V3 history meals (preferred) or carbAll series (fallback).
        // History meals are preferred because their legacy IDs ("glooko_v3meal_{mealGuid}")
        // are needed for food attribution. carbAll would create duplicate carb entries
        // with different legacy IDs, breaking the food → carb correlation.
        var allCarbs = new List<CarbIntake>(v3BolusCarbIntakes);
        var historyMealCarbs = v3Histories?.Histories != null
            ? _v4TreatmentMapper.MapV3HistoryMealsToCarbIntakes(v3Histories)
            : [];

        if (historyMealCarbs.Count > 0)
            allCarbs.AddRange(historyMealCarbs);
        else
            allCarbs.AddRange(_v4TreatmentMapper.MapV3CarbAll(v3Data));

        // Food entries: merge V3 history meals (structure + meal type) with V2 foods (externalId, brand)
        GlookoFood[]? v2Foods = null;
        if (historyMealCarbs.Count > 0)
        {
            try
            {
                v2Foods = await FetchV2FoodsAsync(fromDate, toDate);
            }
            catch (Exception v2Ex)
            {
                _logger.LogWarning(v2Ex, "[{ConnectorSource}] V2 foods fetch failed, food entries will lack externalId/brand metadata", ConnectorSource);
            }
        }

        var foodEntryImports = historyMealCarbs.Count > 0 && v3Histories?.Histories != null
            ? _v4TreatmentMapper.MapV3HistoryMealsToConnectorEntries(v3Histories, v2Foods)
            : [];

        // Build food guid → meal guid lookup for attribution (only when using history meals)
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
                    ? $"glooko_v3meal_{mealGuid}"
                    : null;
        }

        var data = new GlookoSyncData
        {
            SensorGlucose = _config.V3IncludeCgmBackfill
                ? _sensorGlucoseMapper.TransformV3ToSensorGlucose(v3Data, _meterUnits).ToList()
                : [],
            BgChecks = _sensorGlucoseMapper.TransformV3ToBGChecks(v3Data, _meterUnits).ToList(),
            Boluses = v3Boluses,
            CarbIntakes = allCarbs,
            Batches = v3Batches,
            StateSpans = _stateSpanMapper.TransformV3ToStateSpans(v3Data),
            TempBasals = _tempBasalMapper.TransformV3ToTempBasals(v3Data),
            DeviceEvents = _v4TreatmentMapper.MapV3DeviceEvents(v3Data),
            SystemEvents = _systemEventMapper.TransformV3ToSystemEvents(v3Data),
            FoodEntryImports = foodEntryImports,
            FoodEntryToCarbLegacyId = foodResolver,
        };

        return data;
    }

    // ── Shared publish pipeline ──────────────────────────────────────────

    /// <summary>
    ///     Publishes all data types from a <see cref="GlookoSyncData"/> bag.
    ///     Shared between V2 and V3 — the only difference is how the bag was filled.
    /// </summary>
    private async Task PublishAllAsync(
        GlookoSyncData data,
        HashSet<SyncDataType> activeTypes,
        SyncResult result,
        GlookoConnectorConfiguration config,
        ISyncProgressReporter? progressReporter,
        CancellationToken cancellationToken)
    {
        // 1. Glucose
        await ReportMessageAsync(progressReporter, SyncMessageType.ProcessingDataType,
            new() { ["dataType"] = SyncDataType.Glucose.ToString() }, cancellationToken);

        if (activeTypes.Contains(SyncDataType.Glucose))
        {
            if (data.SensorGlucose.Count > 0)
            {
                if (await PublishSensorGlucoseDataAsync(data.SensorGlucose, config, cancellationToken))
                {
                    result.ItemsSynced[SyncDataType.Glucose] = data.SensorGlucose.Count;
                    result.LastEntryTimes[SyncDataType.Glucose] = DateTimeOffset
                        .FromUnixTimeMilliseconds(data.SensorGlucose.Max(s => s.Mills)).UtcDateTime;

                    await ReportMessageAsync(progressReporter, SyncMessageType.PublishingDataType,
                        new() { ["dataType"] = SyncDataType.Glucose.ToString(), ["count"] = data.SensorGlucose.Count.ToString() },
                        cancellationToken);

                    _logger.LogInformation("[{ConnectorSource}] Published {Count} sensor glucose records",
                        ConnectorSource, data.SensorGlucose.Count);
                }
            }

            if (data.BgChecks.Count > 0)
            {
                if (await PublishBGCheckDataAsync(data.BgChecks, config, cancellationToken))
                    _logger.LogInformation("[{ConnectorSource}] Published {Count} BG checks",
                        ConnectorSource, data.BgChecks.Count);
            }
        }

        // 2. Treatments (batches → boluses → carb intakes)
        if (data.Batches.Count > 0)
            await PublishDecompositionBatchesAsync(data.Batches, config, cancellationToken);

        await ReportMessageAsync(progressReporter, SyncMessageType.ProcessingDataType,
            new() { ["dataType"] = SyncDataType.Boluses.ToString() }, cancellationToken);

        if (activeTypes.Contains(SyncDataType.Boluses) && data.Boluses.Count > 0)
        {
            if (await PublishBolusDataAsync(data.Boluses, config, cancellationToken))
            {
                result.ItemsSynced[SyncDataType.Boluses] = data.Boluses.Count;
                _logger.LogInformation("[{ConnectorSource}] Published {Count} boluses", ConnectorSource, data.Boluses.Count);
                await ReportMessageAsync(progressReporter, SyncMessageType.PublishingDataType,
                    new() { ["dataType"] = SyncDataType.Boluses.ToString(), ["count"] = data.Boluses.Count.ToString() }, cancellationToken);
            }
        }

        await ReportMessageAsync(progressReporter, SyncMessageType.ProcessingDataType,
            new() { ["dataType"] = SyncDataType.CarbIntake.ToString() }, cancellationToken);

        if (activeTypes.Contains(SyncDataType.CarbIntake) && data.CarbIntakes.Count > 0)
        {
            if (await PublishCarbIntakeDataAsync(data.CarbIntakes, config, cancellationToken))
            {
                result.ItemsSynced[SyncDataType.CarbIntake] = data.CarbIntakes.Count;
                _logger.LogInformation("[{ConnectorSource}] Published {Count} carb intakes", ConnectorSource, data.CarbIntakes.Count);
                await ReportMessageAsync(progressReporter, SyncMessageType.PublishingDataType,
                    new() { ["dataType"] = SyncDataType.CarbIntake.ToString(), ["count"] = data.CarbIntakes.Count.ToString() }, cancellationToken);
            }
        }

        // 3. Food catalog + attribution
        if (data.FoodEntryImports.Count > 0 && _connectorPublisher is { IsAvailable: true })
        {
            var importedEntries = await _connectorPublisher.Metadata.PublishConnectorFoodEntriesAsync(
                data.FoodEntryImports, ConnectorSource, cancellationToken);

            if (importedEntries is { Count: > 0 })
            {
                _logger.LogInformation("[{ConnectorSource}] Published {Count} food entries to connector food catalog",
                    ConnectorSource, importedEntries.Count);

                if (_mealMatchingService != null && data.CarbIntakes.Count > 0 && data.FoodEntryToCarbLegacyId != null)
                {
                    var pendingEntries = importedEntries
                        .Where(e => e.Status == ConnectorFoodEntryStatus.Pending)
                        .ToList();

                    if (pendingEntries.Count > 0)
                    {
                        var carbsByLegacyId = data.CarbIntakes
                            .Where(ci => ci.LegacyId != null)
                            .ToDictionary(ci => ci.LegacyId!, StringComparer.OrdinalIgnoreCase);

                        var attributedCount = 0;

                        foreach (var entry in pendingEntries)
                        {
                            var legacyKey = data.FoodEntryToCarbLegacyId(entry.ExternalEntryId);
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
                }
            }
        }

        // 4. State spans + temp basals
        await ReportMessageAsync(progressReporter, SyncMessageType.ProcessingDataType,
            new() { ["dataType"] = SyncDataType.StateSpans.ToString() }, cancellationToken);

        if (activeTypes.Contains(SyncDataType.StateSpans))
        {
            var tempBasalCount = 0;

            if (data.StateSpans.Count > 0 && await PublishStateSpanDataAsync(data.StateSpans, config, cancellationToken))
                _logger.LogInformation("[{ConnectorSource}] Published {Count} state spans", ConnectorSource, data.StateSpans.Count);

            if (data.TempBasals.Count > 0 && await PublishTempBasalDataAsync(data.TempBasals, config, cancellationToken))
            {
                tempBasalCount = data.TempBasals.Count;
                _logger.LogInformation("[{ConnectorSource}] Published {Count} temp basals", ConnectorSource, data.TempBasals.Count);
            }

            if (tempBasalCount > 0)
                result.ItemsSynced[SyncDataType.StateSpans] = tempBasalCount;
        }

        // 5. Device events + system events
        await ReportMessageAsync(progressReporter, SyncMessageType.ProcessingDataType,
            new() { ["dataType"] = SyncDataType.DeviceEvents.ToString() }, cancellationToken);

        if (activeTypes.Contains(SyncDataType.DeviceEvents))
        {
            var deviceEventCount = 0;

            if (data.DeviceEvents.Count > 0 && await PublishDeviceEventDataAsync(data.DeviceEvents, config, cancellationToken))
            {
                deviceEventCount += data.DeviceEvents.Count;
                _logger.LogInformation("[{ConnectorSource}] Published {Count} device events", ConnectorSource, data.DeviceEvents.Count);
            }

            if (data.SystemEvents.Count > 0 && await PublishSystemEventDataAsync(data.SystemEvents, config, cancellationToken))
            {
                deviceEventCount += data.SystemEvents.Count;
                _logger.LogInformation("[{ConnectorSource}] Published {Count} system events", ConnectorSource, data.SystemEvents.Count);
            }

            if (deviceEventCount > 0)
                result.ItemsSynced[SyncDataType.DeviceEvents] = deviceEventCount;
        }
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
