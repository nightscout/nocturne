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

        var code = _tokenProvider.UserData?.UserLogin?.GlookoCode;
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
        var patientCode = _tokenProvider.UserData?.UserLogin?.GlookoCode;
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
        var patientCode = _tokenProvider.UserData?.UserLogin?.GlookoCode;

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

            // Compute active types: intersection of requested and enabled types
            if (!request.DataTypes.Any())
                request.DataTypes = SupportedDataTypes;
            var enabledTypes = config.GetEnabledDataTypes(SupportedDataTypes);
            var activeTypes = request.DataTypes.Where(t => enabledTypes.Contains(t)).ToHashSet();

            // Convert real UTC (from database) back to Glooko's fake-UTC format
            // for API requests. Glooko timestamps are labeled as UTC but are actually
            // local time, so we reverse the timezone correction applied during inbound processing.
            var from = request.From.HasValue
                ? _timeMapper.ToGlookoTime(request.From.Value)
                : (DateTime?)null;

            await ReportMessageAsync(progressReporter, SyncMessageType.FetchingData,
                new() { ["from"] = (from ?? DateTime.UtcNow.AddMonths(-6)).ToString("MMM dd"), ["to"] = DateTime.UtcNow.ToString("MMM dd") },
                cancellationToken);

            var batchData = await FetchBatchDataAsync(from);

            if (batchData == null)
            {
                result.Success = false;
                result.Message = "Failed to fetch data";
                result.Errors.Add("No data returned from Glooko");
                return result;
            }

            // Fetch V3 data once upfront if needed for any data type
            GlookoV3GraphResponse? v3Data = null;
            var needsV3Data = _config.UseV3Api && (
                activeTypes.Contains(SyncDataType.Boluses) ||
                activeTypes.Contains(SyncDataType.CarbIntake) ||
                activeTypes.Contains(SyncDataType.StateSpans) ||
                activeTypes.Contains(SyncDataType.DeviceEvents) ||
                (_config.V3IncludeCgmBackfill && activeTypes.Contains(SyncDataType.Glucose))
            );

            if (needsV3Data)
            {
                try
                {
                    _logger.LogInformation("[{ConnectorSource}] Fetching additional data from v3 API...", ConnectorSource);
                    v3Data = await FetchV3GraphDataAsync(from);
                }
                catch (Exception v3Ex)
                {
                    _logger.LogWarning(v3Ex, "[{ConnectorSource}] V3 API fetch failed, continuing with v2 data only", ConnectorSource);
                }
            }

            // 1. Process Glucose
            await ReportMessageAsync(progressReporter, SyncMessageType.ProcessingDataType,
                new() { ["dataType"] = SyncDataType.Glucose.ToString() }, cancellationToken);

            if (activeTypes.Contains(SyncDataType.Glucose))
            {
                var sensorGlucose = _sensorGlucoseMapper.TransformBatchDataToSensorGlucose(batchData).ToList();
                if (sensorGlucose.Count > 0)
                {
                    var success = await PublishSensorGlucoseDataAsync(sensorGlucose, config, cancellationToken);
                    if (success)
                    {
                        result.ItemsSynced[SyncDataType.Glucose] = sensorGlucose.Count;
                        result.LastEntryTimes[SyncDataType.Glucose] = DateTimeOffset
                            .FromUnixTimeMilliseconds(sensorGlucose.Max(s => s.Mills)).UtcDateTime;

                        await ReportMessageAsync(progressReporter, SyncMessageType.PublishingDataType,
                            new() { ["dataType"] = SyncDataType.Glucose.ToString(), ["count"] = sensorGlucose.Count.ToString() },
                            cancellationToken);
                    }
                }

                // V3 CGM backfill
                if (_config.V3IncludeCgmBackfill && v3Data != null)
                {
                    var v3Glucose = _sensorGlucoseMapper.TransformV3ToSensorGlucose(v3Data, _meterUnits).ToList();
                    if (v3Glucose.Count > 0)
                    {
                        await PublishSensorGlucoseDataAsync(v3Glucose, config, cancellationToken);
                        _logger.LogInformation("[{ConnectorSource}] Published {Count} CGM backfill sensor glucose from v3",
                            ConnectorSource, v3Glucose.Count);
                    }
                }
            }

            // 2. Process Treatments (boluses, carb intake)
            var allBoluses = new List<Bolus>();
            var allCarbs = new List<CarbIntake>();
            var allDeviceEvents = new List<DeviceEvent>();
            var allBatches = new List<DecompositionBatch>();

            if (_config.UseV3Api && v3Data != null)
            {
                var (v3Boluses, v3BolusCarbIntakes, v3Batches) = _v4TreatmentMapper.MapV3Boluses(v3Data);
                allBoluses.AddRange(v3Boluses);
                allCarbs.AddRange(v3BolusCarbIntakes);
                allBatches.AddRange(v3Batches);

                // V2 standalone food records have no V3 equivalent — create CarbIntake records
                allCarbs.AddRange(_v4TreatmentMapper.MapFoodsToCarbIntakes(batchData));
                allDeviceEvents.AddRange(_v4TreatmentMapper.MapV3DeviceEvents(v3Data));
            }
            else
            {
                var (v2Boluses, v2Carbs, v2Batches) = _v4TreatmentMapper.MapBatchData(batchData);
                allBoluses.AddRange(v2Boluses);
                allCarbs.AddRange(v2Carbs);
                allBatches.AddRange(v2Batches);
            }

            // Persist decomposition batches before V4 records (FK constraint)
            if (allBatches.Count > 0)
                await PublishDecompositionBatchesAsync(allBatches, config, cancellationToken);

            // Publish boluses
            await ReportMessageAsync(progressReporter, SyncMessageType.ProcessingDataType,
                new() { ["dataType"] = SyncDataType.Boluses.ToString() }, cancellationToken);

            if (activeTypes.Contains(SyncDataType.Boluses) && allBoluses.Count > 0)
            {
                if (await PublishBolusDataAsync(allBoluses, config, cancellationToken))
                {
                    result.ItemsSynced[SyncDataType.Boluses] = allBoluses.Count;
                    _logger.LogInformation("[{ConnectorSource}] Published {Count} boluses", ConnectorSource, allBoluses.Count);
                    await ReportMessageAsync(progressReporter, SyncMessageType.PublishingDataType,
                        new() { ["dataType"] = SyncDataType.Boluses.ToString(), ["count"] = allBoluses.Count.ToString() }, cancellationToken);
                }
            }

            // Publish carb intakes
            await ReportMessageAsync(progressReporter, SyncMessageType.ProcessingDataType,
                new() { ["dataType"] = SyncDataType.CarbIntake.ToString() }, cancellationToken);

            if (activeTypes.Contains(SyncDataType.CarbIntake) && allCarbs.Count > 0)
            {
                if (await PublishCarbIntakeDataAsync(allCarbs, config, cancellationToken))
                {
                    result.ItemsSynced[SyncDataType.CarbIntake] = allCarbs.Count;
                    _logger.LogInformation("[{ConnectorSource}] Published {Count} carb intakes", ConnectorSource, allCarbs.Count);
                    await ReportMessageAsync(progressReporter, SyncMessageType.PublishingDataType,
                        new() { ["dataType"] = SyncDataType.CarbIntake.ToString(), ["count"] = allCarbs.Count.ToString() }, cancellationToken);
                }
            }

            // Publish V2 food records as connector food entries (creates Food catalog + ConnectorFoodEntry records)
            if (_config.UseV3Api && batchData.Foods is { Length: > 0 })
            {
                var foodEntryImports = _v4TreatmentMapper.MapFoodsToConnectorEntries(batchData);
                if (foodEntryImports.Count > 0 && _connectorPublisher is { IsAvailable: true })
                {
                    var importedEntries = await _connectorPublisher.Metadata.PublishConnectorFoodEntriesAsync(
                        foodEntryImports, ConnectorSource, cancellationToken);

                    if (importedEntries is { Count: > 0 })
                    {
                        _logger.LogInformation(
                            "[{ConnectorSource}] Published {Count} food entries to connector food catalog",
                            ConnectorSource, importedEntries.Count);

                        // Attribute food entries to CarbIntakes using the Glooko guid correlation:
                        // ConnectorFoodEntry.ExternalEntryId == food.Guid
                        // CarbIntake.LegacyId == "glooko_food_{food.Guid}"
                        // Only process Pending entries (newly created this sync); already-matched
                        // entries from previous syncs are skipped to avoid FK errors from
                        // in-memory CarbIntake IDs that don't match the persisted DB IDs.
                        if (_mealMatchingService != null && allCarbs.Count > 0)
                        {
                            var pendingEntries = importedEntries
                                .Where(e => e.Status == ConnectorFoodEntryStatus.Pending)
                                .ToList();

                            if (pendingEntries.Count > 0)
                            {
                                var carbsByLegacyId = allCarbs
                                    .Where(ci => ci.LegacyId != null)
                                    .ToDictionary(ci => ci.LegacyId!, StringComparer.OrdinalIgnoreCase);

                                foreach (var entry in pendingEntries)
                                {
                                    var legacyKey = $"glooko_food_{entry.ExternalEntryId}";
                                    if (!carbsByLegacyId.TryGetValue(legacyKey, out var carbIntake))
                                        continue;

                                    try
                                    {
                                        await _mealMatchingService.AcceptMatchAsync(
                                            entry.Id, carbIntake.Id, entry.Carbs, timeOffsetMinutes: 0, cancellationToken);
                                    }
                                    catch (Exception ex)
                                    {
                                        _logger.LogWarning(ex,
                                            "[{ConnectorSource}] Failed to attribute food entry {FoodEntryId} to CarbIntake {CarbIntakeId}",
                                            ConnectorSource, entry.Id, carbIntake.Id);
                                    }
                                }

                                _logger.LogInformation(
                                    "[{ConnectorSource}] Attributed {Count} food entries to carb intakes",
                                    ConnectorSource, pendingEntries.Count);
                            }
                        }
                    }
                }
            }

            // 3. Process DeviceEvents
            await ReportMessageAsync(progressReporter, SyncMessageType.ProcessingDataType,
                new() { ["dataType"] = SyncDataType.DeviceEvents.ToString() }, cancellationToken);

            if (activeTypes.Contains(SyncDataType.DeviceEvents))
            {
                var deviceEventCount = 0;

                if (allDeviceEvents.Count > 0 && await PublishDeviceEventDataAsync(allDeviceEvents, config, cancellationToken))
                {
                    deviceEventCount += allDeviceEvents.Count;
                    _logger.LogInformation("[{ConnectorSource}] Published {Count} device events", ConnectorSource, allDeviceEvents.Count);
                }

                if (v3Data != null)
                {
                    var systemEvents = _systemEventMapper.TransformV3ToSystemEvents(v3Data);
                    if (systemEvents.Any() && await PublishSystemEventDataAsync(systemEvents, config, cancellationToken))
                    {
                        deviceEventCount += systemEvents.Count;
                        _logger.LogInformation("[{ConnectorSource}] Published {Count} system events from v3", ConnectorSource, systemEvents.Count);
                    }
                }

                if (deviceEventCount > 0)
                    result.ItemsSynced[SyncDataType.DeviceEvents] = deviceEventCount;
            }

            // 4. Process StateSpans and TempBasals
            await ReportMessageAsync(progressReporter, SyncMessageType.ProcessingDataType,
                new() { ["dataType"] = SyncDataType.StateSpans.ToString() }, cancellationToken);

            if (activeTypes.Contains(SyncDataType.StateSpans))
            {
                var tempBasalCount = 0;

                if (v3Data != null)
                {
                    var stateSpans = _stateSpanMapper.TransformV3ToStateSpans(v3Data);
                    if (stateSpans.Any() && await PublishStateSpanDataAsync(stateSpans, config, cancellationToken))
                        _logger.LogInformation("[{ConnectorSource}] Published {Count} state spans from v3", ConnectorSource, stateSpans.Count);

                    var v3TempBasals = _tempBasalMapper.TransformV3ToTempBasals(v3Data);
                    if (v3TempBasals.Any() && await PublishTempBasalDataAsync(v3TempBasals, config, cancellationToken))
                    {
                        tempBasalCount += v3TempBasals.Count;
                        _logger.LogInformation("[{ConnectorSource}] Published {Count} temp basals from v3", ConnectorSource, v3TempBasals.Count);
                    }
                }

                var v2StateSpans = _stateSpanMapper.TransformV2ToStateSpans(batchData);
                if (v2StateSpans.Any() && await PublishStateSpanDataAsync(v2StateSpans, config, cancellationToken))
                    _logger.LogInformation("[{ConnectorSource}] Published {Count} state spans from v2", ConnectorSource, v2StateSpans.Count);

                var v2TempBasals = _tempBasalMapper.TransformV2ToTempBasals(batchData);
                if (v2TempBasals.Any() && await PublishTempBasalDataAsync(v2TempBasals, config, cancellationToken))
                {
                    tempBasalCount += v2TempBasals.Count;
                    _logger.LogInformation("[{ConnectorSource}] Published {Count} temp basals from v2", ConnectorSource, v2TempBasals.Count);
                }

                if (tempBasalCount > 0)
                    result.ItemsSynced[SyncDataType.StateSpans] = tempBasalCount;
            }

            // 5. Process Profiles
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

    // ── V2 batch data ───────────────────────────────────────────────────

    /// <summary>
    ///     Fetches comprehensive batch data from all v2 Glooko endpoints.
    /// </summary>
    public async Task<GlookoBatchData?> FetchBatchDataAsync(DateTime? since = null)
    {
        try
        {
            var patientCode = EnsureAuthenticatedAndGetCode();
            if (patientCode == null) return null;

            var fromDate = since ?? _timeMapper.ToGlookoTime(DateTime.UtcNow.AddDays(-1));
            var toDate = _timeMapper.ToGlookoTime(DateTime.UtcNow);

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
                + "Readings={ReadingsCount}, Foods={FoodsCount}, "
                + "NormalBoluses={BolusCount}, TempBasals={TempBasalCount}, "
                + "ScheduledBasals={ScheduledBasalCount}, Suspends={SuspendCount}",
                ConnectorSource,
                batchData.Readings?.Length ?? 0,
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

    // ── V3 API methods ──────────────────────────────────────────────────

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
    public async Task<GlookoV3GraphResponse?> FetchV3GraphDataAsync(DateTime? since = null)
    {
        try
        {
            var patientCode = EnsureAuthenticatedAndGetCode();
            if (patientCode == null) return null;

            // Ensure we have meter units
            if (string.IsNullOrEmpty(_meterUnits)) await FetchV3UserProfileAsync();

            var fromDate = since ?? _timeMapper.ToGlookoTime(DateTime.UtcNow.AddDays(-1));
            var toDate = _timeMapper.ToGlookoTime(DateTime.UtcNow);

            var url = ConstructV3GraphUrl(fromDate, toDate);
            _logger.LogInformation("[{ConnectorSource}] Fetching v3 graph data from {StartDate:yyyy-MM-dd} to {EndDate:yyyy-MM-dd}",
                ConnectorSource, fromDate, toDate);

            var result = await FetchFromGlookoEndpointWithRetry(url);
            if (!result.HasValue) return null;

            var graphData = JsonSerializer.Deserialize<GlookoV3GraphResponse>(result.Value.GetRawText());

            if (graphData?.Series != null)
                _logger.LogInformation(
                    "[{ConnectorSource}] Fetched v3 graph data: "
                    + "AutomaticBolus={AutoBolus}, DeliveredBolus={Bolus}, "
                    + "PumpAlarm={Alarms}, ReservoirChange={Reservoir}, SetSiteChange={SetSite}, "
                    + "CgmReadings={Cgm}",
                    ConnectorSource,
                    graphData.Series.AutomaticBolus?.Length ?? 0,
                    graphData.Series.DeliveredBolus?.Length ?? 0,
                    graphData.Series.PumpAlarm?.Length ?? 0,
                    graphData.Series.ReservoirChange?.Length ?? 0,
                    graphData.Series.SetSiteChange?.Length ?? 0,
                    (graphData.Series.CgmHigh?.Length ?? 0)
                    + (graphData.Series.CgmNormal?.Length ?? 0)
                    + (graphData.Series.CgmLow?.Length ?? 0));

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
