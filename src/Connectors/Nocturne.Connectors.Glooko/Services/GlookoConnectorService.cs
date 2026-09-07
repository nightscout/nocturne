using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.Core.Utilities;
using Nocturne.Connectors.Glooko.Configurations;
using Nocturne.Connectors.Glooko.Mappers;
using Nocturne.Connectors.Glooko.Models;
using Nocturne.Connectors.Glooko.Utilities;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.Timezones;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Timezones;
using Nocturne.Core.Models.V4;
using Nocturne.Core.Contracts.V4;

namespace Nocturne.Connectors.Glooko.Services;

/// <summary>
///     Connector service for Glooko data source.
///     Based on the original nightscout-connect Glooko implementation.
/// </summary>
public class GlookoConnectorService : BaseConnectorService<GlookoConnectorConfiguration>
{
    private readonly IConnectorPublisher? _connectorPublisher;
    private readonly IMealMatchingService? _mealMatchingService;
    private readonly IRateLimitingStrategy _rateLimitingStrategy;
    private readonly IRetryDelayStrategy _retryDelayStrategy;
    private readonly GlookoAuthTokenProvider _tokenProvider;
    private readonly ITimezoneTimelineService? _timezoneTimelineService;
    private readonly IConnectorSyncCursorStore? _cursorStore;
    private readonly ILogger<GlookoConnectorService> _glookoLogger;

    public GlookoConnectorService(
        HttpClient httpClient,
        IConnectorServerResolver<GlookoConnectorConfiguration> serverResolver,
        ILogger<GlookoConnectorService> logger,
        IRetryDelayStrategy retryDelayStrategy,
        IRateLimitingStrategy rateLimitingStrategy,
        GlookoAuthTokenProvider tokenProvider,
        IConnectorPublisher? publisher = null,
        IMealMatchingService? mealMatchingService = null,
        ITimezoneTimelineService? timezoneTimelineService = null,
        IConnectorSyncCursorStore? cursorStore = null
    )
        : base(httpClient, serverResolver, logger, publisher)
    {
        _connectorPublisher = publisher;
        _mealMatchingService = mealMatchingService;
        _retryDelayStrategy = retryDelayStrategy ?? throw new ArgumentNullException(nameof(retryDelayStrategy));
        _rateLimitingStrategy = rateLimitingStrategy ?? throw new ArgumentNullException(nameof(rateLimitingStrategy));
        _tokenProvider = tokenProvider ?? throw new ArgumentNullException(nameof(tokenProvider));
        _timezoneTimelineService = timezoneTimelineService;
        _cursorStore = cursorStore;
        _glookoLogger = logger;
    }

    public override string ServiceName => "Glooko";
    protected override string ConnectorSource => DataSources.GlookoConnector;

    private const string SyncSucceededMessage = "Sync completed successfully";


    // ── Authentication ──────────────────────────────────────────────────

    private async Task<bool> AuthenticateWithConfigAsync(GlookoSyncContext context)
    {
        var token = await _tokenProvider.GetValidTokenAsync(context.Config);
        if (token == null)
        {
            TrackFailedRequest("Failed to get valid token");
            return false;
        }

        // The token IS the session cookie for Glooko
        context.SessionCookie = token;

        // Retrieve user data from cache metadata via the token provider's public accessor
        var cached = await _tokenProvider.GetCachedSessionAsync();
        if (cached?.Metadata != null && cached.Metadata.TryGetValue("UserData", out var userDataJson))
        {
            context.UserData = JsonSerializer.Deserialize<GlookoUserData>(userDataJson);
        }

        TrackSuccessfulRequest();
        return true;
    }

    /// <summary>
    ///     Validates that the session is active and the Glooko user code is available.
    ///     Throws <see cref="InvalidOperationException"/> if not authenticated.
    ///     Returns null and logs a warning if the user code is missing.
    /// </summary>
    private string? EnsureAuthenticatedAndGetCode(GlookoSyncContext context)
    {
        if (string.IsNullOrEmpty(context.SessionCookie))
            throw new InvalidOperationException(
                "Not authenticated with Glooko. Call AuthenticateAsync first.");

        var code = context.PatientCode;
        if (code == null)
            _logger.LogWarning("Missing Glooko user code, cannot fetch data");

        return code;
    }

    // ── HTTP helpers ────────────────────────────────────────────────────

    /// <summary>
    ///     Sends a GET request to a Glooko API endpoint with standard headers.
    ///     Relative paths are resolved against the configured server region.
    /// </summary>
    private async Task<JsonElement?> FetchFromGlookoEndpoint(GlookoSyncContext context, string url)
    {
        var baseUrl = GlookoConstants.ResolveBaseUrl(context.Config.Server);
        var webOrigin = GlookoConstants.ResolveWebOrigin(context.Config.Server);
        var absoluteUrl = url.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? url
            : $"{baseUrl}{url}";

        _logger.LogDebug("GLOOKO FETCHER LOADING {Url}", absoluteUrl);

        var request = new HttpRequestMessage(HttpMethod.Get, absoluteUrl);
        GlookoHttpHelper.ApplyStandardHeaders(request, webOrigin, context.SessionCookie);

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

        // 403 on a patient-scoped endpoint (e.g. {"code":"data_cant_view"}) means the cached
        // glookoCode is no longer authorized — typically it changed after an account/data-source
        // re-link. Surface a distinct type so the sync re-authenticates and re-resolves the code
        // instead of hammering the stale one until the 24h session cache expires.
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            var body = await GlookoHttpHelper.ReadResponseAsync(response);
            _logger.LogWarning("Forbidden (403) fetching from {Url}: {Body}", absoluteUrl, body);
            throw new GlookoDataForbiddenException($"Glooko returned 403 Forbidden for {absoluteUrl}: {body}");
        }

        _logger.LogWarning("Failed to fetch from {Url}: {StatusCode}", absoluteUrl, response.StatusCode);
        throw new HttpRequestException($"HTTP {(int)response.StatusCode} {response.StatusCode}");
    }

    /// <summary>
    ///     Fetches from a Glooko endpoint with retry logic and exponential backoff.
    ///     Throws rather than returning null once the attempts are spent, so a caller cannot mistake
    ///     an exhausted endpoint for one that legitimately had no data.
    /// </summary>
    /// <param name="maxRetries">Total attempts, not retries on top of a first try; clamped to a floor of one.</param>
    internal async Task<JsonElement?> FetchFromGlookoEndpointWithRetry(
        GlookoSyncContext context, string url, int maxRetries = 3)
    {
        HttpRequestException? lastException = null;

        return await ConnectorRetryLoop.RunAsync<JsonElement?>(
            async (attempt, _) =>
            {
                try
                {
                    var result = await FetchFromGlookoEndpoint(context, url);
                    if (result.HasValue)
                        return RetryStep<JsonElement?>.Complete(result);

                    _logger.LogWarning("Attempt {AttemptNumber} failed for {Url}", attempt + 1, url);
                }
                catch (GlookoDataForbiddenException)
                {
                    // The patient code is part of the URL; retrying it unchanged will 403 again.
                    // Bubble up immediately so the caller can re-authenticate and rebuild URLs.
                    throw;
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

                return RetryStep<JsonElement?>.RetryAfterDelay;
            },
            _retryDelayStrategy,
            maxRetries,
            attempts =>
            {
                _logger.LogError("All {MaxRetries} attempts failed for {Url}", attempts, url);
                throw lastException ?? new HttpRequestException($"All {attempts} attempts failed for {url}");
            },
            CancellationToken.None,
            attempt => _logger.LogInformation("Applying retry backoff before retry {RetryNumber}", attempt + 2));
    }

    // ── URL construction ────────────────────────────────────────────────

    private static string ConstructV2Url(
        GlookoSyncContext context, string endpoint, DateTime startDate, DateTime endDate)
    {
        var patientCode = context.PatientCode;
        var maxCount = Math.Max(1, (int)Math.Ceiling((endDate - startDate).TotalMinutes / 5));

        return $"{endpoint}?patient={patientCode}"
             + $"&startDate={startDate:yyyy-MM-ddTHH:mm:ss.fffZ}"
             + $"&endDate={endDate:yyyy-MM-ddTHH:mm:ss.fffZ}"
             + $"&lastGuid={GlookoConstants.LegacyLastGuid}"
             + $"&lastUpdatedAt={startDate:yyyy-MM-ddTHH:mm:ss.fffZ}"
             + $"&limit={maxCount}";
    }

    private static string ConstructV3GraphUrl(GlookoSyncContext context, DateTime startDate, DateTime endDate)
    {
        var patientCode = context.PatientCode;

        var series = GlookoConstants.V3GraphSeries
            .Concat(GlookoConstants.V3PumpModeSeries);

        if (context.Config.V3IncludeCgmBackfill)
            series = series.Concat(GlookoConstants.V3CgmBackfillSeries);

        var seriesParams = string.Join("&", series.Select(s => $"series[]={s}"));

        return $"{GlookoConstants.V3GraphDataPath}?patient={patientCode}"
             + $"&startDate={startDate:yyyy-MM-ddTHH:mm:ss.fffZ}"
             + $"&endDate={endDate:yyyy-MM-ddTHH:mm:ss.fffZ}"
             + $"&{seriesParams}"
             + "&locale=en&insulinTooltips=false&filterBgReadings=false&splitByDay=false";
    }

    /// <summary>
    ///     Builds a v3 graph/data URL requesting ONLY the pump-mode series. Pump operating-mode spans
    ///     (auto/manual/sleep/exercise/...) have no SSV2 equivalent — verified against the decompiled app,
    ///     which only exposes aggregate mode percentages, never per-interval spans — so the SSV2 sync path
    ///     keeps this one slim v3 call for the mode timeline (a fraction of the full graph payload).
    /// </summary>
    private static string ConstructV3PumpModeUrl(
        GlookoSyncContext context, DateTime startDate, DateTime endDate)
    {
        var patientCode = context.PatientCode;
        var seriesParams = string.Join("&", GlookoConstants.V3PumpModeSeries.Select(s => $"series[]={s}"));

        return $"{GlookoConstants.V3GraphDataPath}?patient={patientCode}"
             + $"&startDate={startDate:yyyy-MM-ddTHH:mm:ss.fffZ}"
             + $"&endDate={endDate:yyyy-MM-ddTHH:mm:ss.fffZ}"
             + $"&{seriesParams}"
             + "&locale=en&insulinTooltips=false&filterBgReadings=false&splitByDay=false";
    }

    /// <summary>
    ///     Fetches ONLY the v3 pump-mode series (see <see cref="ConstructV3PumpModeUrl"/>). Returns null on
    ///     any failure — including a 403 from a stale patient code — so a mode-fetch problem degrades to
    ///     "no mode spans this pass" rather than failing the SSV2 sync.
    /// </summary>
    private async Task<GlookoV3GraphResponse?> FetchV3PumpModeGraphAsync(
        GlookoSyncContext context, DateTime startDate, DateTime endDate)
    {
        try
        {
            var patientCode = EnsureAuthenticatedAndGetCode(context);
            if (patientCode == null) return null;

            var url = ConstructV3PumpModeUrl(context, startDate, endDate);
            var result = await FetchFromGlookoEndpointWithRetry(context, url);
            if (!result.HasValue) return null;

            return JsonSerializer.Deserialize<GlookoV3GraphResponse>(result.Value.GetRawText());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "[{ConnectorSource}] Failed to fetch v3 pump-mode series; mode state spans skipped this pass",
                ConnectorSource);
            return null;
        }
    }

    // ── Sync orchestration ──────────────────────────────────────────────

    protected override async Task<SyncResult> PerformSyncInternalAsync(
        SyncRequest request,
        GlookoConnectorConfiguration config,
        CancellationToken cancellationToken)
    {
        var result = new SyncResult
        {
            Success = true,
            Message = SyncSucceededMessage,
            StartTime = DateTime.UtcNow
        };

        try
        {
            // See GlookoSyncContext: this run's entire working state lives here, never on the service.
            var context = new GlookoSyncContext(config, ConnectorSource, _glookoLogger);

            await ReportSyncMessageAsync(SyncMessageType.Authenticating, null, cancellationToken);

            if (!await AuthenticateWithConfigAsync(context))
            {
                result.Success = false;
                result.Message = "Authentication failed";
                result.Errors.Add("Authentication failed");
                return result;
            }

            var activeTypes = ResolveActiveTypes(request, config);

            // Resolve the tenant's timezone timeline before mapping any records. The account's home
            // zone (from the V3 profile) seeds the timeline's origin on first sync; thereafter the
            // user's travel/relocation entries drive per-record conversion. Falls back to the legacy
            // static offset when the timeline is empty (e.g. V2-only accounts, or profile tz unknown).
            await ConfigureTimezoneTimelineAsync(context, cancellationToken);

            // The request window is real-UTC; Glooko queries expect fake-UTC (local wall-clock). Pad by
            // a day each side so a non-zero offset between the two never clips edge data (dedup absorbs
            // the overlap).
            var from = request.From.HasValue
                ? context.TimeMapper.ToGlookoTime(request.From.Value).AddDays(-1)
                : context.TimeMapper.ToGlookoTime(DateTime.UtcNow.AddMonths(-6)).AddDays(-1);
            var to = context.TimeMapper.ToGlookoTime(request.To ?? DateTime.UtcNow).AddDays(1);

            var chunks = DateChunker.Chunk(from, to, GlookoConstants.SyncChunkSize).ToList();

            _logger.LogInformation(
                "[{ConnectorSource}] Syncing {From:yyyy-MM-dd} to {To:yyyy-MM-dd} in {ChunkCount} chunk(s)",
                ConnectorSource, from, to, chunks.Count);

            // Run the sync; if Glooko rejects the patient code (403 data_cant_view) the cached
            // glookoCode has gone stale (e.g. the account was re-linked), so re-authenticate once
            // to resolve the current code and retry from scratch. A second 403 propagates to the
            // outer handler and fails the sync rather than looping.
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    // A request carrying an explicit end is a reset/backfill: it rescans its window
                    // from the start. An open-ended background sync resumes incrementally.
                    await RunSyncPassAsync(
                        context, from, !request.To.HasValue, chunks, activeTypes, result, cancellationToken);
                    break;
                }
                catch (GlookoDataForbiddenException ex) when (attempt == 0)
                {
                    _logger.LogWarning(ex,
                        "[{ConnectorSource}] Glooko returned 403 (data_cant_view) for patient code {Code}; the account's "
                        + "glookoCode likely changed. Invalidating cached session and re-authenticating.",
                        ConnectorSource, context.PatientCode);

                    _tokenProvider.InvalidateToken();
                    context.ClearSessionAndProfile();

                    if (!await AuthenticateWithConfigAsync(context))
                    {
                        result.Success = false;
                        result.Message = "Re-authentication failed after Glooko denied data access";
                        result.Errors.Add("Re-authentication failed after Glooko returned 403 (data_cant_view)");
                        break;
                    }

                    await ConfigureTimezoneTimelineAsync(context, cancellationToken);

                    // Drop partial results from the aborted pass; the retry re-syncs from scratch
                    // with the refreshed patient code.
                    result.ItemsSynced.Clear();
                    result.Errors.Clear();
                    result.Success = true;
                    result.Message = SyncSucceededMessage;

                    _logger.LogInformation(
                        "[{ConnectorSource}] Re-authenticated after 403; retrying sync with patient code {Code}",
                        ConnectorSource, context.PatientCode);
                }
            }

            result.EndTime = DateTime.UtcNow;
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during Glooko batch sync");
            result.Success = false;
            result.Message = "Sync failed with exception";
            result.Errors.Add(ex.Message);
            result.EndTime = DateTime.UtcNow;
            return result;
        }
    }

    /// <summary>
    ///     Runs one full sync pass: every date chunk followed by the profile/device-settings fetch,
    ///     or the single cursor-driven SSV2 pass when the tenant is on it.
    ///     Throws <see cref="GlookoDataForbiddenException"/> when Glooko rejects the patient code, so
    ///     the caller can re-authenticate and retry with a refreshed code.
    /// </summary>
    private async Task RunSyncPassAsync(
        GlookoSyncContext context,
        DateTime from,
        bool incremental,
        List<(DateTime From, DateTime To)> chunks,
        HashSet<SyncDataType> activeTypes,
        SyncResult result,
        CancellationToken cancellationToken)
    {
        // SSV2 resumes from each resource's stored cursor rather than a date window, so it runs
        // once for the whole pass instead of per chunk, and brings its own profile source.
        if (context.Config.UseSsv2Sync)
        {
            await FetchAndMapViaSsv2Async(context, from, incremental, activeTypes, result, cancellationToken);
            return;
        }

        for (var i = 0; i < chunks.Count; i++)
        {
            var (chunkFrom, chunkTo) = chunks[i];

            await ReportSyncMessageAsync(SyncMessageType.FetchingData,
                new()
                {
                    ["from"] = chunkFrom.ToString("MMM dd"),
                    ["to"] = chunkTo.ToString("MMM dd"),
                    ["chunk"] = $"{i + 1}/{chunks.Count}",
                },
                cancellationToken);

            var chunkSuccess = context.Config.UseV3Api
                ? await FetchAndMapViaV3Async(context, chunkFrom, chunkTo, activeTypes, result, cancellationToken)
                : await FetchAndMapViaV2Async(context, chunkFrom, chunkTo, activeTypes, result, cancellationToken);

            if (!chunkSuccess)
            {
                _logger.LogWarning(
                    "[{ConnectorSource}] Chunk {Chunk}/{Total} ({From:yyyy-MM-dd} to {To:yyyy-MM-dd}) failed, stopping sync",
                    ConnectorSource, i + 1, chunks.Count, chunkFrom, chunkTo);
                result.Success = false;
                result.Message = FetchFailedMessage;
                result.Errors.Add($"Chunk {i + 1}/{chunks.Count} failed ({chunkFrom:yyyy-MM-dd} to {chunkTo:yyyy-MM-dd})");
                return;
            }

            _logger.LogInformation(
                "[{ConnectorSource}] Completed chunk {Chunk}/{Total} ({From:yyyy-MM-dd} to {To:yyyy-MM-dd})",
                ConnectorSource, i + 1, chunks.Count, chunkFrom, chunkTo);
        }

        // Profiles (V3 device settings — used in both modes, no V2 equivalent)
        await ReportSyncMessageAsync(SyncMessageType.ProcessingDataType,
            new() { ["dataType"] = SyncDataType.Profiles.ToString() }, cancellationToken);

        if (activeTypes.Contains(SyncDataType.Profiles))
        {
            try
            {
                var deviceSettings = await FetchV3DeviceSettingsAsync(context);
                if (deviceSettings is null)
                {
                    RecordFetchFailure(result, SyncDataType.Profiles, activeTypes);
                }
                else
                {
                    await PublishRecordTypeAsync(result, SyncDataType.Profiles, activeTypes,
                        context.ProfileMapper.TransformDeviceSettingsToProfiles(deviceSettings),
                        PublishProfileDataAsync, context.Config, cancellationToken,
                        "from device settings");

                    // The spans derive from the device settings but are state spans, so they gate and
                    // count under StateSpans like every other state-span publish, not under Profiles.
                    await PublishRecordTypeAsync(result, SyncDataType.StateSpans, activeTypes,
                        context.ProfileMapper.TransformDeviceSettingsToStateSpans(deviceSettings),
                        PublishStateSpanDataAsync, context.Config, cancellationToken,
                        "device settings");
                }
            }
            catch (GlookoDataForbiddenException) { throw; }
            catch (Exception profileEx)
            {
                _logger.LogWarning(profileEx, "[{ConnectorSource}] Failed to fetch/publish profile data", ConnectorSource);
                RecordFetchFailure(result, SyncDataType.Profiles, activeTypes);
            }
        }
    }

    // ── V2 fetch + map ──────────────────────────────────────────────────

    /// <summary>
    ///     Fetches from all V2 endpoints, maps each record type, and publishes inline.
    /// </summary>
    private async Task<bool> FetchAndMapViaV2Async(
        GlookoSyncContext context,
        DateTime fromDate,
        DateTime toDate,
        HashSet<SyncDataType> activeTypes,
        SyncResult result,
        CancellationToken cancellationToken)
    {
        var batchData = await FetchBatchDataAsync(context, fromDate, toDate, activeTypes, result);
        if (batchData == null) return false;

        return await MapAndPublishV2BatchAsync(context, batchData, activeTypes, result, cancellationToken);
    }

    /// <summary>
    ///     Maps and publishes a populated <see cref="GlookoBatchData"/> (glucose, manual BG, treatments,
    ///     foods, state spans, temp basals). Shared by the date-windowed V2 path and the SSV2 cursor
    ///     path, which differ only in how the batch is fetched.
    /// </summary>
    private async Task<bool> MapAndPublishV2BatchAsync(
        GlookoSyncContext context,
        GlookoBatchData batchData,
        HashSet<SyncDataType> activeTypes,
        SyncResult result,
        CancellationToken cancellationToken)
    {
        var config = context.Config;

        var sensorGlucose = context.SensorGlucoseMapper.TransformBatchDataToSensorGlucose(batchData).ToList();
        await PublishRecordTypeAsync(result, SyncDataType.Glucose, activeTypes,
            sensorGlucose, PublishSensorGlucoseDataAsync, config, cancellationToken);

        var bgChecks = context.SensorGlucoseMapper.TransformBatchDataToBGChecks(batchData).ToList();
        await PublishRecordTypeAsync(result, SyncDataType.ManualBG, activeTypes,
            bgChecks, PublishBGCheckDataAsync, config, cancellationToken);

        var (boluses, carbs, _) = context.V4TreatmentMapper.MapBatchData(batchData);

        await PublishRecordTypeAsync(result, SyncDataType.Boluses, activeTypes,
            boluses, PublishBolusDataAsync, config, cancellationToken);

        await PublishRecordTypeAsync(result, SyncDataType.CarbIntake, activeTypes,
            carbs, PublishCarbIntakeDataAsync, config, cancellationToken);

        // Food attribution resolves against the carbs published above.
        var foodEntryImports = batchData.Foods is { Length: > 0 }
            ? context.V4TreatmentMapper.MapFoodsToConnectorEntries(batchData) : [];
        Func<string, string?> foodResolver = externalEntryId => $"glooko_food_{externalEntryId}";
        await PublishFoodEntriesAndAttributeAsync(
            foodEntryImports, carbs, foodResolver, result, activeTypes, cancellationToken);

        await PublishRecordTypeAsync(result, SyncDataType.StateSpans, activeTypes,
            context.StateSpanMapper.TransformV2ToStateSpans(batchData),
            PublishStateSpanDataAsync, config, cancellationToken);

        await PublishRecordTypeAsync(result, SyncDataType.TempBasals, activeTypes,
            context.TempBasalMapper.TransformV2ToTempBasals(batchData),
            PublishTempBasalDataAsync, config, cancellationToken);

        return true;
    }

    // ── V3 fetch + map ──────────────────────────────────────────────────

    /// <summary>
    ///     Fetches from V3 graph/data and histories endpoints, maps each record type, and publishes inline.
    /// </summary>
    private async Task<bool> FetchAndMapViaV3Async(
        GlookoSyncContext context,
        DateTime fromDate,
        DateTime toDate,
        HashSet<SyncDataType> activeTypes,
        SyncResult result,
        CancellationToken cancellationToken)
    {
        var config = context.Config;

        _logger.LogInformation("[{ConnectorSource}] Fetching data from v3 API...", ConnectorSource);

        var v3Data = await FetchV3GraphDataAsync(context, fromDate, toDate);
        if (v3Data == null) return false;

        // Histories carry the meals: without them carbs fall back to the coarser carbAll series and
        // food entries have no source at all, so the run reports both types as unfetched.
        var v3Histories = await FetchV3HistoriesAsync(context, fromDate, toDate);
        if (v3Histories is null)
            RecordFetchFailure(result, SyncDataType.CarbIntake, activeTypes);

        if (config.V3IncludeCgmBackfill)
        {
            var sensorGlucose = context.SensorGlucoseMapper.TransformV3ToSensorGlucose(v3Data, context.MeterUnits).ToList();
            await PublishRecordTypeAsync(result, SyncDataType.Glucose, activeTypes,
                sensorGlucose, PublishSensorGlucoseDataAsync, config, cancellationToken);
        }

        var bgChecks = context.SensorGlucoseMapper.TransformV3ToBGChecks(v3Data, context.MeterUnits).ToList();
        await PublishRecordTypeAsync(result, SyncDataType.ManualBG, activeTypes,
            bgChecks, PublishBGCheckDataAsync, config, cancellationToken);

        var (v3Boluses, v3BolusCarbIntakes, _) = context.V4TreatmentMapper.MapV3Boluses(v3Data);

        // Carbs: bolus wizard + history meals (preferred) or carbAll (fallback)
        var allCarbs = new List<CarbIntake>(v3BolusCarbIntakes);
        var historyMealCarbs = v3Histories?.Histories != null
            ? context.V4TreatmentMapper.MapV3HistoryMealsToCarbIntakes(v3Histories) : [];

        if (historyMealCarbs.Count > 0)
            allCarbs.AddRange(historyMealCarbs);
        else
            allCarbs.AddRange(context.V4TreatmentMapper.MapV3CarbAll(v3Data));

        await PublishRecordTypeAsync(result, SyncDataType.Boluses, activeTypes,
            v3Boluses, PublishBolusDataAsync, config, cancellationToken);

        await PublishRecordTypeAsync(result, SyncDataType.CarbIntake, activeTypes,
            allCarbs, PublishCarbIntakeDataAsync, config, cancellationToken);

        // Pen injections: gkInsulinBasal → BasalInjection, gkInsulinBolus → Bolus.
        var (manualBasalInjections, manualBoluses) = context.V4TreatmentMapper.MapV3ManualInsulin(v3Data);

        await PublishRecordTypeAsync(result, SyncDataType.Boluses, activeTypes,
            manualBoluses, PublishBolusDataAsync, config, cancellationToken);

        await PublishRecordTypeAsync(result, SyncDataType.BasalInjections, activeTypes,
            manualBasalInjections, PublishBasalInjectionDataAsync, config, cancellationToken);

        // Food attribution resolves against the carbs published above.
        if (v3Histories is null)
        {
            RecordFetchFailure(result, SyncDataType.Food, activeTypes);
        }
        else
        {
            GlookoFood[]? v2Foods = null;
            if (historyMealCarbs.Count > 0 && activeTypes.Contains(SyncDataType.Food))
            {
                // V2 foods only enrich the entries with externalId/brand, so a failure here is
                // sticky rather than fatal: the entries below still publish without that metadata.
                v2Foods = await FetchV2FoodsAsync(context, fromDate, toDate);
                if (v2Foods is null)
                    RecordFetchFailure(result, SyncDataType.Food, activeTypes);
            }

            var foodEntryImports = historyMealCarbs.Count > 0 && v3Histories.Histories != null
                ? context.V4TreatmentMapper.MapV3HistoryMealsToConnectorEntries(v3Histories, v2Foods) : [];

            Func<string, string?>? foodResolver = null;
            if (historyMealCarbs.Count > 0 && v3Histories.Histories != null)
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
                foodEntryImports, allCarbs, foodResolver, result, activeTypes, cancellationToken);
        }

        var stateSpans = context.StateSpanMapper.TransformV3ToStateSpans(v3Data);
        stateSpans.AddRange(context.StateSpanMapper.TransformV3PumpModeToStateSpans(v3Data));
        await PublishRecordTypeAsync(result, SyncDataType.StateSpans, activeTypes,
            stateSpans, PublishStateSpanDataAsync, config, cancellationToken);

        await PublishRecordTypeAsync(result, SyncDataType.TempBasals, activeTypes,
            context.TempBasalMapper.TransformV3ToTempBasals(v3Data),
            PublishTempBasalDataAsync, config, cancellationToken);

        // Device events and system events share one ItemsSynced entry — see
        // <see cref="BaseConnectorService{TConfig}.PublishSystemEventDataAsync"/>.
        await PublishRecordTypeAsync(result, SyncDataType.DeviceEvents, activeTypes,
            context.V4TreatmentMapper.MapV3DeviceEvents(v3Data),
            PublishDeviceEventDataAsync, config, cancellationToken);

        await PublishRecordTypeAsync(result, SyncDataType.DeviceEvents, activeTypes,
            context.SystemEventMapper.TransformV3ToSystemEvents(v3Data),
            PublishSystemEventDataAsync, config, cancellationToken);

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
        SyncResult result,
        HashSet<SyncDataType> activeTypes,
        CancellationToken cancellationToken)
    {
        if (!activeTypes.Contains(SyncDataType.Food))
            return;

        if (foodEntryImports.Count == 0)
        {
            RecordPublishOutcome(result, SyncDataType.Food, 0, success: true);
            return;
        }

        if (_connectorPublisher is not { IsAvailable: true })
        {
            _logger.LogWarning("Publisher not available for food entry submission");
            RecordPublishOutcome(result, SyncDataType.Food, foodEntryImports.Count, success: false);
            return;
        }

        var importedEntries = await _connectorPublisher.Metadata.PublishConnectorFoodEntriesAsync(
            foodEntryImports, ConnectorSource, WriteOrigin.Live, cancellationToken); // Food is a dormant broadcast category — origin irrelevant until wired.

        // The publisher returns null only from its own catch; an import that reached the catalog
        // returns a list, empty when nothing was accepted.
        RecordPublishOutcome(result, SyncDataType.Food, foodEntryImports.Count, importedEntries is not null);

        if (importedEntries is null || importedEntries.Count == 0)
            return;

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

    // ── V2 batch data fetching ──────────────────────────────────────────

    /// <summary>
    ///     Fetches comprehensive batch data from all v2 Glooko endpoints.
    /// </summary>
    /// <remarks>
    ///     One endpoint being down costs only the types it carries, so each is recorded through
    ///     <see cref="BaseConnectorService{TConfig}.RecordFetchFailure"/> and the rest of the batch
    ///     still fetches and publishes.
    /// </remarks>
    private async Task<GlookoBatchData?> FetchBatchDataAsync(
        GlookoSyncContext context,
        DateTime fromDate,
        DateTime toDate,
        HashSet<SyncDataType> activeTypes,
        SyncResult result)
    {
        try
        {
            var patientCode = EnsureAuthenticatedAndGetCode(context);
            if (patientCode == null) return null;

            _logger.LogInformation("Fetching comprehensive Glooko data from {From:yyyy-MM-dd} to {To:yyyy-MM-dd}", fromDate, toDate);

            var batchData = new GlookoBatchData();

            // An endpoint's types are what its payload feeds downstream in FetchAndMapViaV2Async, not
            // what the endpoint is named: foods become carb intakes as well as catalog entries, a bolus
            // carries its own wizard carbs, and all three basal endpoints map to temp basals (suspends
            // additionally to a pump-mode span).
            var endpointDefinitions = new (string Endpoint, SyncDataType[] Types, Action<JsonElement> Handler)[]
            {
                (GlookoConstants.FoodsPath, [SyncDataType.CarbIntake, SyncDataType.Food], json =>
                {
                    if (json.TryGetProperty("foods", out var el))
                        batchData.Foods = JsonSerializer.Deserialize<GlookoFood[]>(el.GetRawText()) ?? [];
                }),
                (GlookoConstants.ScheduledBasalsPath, [SyncDataType.TempBasals], json =>
                {
                    if (json.TryGetProperty("scheduledBasals", out var el))
                        batchData.ScheduledBasals = JsonSerializer.Deserialize<GlookoBasal[]>(el.GetRawText()) ?? [];
                }),
                (GlookoConstants.NormalBolusesPath, [SyncDataType.Boluses, SyncDataType.CarbIntake], json =>
                {
                    if (json.TryGetProperty("normalBoluses", out var el))
                        batchData.NormalBoluses = JsonSerializer.Deserialize<GlookoBolus[]>(el.GetRawText()) ?? [];
                }),
                (GlookoConstants.CgmReadingsPath, [SyncDataType.Glucose], json =>
                {
                    if (json.TryGetProperty("readings", out var el))
                        batchData.Readings = JsonSerializer.Deserialize<GlookoCgmReading[]>(el.GetRawText()) ?? [];
                }),
                (GlookoConstants.MeterReadingsPath, [SyncDataType.ManualBG], json =>
                {
                    if (json.TryGetProperty("readings", out var el))
                        batchData.MeterReadings = JsonSerializer.Deserialize<GlookoMeterReading[]>(el.GetRawText()) ?? [];
                }),
                (GlookoConstants.SuspendBasalsPath, [SyncDataType.StateSpans, SyncDataType.TempBasals], json =>
                {
                    if (json.TryGetProperty("suspendBasals", out var el))
                        batchData.SuspendBasals = JsonSerializer.Deserialize<GlookoSuspendBasal[]>(el.GetRawText()) ?? [];
                }),
                (GlookoConstants.TemporaryBasalsPath, [SyncDataType.TempBasals], json =>
                {
                    if (json.TryGetProperty("temporaryBasals", out var el))
                        batchData.TempBasals = JsonSerializer.Deserialize<GlookoTempBasal[]>(el.GetRawText()) ?? [];
                }),
            };

            for (var i = 0; i < endpointDefinitions.Length; i++)
            {
                var (endpoint, types, handler) = endpointDefinitions[i];
                var url = ConstructV2Url(context, endpoint, fromDate, toDate);

                await _rateLimitingStrategy.ApplyDelayAsync(i);

                try
                {
                    var fetchResult = await FetchFromGlookoEndpointWithRetry(context, url);
                    if (fetchResult.HasValue)
                        handler(fetchResult.Value);
                }
                catch (GlookoDataForbiddenException) { throw; }
                catch (Exception ex)
                {
                    // A payload that arrived but would not parse loses the same data as one that never
                    // arrived, so both land here.
                    _logger.LogWarning(ex,
                        "Failed to fetch or parse {Endpoint}. Continuing with other endpoints.", endpoint);

                    foreach (var type in types)
                        RecordFetchFailure(result, type, activeTypes);
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
        catch (GlookoDataForbiddenException) { throw; }
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
    private async Task<GlookoFood[]?> FetchV2FoodsAsync(
        GlookoSyncContext context, DateTime fromDate, DateTime toDate)
    {
        try
        {
            var patientCode = EnsureAuthenticatedAndGetCode(context);
            if (patientCode == null) return null;

            var url = ConstructV2Url(context, GlookoConstants.FoodsPath, fromDate, toDate);
            var result = await FetchFromGlookoEndpointWithRetry(context, url);
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

    // ── SSV2 granular sync ──────────────────────────────────────────────

    /// <summary>
    ///     Fetches every record of an SSV2 resource via cursor pagination, returning the raw records for the
    ///     caller to map. In incremental mode the scan resumes from (and persists) the stored per-resource
    ///     cursor so only server-side updates since the last sync are pulled; in explicit-range mode (the
    ///     reset/backfill path, signalled by a non-null sync window) the stored cursor is bypassed and the
    ///     scan runs from the beginning, leaving the incremental cursor untouched.
    /// </summary>
    /// <param name="resource">SSV2 resource path (e.g. <c>/api/v2/cgm/egvs</c>).</param>
    /// <param name="selectRecords">Projects a deserialized page to its record array.</param>
    /// <param name="incremental">When true, resume from and persist the stored cursor.</param>
    /// <param name="startDate">Optional clinical-time floor (fake-UTC). Required by egvs; omitted otherwise.</param>
    private async Task<List<TRecord>> FetchSsv2Async<TPage, TRecord>(
        GlookoSyncContext context,
        string resource,
        Func<TPage, TRecord[]?> selectRecords,
        bool incremental,
        DateTime? startDate)
        where TPage : GlookoSsv2Page
    {
        EnsureAuthenticatedAndGetCode(context);

        var stored = incremental && _cursorStore != null
            ? await _cursorStore.GetAsync(ServiceName, resource)
            : null;

        var initialUpdatedAt = stored?.LastUpdatedAt ?? GlookoConstants.Ssv2InitialLastUpdatedAt;
        var initialGuid = stored?.LastGuid ?? GlookoConstants.Ssv2InitialLastGuid;
        var lastUpdatedAt = initialUpdatedAt;
        var lastGuid = initialGuid;

        var all = new List<TRecord>();

        for (var page = 0; page < GlookoConstants.Ssv2MaxPages; page++)
        {
            var url = ConstructSsv2Url(resource, startDate, lastUpdatedAt, lastGuid);
            var result = await FetchFromGlookoEndpointWithRetry(context, url);
            if (!result.HasValue) break;

            var pageData = JsonSerializer.Deserialize<TPage>(result.Value.GetRawText());
            var batch = pageData == null ? null : selectRecords(pageData);

            if (batch is { Length: > 0 })
                all.AddRange(batch);

            // Stop on a null/empty page.
            if (pageData == null || batch is not { Length: > 0 })
                break;

            // Advance to this page's resume watermark *before* the last-page check, so the final page's
            // cursor is captured too — otherwise the next incremental sync re-fetches that page.
            var prevUpdatedAt = lastUpdatedAt;
            var prevGuid = lastGuid;
            lastUpdatedAt = pageData.LastUpdatedAt ?? lastUpdatedAt;
            lastGuid = pageData.LastGuid ?? lastGuid;

            if (pageData.LastPage)
                break;

            // Loop guard: a non-last page that fails to move the cursor would otherwise spin forever.
            if (lastUpdatedAt == prevUpdatedAt && lastGuid == prevGuid)
            {
                _logger.LogWarning("[{ConnectorSource}] SSV2 {Resource} cursor did not advance; stopping pagination",
                    ConnectorSource, resource);
                break;
            }
        }

        // Persist only for incremental scans, and only when the cursor actually advanced from where this
        // run started (so a no-op pass never rewrites the stored watermark or the epoch default).
        if (incremental && _cursorStore != null
            && (lastUpdatedAt != initialUpdatedAt || lastGuid != initialGuid))
            await _cursorStore.SetAsync(ServiceName, resource, new ConnectorSyncCursor(lastUpdatedAt, lastGuid));

        _logger.LogInformation("[{ConnectorSource}] SSV2 {Resource} fetched {Count} records (incremental={Incremental})",
            ConnectorSource, resource, all.Count, incremental);
        return all;
    }

    /// <summary>
    ///     Fetches the granular <c>cgm/egvs</c> stream and maps it to SensorGlucose.
    /// </summary>
    internal async Task<List<SensorGlucose>> FetchSsv2EgvsAsync(
        GlookoSyncContext context, bool incremental, DateTime? startDate)
    {
        var egvs = await FetchSsv2Async<GlookoEgvPage, GlookoEgv>(
            context, GlookoConstants.Ssv2EgvsPath, p => p.Egvs, incremental, startDate);
        return context.SensorGlucoseMapper.TransformEgvsToSensorGlucose(egvs).ToList();
    }

    /// <summary>
    ///     SSV2 sync pass: glucose from the granular egvs feed, and boluses / carbs / manual BG / state
    ///     spans / temp basals via the same v2 batch mappers but fetched incrementally by cursor. In
    ///     incremental mode each resource omits <c>startDate</c> and relies solely on its stored cursor;
    ///     a backfill passes the clinical floor and bypasses the cursor.
    ///     Each resource is fetched in isolation: if one feed fails (network, server error, malformed
    ///     page) it is logged and skipped so the rest of the pass still imports, mirroring the
    ///     per-endpoint resilience of the windowed batch path.
    /// </summary>
    private async Task FetchAndMapViaSsv2Async(
        GlookoSyncContext context,
        DateTime from,
        bool incremental,
        HashSet<SyncDataType> activeTypes,
        SyncResult result,
        CancellationToken cancellationToken)
    {
        var config = context.Config;

        // Incremental syncs resume purely from each resource's stored cursor; an explicit-range backfill
        // passes the clinical floor. (egvs ignores startDate server-side — its cursor is authoritative —
        // but it is kept consistent with the other resources rather than special-cased.)
        DateTime? batchStart = incremental ? null : from;

        if (activeTypes.Contains(SyncDataType.Glucose))
        {
            var egvGlucose = await FetchSsv2SafelyAsync(
                GlookoConstants.Ssv2EgvsPath,
                () => FetchSsv2EgvsAsync(context, incremental, batchStart),
                new List<SensorGlucose>());
            if (egvGlucose.Count > 0)
            {
                await PublishRecordTypeAsync(result, SyncDataType.Glucose, activeTypes,
                    egvGlucose, PublishSensorGlucoseDataAsync, config, cancellationToken);
            }
        }

        var batchData = new GlookoBatchData
        {
            NormalBoluses = await FetchSsv2BatchResourceAsync<GlookoNormalBolusPage, GlookoBolus>(
                context, GlookoConstants.NormalBolusesPath, p => p.NormalBoluses, incremental, batchStart),
            ScheduledBasals = await FetchSsv2BatchResourceAsync<GlookoScheduledBasalPage, GlookoBasal>(
                context, GlookoConstants.ScheduledBasalsPath, p => p.ScheduledBasals, incremental, batchStart),
            TempBasals = await FetchSsv2BatchResourceAsync<GlookoTemporaryBasalPage, GlookoTempBasal>(
                context, GlookoConstants.TemporaryBasalsPath, p => p.TemporaryBasals, incremental, batchStart),
            SuspendBasals = await FetchSsv2BatchResourceAsync<GlookoSuspendBasalPage, GlookoSuspendBasal>(
                context, GlookoConstants.SuspendBasalsPath, p => p.SuspendBasals, incremental, batchStart),
            MeterReadings = await FetchSsv2BatchResourceAsync<GlookoMeterReadingPage, GlookoMeterReading>(
                context, GlookoConstants.MeterReadingsPath, p => p.Readings, incremental, batchStart),
            Foods = await FetchSsv2BatchResourceAsync<GlookoFoodPage, GlookoFood>(
                context, GlookoConstants.FoodsPath, p => p.Foods, incremental, batchStart),
        };

        await MapAndPublishV2BatchAsync(context, batchData, activeTypes, result, cancellationToken);

        // Pump-mode state spans (auto/manual/sleep/exercise/...) — the ONE thing with no SSV2 source
        // (confirmed by reverse-engineering the app: only aggregate mode % is exposed, never per-interval
        // spans). Keep a single slim v3 graph/data call requesting ONLY the pump-mode series, fed into the
        // existing mapper. Windowed to a few recent days on incremental syncs (modes don't change
        // retroactively; dedup absorbs overlap), full range on backfill. Additional to the basal-derived
        // state spans from MapAndPublishV2BatchAsync; degrades to none on failure.
        if (activeTypes.Contains(SyncDataType.StateSpans))
        {
            var modeTo = context.TimeMapper.ToGlookoTime(DateTime.UtcNow).AddDays(1);
            var modeFrom = incremental ? modeTo.AddDays(-3) : from;
            var modeData = await FetchV3PumpModeGraphAsync(context, modeFrom, modeTo);
            if (modeData != null)
            {
                var modeSpans = context.StateSpanMapper.TransformV3PumpModeToStateSpans(modeData);
                if (modeSpans.Count > 0 && await PublishStateSpanDataAsync(modeSpans, config, cancellationToken))
                    result.ItemsSynced[SyncDataType.StateSpans] =
                        result.ItemsSynced.GetValueOrDefault(SyncDataType.StateSpans) + modeSpans.Count;
            }
        }

        // Pen injections — manual insulin logged via pen: injection_boluses → Bolus, injection_basals →
        // BasalInjection. The v3 path covers these via its gkInsulin* series; the windowed v2 batch path
        // does not. Critical for MDI users, who have no pump bolus/basal data at all.
        if (activeTypes.Contains(SyncDataType.Boluses) || activeTypes.Contains(SyncDataType.BasalInjections))
        {
            var injectionBoluses = await FetchSsv2SafelyAsync(
                GlookoConstants.Ssv2InjectionBolusesPath,
                () => FetchSsv2Async<GlookoInjectionBolusPage, GlookoInjectionInsulin>(
                    context, GlookoConstants.Ssv2InjectionBolusesPath, p => p.InjectionBoluses, incremental, batchStart),
                new List<GlookoInjectionInsulin>());
            var injectionBasals = await FetchSsv2SafelyAsync(
                GlookoConstants.Ssv2InjectionBasalsPath,
                () => FetchSsv2Async<GlookoInjectionBasalPage, GlookoInjectionInsulin>(
                    context, GlookoConstants.Ssv2InjectionBasalsPath, p => p.InjectionBasals, incremental, batchStart),
                new List<GlookoInjectionInsulin>());

            var (penBasals, penBoluses) = context.V4TreatmentMapper.MapSsv2InjectionInsulin(injectionBasals, injectionBoluses);

            await PublishRecordTypeAsync(result, SyncDataType.Boluses, activeTypes,
                penBoluses, PublishBolusDataAsync, config, cancellationToken);
            await PublishRecordTypeAsync(result, SyncDataType.BasalInjections, activeTypes,
                penBasals, PublishBasalInjectionDataAsync, config, cancellationToken);
        }

        // App-logged insulin doses — cgm/insulin_events: doses logged in the app by CGM-only/MDI users,
        // not pump-delivered. "fast_acting" → rapid Bolus, "long_acting"/"intermediate" → BasalInjection.
        // Distinct from the pen-injection feeds above (which carry a product name); this feed has none, so
        // DIA/peak is resolved by category.
        if (activeTypes.Contains(SyncDataType.Boluses) || activeTypes.Contains(SyncDataType.BasalInjections))
        {
            var insulinEvents = await FetchSsv2SafelyAsync(
                GlookoConstants.Ssv2InsulinEventsPath,
                () => FetchSsv2Async<GlookoInsulinEventPage, GlookoSsv2InsulinEvent>(
                    context, GlookoConstants.Ssv2InsulinEventsPath, p => p.InsulinEvents, incremental, batchStart),
                new List<GlookoSsv2InsulinEvent>());

            var (eventBasals, eventBoluses) = context.V4TreatmentMapper.MapSsv2InsulinEvents(insulinEvents);

            await PublishRecordTypeAsync(result, SyncDataType.Boluses, activeTypes,
                eventBoluses, PublishBolusDataAsync, config, cancellationToken);
            await PublishRecordTypeAsync(result, SyncDataType.BasalInjections, activeTypes,
                eventBasals, PublishBasalInjectionDataAsync, config, cancellationToken);
        }

        // Extended/dual-wave boluses — square (all-extended) or dual (immediate + extended) deliveries
        // with a duration. Net-new vs the windowed path and the v3 graph (no extended-bolus series).
        if (activeTypes.Contains(SyncDataType.Boluses))
        {
            var extended = await FetchSsv2SafelyAsync(
                GlookoConstants.Ssv2ExtendedBolusesPath,
                () => FetchSsv2Async<GlookoExtendedBolusPage, GlookoExtendedBolus>(
                    context, GlookoConstants.Ssv2ExtendedBolusesPath, p => p.ExtendedBoluses, incremental, batchStart),
                new List<GlookoExtendedBolus>());
            var extendedBoluses = context.V4TreatmentMapper.MapSsv2ExtendedBoluses(extended);
            await PublishRecordTypeAsync(result, SyncDataType.Boluses, activeTypes,
                extendedBoluses, PublishBolusDataAsync, config, cancellationToken);
        }

        // Standalone carbs — app-logged carb entries not tied to a bolus (v3 carbAll equivalent),
        // additional to the carbs derived from bolus.carbsInput + foods in MapAndPublishV2BatchAsync
        // (PublishRecordTypeAsync accumulates the count).
        if (activeTypes.Contains(SyncDataType.CarbIntake))
        {
            var carbsEvents = await FetchSsv2SafelyAsync(
                GlookoConstants.Ssv2CarbsEventsPath,
                () => FetchSsv2Async<GlookoCarbsEventPage, GlookoSsv2CarbsEvent>(
                    context, GlookoConstants.Ssv2CarbsEventsPath, p => p.CarbsEvents, incremental, batchStart),
                new List<GlookoSsv2CarbsEvent>());
            var standaloneCarbs = context.V4TreatmentMapper.MapSsv2CarbsEvents(carbsEvents);
            await PublishRecordTypeAsync(result, SyncDataType.CarbIntake, activeTypes,
                standaloneCarbs, PublishCarbIntakeDataAsync, config, cancellationToken);
        }

        // Notes — app-logged free-text notes (camelCase /api/v2/notes) → Note.
        if (activeTypes.Contains(SyncDataType.Notes))
        {
            var rawNotes = await FetchSsv2SafelyAsync(
                GlookoConstants.Ssv2NotesPath,
                () => FetchSsv2Async<GlookoNotePage, GlookoSsv2Note>(
                    context, GlookoConstants.Ssv2NotesPath, p => p.Notes, incremental, batchStart),
                new List<GlookoSsv2Note>());
            var notes = context.NoteMapper.MapSsv2Notes(rawNotes);
            await PublishRecordTypeAsync(result, SyncDataType.Notes, activeTypes,
                notes, PublishNoteDataAsync, config, cancellationToken);
        }

        // Activities — two app-logged exercise sources mapped to Activity: exercises (seconds duration,
        // numeric intensity) and cgm/exercise_events (minutes duration, string intensity). Both normalize
        // to minutes. PublishRecordTypeAsync accumulates the count across the two sources.
        if (activeTypes.Contains(SyncDataType.Activity))
        {
            var rawExercises = await FetchSsv2SafelyAsync(
                GlookoConstants.Ssv2ExercisesPath,
                () => FetchSsv2Async<GlookoExercisePage, GlookoSsv2Exercise>(
                    context, GlookoConstants.Ssv2ExercisesPath, p => p.Exercises, incremental, batchStart),
                new List<GlookoSsv2Exercise>());
            var exerciseActivities = context.ActivityMapper.MapSsv2Exercises(rawExercises);
            await PublishRecordTypeAsync(result, SyncDataType.Activity, activeTypes,
                exerciseActivities, PublishActivityDataAsync, config, cancellationToken);

            var rawExerciseEvents = await FetchSsv2SafelyAsync(
                GlookoConstants.Ssv2ExerciseEventsPath,
                () => FetchSsv2Async<GlookoExerciseEventPage, GlookoSsv2ExerciseEvent>(
                    context, GlookoConstants.Ssv2ExerciseEventsPath, p => p.ExerciseEvents, incremental, batchStart),
                new List<GlookoSsv2ExerciseEvent>());
            var exerciseEventActivities = context.ActivityMapper.MapSsv2ExerciseEvents(rawExerciseEvents);
            await PublishRecordTypeAsync(result, SyncDataType.Activity, activeTypes,
                exerciseEventActivities, PublishActivityDataAsync, config, cancellationToken);

            // Biometric/health series — body weight, daily step counts, and resting heart rate. These map to
            // the Core BodyWeight/StepCount/HeartRate models (not V4), upserted by deterministic Id via the
            // Metadata publisher. Gated under Activity (the closest existing biometric gate; there is no
            // SyncDataType for weight/steps/HR). Not routed through PublishRecordTypeAsync — its count is
            // keyed by SyncDataType, and these would otherwise inflate the Activity count.

            // Body weight — two sources: manual/HealthKit (grams) + third-party/Validic (kilograms).
            var weights = await FetchSsv2SafelyAsync(
                GlookoConstants.Ssv2WeightsPath,
                () => FetchSsv2Async<GlookoWeightPage, GlookoSsv2Weight>(
                    context, GlookoConstants.Ssv2WeightsPath, p => p.Weights, incremental, batchStart),
                new List<GlookoSsv2Weight>());
            var validicWeights = await FetchSsv2SafelyAsync(
                GlookoConstants.Ssv2ValidicWeightsPath,
                () => FetchSsv2Async<GlookoValidicWeightPage, GlookoSsv2ValidicWeight>(
                    context, GlookoConstants.Ssv2ValidicWeightsPath, p => p.Weights, incremental, batchStart),
                new List<GlookoSsv2ValidicWeight>());

            var bodyWeights = context.BodyWeightMapper.MapSsv2Weights(weights);
            bodyWeights.AddRange(context.BodyWeightMapper.MapSsv2ValidicWeights(validicWeights));
            if (bodyWeights.Count > 0)
                await PublishBodyWeightDataAsync(bodyWeights, config, cancellationToken);

            // Daily step counts — validic/routines (per-day total).
            var routines = await FetchSsv2SafelyAsync(
                GlookoConstants.Ssv2RoutinesPath,
                () => FetchSsv2Async<GlookoRoutinePage, GlookoSsv2Routine>(
                    context, GlookoConstants.Ssv2RoutinesPath, p => p.Routines, incremental, batchStart),
                new List<GlookoSsv2Routine>());
            var stepCounts = context.StepCountMapper.MapSsv2Routines(routines);
            if (stepCounts.Count > 0)
                await PublishStepCountDataAsync(stepCounts, config, cancellationToken);

            // Resting heart rate — the only HR-bearing SSV2 source (validic/biometric_measurements);
            // most records carry other vitals and no HR, so the mapper skips those.
            var biometrics = await FetchSsv2SafelyAsync(
                GlookoConstants.Ssv2BiometricMeasurementsPath,
                () => FetchSsv2Async<GlookoBiometricMeasurementPage, GlookoSsv2BiometricMeasurement>(
                    context, GlookoConstants.Ssv2BiometricMeasurementsPath, p => p.BiometricMeasurements, incremental, batchStart),
                new List<GlookoSsv2BiometricMeasurement>());
            var heartRates = context.HeartRateMapper.MapSsv2BiometricMeasurements(biometrics);
            if (heartRates.Count > 0)
                await PublishHeartRateDataAsync(heartRates, config, cancellationToken);
        }

        // Device events — granular pumps/events feed (reservoir/site/cannula changes) plus pump alarms
        // (→ system events). Net-new for SSV2 vs the windowed batch path; the v3 path derives both from
        // its graph series. Both are reported under the DeviceEvents count, matching the v3 path.
        if (activeTypes.Contains(SyncDataType.DeviceEvents))
        {
            var deviceEventCount = 0;

            var pumpEvents = await FetchSsv2SafelyAsync(
                GlookoConstants.Ssv2PumpEventsPath,
                () => FetchSsv2Async<GlookoPumpEventPage, GlookoPumpEvent>(
                    context, GlookoConstants.Ssv2PumpEventsPath, p => p.Events, incremental, batchStart),
                new List<GlookoPumpEvent>());
            var deviceEvents = context.PumpEventMapper.TransformPumpEventsToDeviceEvents(pumpEvents);
            if (deviceEvents.Count > 0 && await PublishDeviceEventDataAsync(deviceEvents, config, cancellationToken))
                deviceEventCount += deviceEvents.Count;

            var alarms = await FetchSsv2SafelyAsync(
                GlookoConstants.Ssv2AlarmsPath,
                () => FetchSsv2Async<GlookoSsv2AlarmPage, GlookoSsv2Alarm>(
                    context, GlookoConstants.Ssv2AlarmsPath, p => p.Alarms, incremental, batchStart),
                new List<GlookoSsv2Alarm>());
            var systemEvents = context.SystemEventMapper.TransformSsv2AlarmsToSystemEvents(alarms);
            if (systemEvents.Count > 0 && await PublishSystemEventDataAsync(systemEvents, config, cancellationToken))
                deviceEventCount += systemEvents.Count;

            if (deviceEventCount > 0)
                result.ItemsSynced[SyncDataType.DeviceEvents] = deviceEventCount;

            // Patient hardware inventory — the pumps / cgm_devices feeds map to PatientDevice (the user's
            // pump + CGM). Gated under DeviceEvents as the closest existing device gate (there is no
            // SyncDataType for hardware inventory). Upserted via IDevicePublisher.PublishPatientDevicesAsync
            // keyed on the mapper's deterministic Id, so re-syncs update in place.
            var pumpDevices = await FetchSsv2SafelyAsync(
                GlookoConstants.Ssv2PumpsPath,
                () => FetchSsv2Async<GlookoPumpDevicePage, GlookoSsv2Device>(
                    context, GlookoConstants.Ssv2PumpsPath, p => p.Pumps, incremental, batchStart),
                new List<GlookoSsv2Device>());
            var cgmDevices = await FetchSsv2SafelyAsync(
                GlookoConstants.Ssv2CgmDevicesPath,
                () => FetchSsv2Async<GlookoCgmDevicePage, GlookoSsv2Device>(
                    context, GlookoConstants.Ssv2CgmDevicesPath, p => p.CgmDevices, incremental, batchStart),
                new List<GlookoSsv2Device>());

            var patientDevices = context.DeviceMapper.TransformPumpsToPatientDevices(pumpDevices);
            patientDevices.AddRange(context.DeviceMapper.TransformCgmDevicesToPatientDevices(cgmDevices));
            if (patientDevices.Count > 0 && _connectorPublisher is { IsAvailable: true })
                await _connectorPublisher.Device.PublishPatientDevicesAsync(
                    patientDevices, ConnectorSource, await DevicePublishOriginAsync(), cancellationToken);
        }

        // Profiles — SSV2-native source from pumps/settings (basal/bolus programs), replacing the v3
        // devices_and_settings call the windowed paths use. The current snapshot becomes one Nocturne
        // Profile. Unlike the v3 mapper there are no profile state spans here: pumps/settings exposes only
        // the current program set, not a historical active-profile timeline.
        if (activeTypes.Contains(SyncDataType.Profiles))
        {
            var settings = await FetchSsv2SafelyAsync(
                GlookoConstants.Ssv2PumpSettingsPath,
                () => FetchSsv2Async<GlookoSsv2PumpSettingsPage, GlookoSsv2PumpSettings>(
                    context, GlookoConstants.Ssv2PumpSettingsPath, p => p.Settings, incremental, batchStart),
                new List<GlookoSsv2PumpSettings>());

            var profile = context.SettingsProfileMapper.TransformSettingsToProfile(settings);
            if (profile != null
                && await PublishProfileDataAsync(new List<Profile> { profile }, config, cancellationToken))
            {
                result.ItemsSynced[SyncDataType.Profiles] = 1;
                _logger.LogInformation("[{ConnectorSource}] Published profile from SSV2 pump settings", ConnectorSource);
            }
        }
    }

    /// <summary>
    ///     Fetches one SSV2 batch resource and returns its records as an array, or an empty array if the
    ///     fetch fails (logged and skipped — see <see cref="FetchSsv2SafelyAsync{T}"/>).
    /// </summary>
    private Task<TRecord[]> FetchSsv2BatchResourceAsync<TPage, TRecord>(
        GlookoSyncContext context,
        string resource, Func<TPage, TRecord[]?> selectRecords, bool incremental, DateTime? startDate)
        where TPage : GlookoSsv2Page
        => FetchSsv2SafelyAsync(
            resource,
            async () => (await FetchSsv2Async<TPage, TRecord>(context, resource, selectRecords, incremental, startDate)).ToArray(),
            Array.Empty<TRecord>());

    /// <summary>
    ///     Runs an SSV2 fetch and returns its result, or — if it throws — logs a warning and returns
    ///     <paramref name="fallback"/> so one failing feed degrades to "no records this pass" instead of
    ///     aborting the whole sync. Mirrors the per-endpoint resilience of the windowed batch path.
    /// </summary>
    private async Task<T> FetchSsv2SafelyAsync<T>(string resource, Func<Task<T>> fetch, T fallback)
    {
        try
        {
            return await fetch();
        }
        catch (OperationCanceledException) { throw; }
        catch (GlookoDataForbiddenException)
        {
            // A stale patient code fails every resource, not just this one. Let it reach the pass's
            // caller so the sync re-authenticates and retries, rather than degrading the whole run to
            // "every feed returned nothing".
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "[{ConnectorSource}] SSV2 fetch for {Resource} failed; skipping it and continuing with the rest of the sync",
                ConnectorSource, resource);
            return fallback;
        }
    }

    private static string ConstructSsv2Url(string resource, DateTime? startDate, string lastUpdatedAt, string lastGuid)
    {
        // sendSoftDeleted=false by design: downstream deletion propagation isn't built, so we neither
        // page through tombstones nor act on them. Consequence (same as the windowed path): a record
        // deleted at the source *after* it was already ingested is never removed here. Flipping this to
        // true is only safe once tombstone ingest + downstream soft-delete exists — tracked as a
        // separate SSV2 follow-up.
        var url = $"{resource}?lastUpdatedAt={lastUpdatedAt}"
                + $"&lastGuid={lastGuid}"
                + $"&limit={GlookoConstants.Ssv2PageSize}"
                + "&sendSoftDeleted=false&allDevicesFlag=true";

        if (startDate.HasValue)
            url += $"&startDate={startDate.Value:yyyy-MM-ddTHH:mm:ss.fffZ}";

        return url;
    }


    /// <summary>
    ///     Fetches user profile from v3 API to get meter units and the account's home timezone.
    /// </summary>
    private async Task<GlookoV3UsersResponse?> FetchV3UserProfileAsync(GlookoSyncContext context)
    {
        try
        {
            EnsureAuthenticatedAndGetCode(context);

            var result = await FetchFromGlookoEndpoint(context, GlookoConstants.V3UsersPath);
            if (!result.HasValue) return null;

            var profile = JsonSerializer.Deserialize<GlookoV3UsersResponse>(result.Value.GetRawText());
            if (profile?.CurrentUser != null)
            {
                context.MeterUnits = profile.CurrentUser.MeterUnits;
                context.Timezone = profile.CurrentUser.Timezone;
                _logger.LogInformation("[{ConnectorSource}] User profile loaded. MeterUnits: {Units}, Timezone: {Timezone}",
                    ConnectorSource, context.MeterUnits, context.Timezone ?? "(none)");
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
    ///     Builds and installs the tenant's timezone timeline on the shared time mapper for this sync.
    ///     For V3 accounts it fetches the profile (capturing the home zone and meter units in one call)
    ///     and seeds the timeline origin from that zone on first sync. When no timezone service is wired
    ///     or the timeline is empty, conversion falls back to the legacy static offset.
    /// </summary>
    private async Task ConfigureTimezoneTimelineAsync(GlookoSyncContext context, CancellationToken cancellationToken)
    {
        if (_timezoneTimelineService is null)
            return;

        try
        {
            if (context.Config.UseV3Api && string.IsNullOrEmpty(context.MeterUnits))
                await FetchV3UserProfileAsync(context);

            if (!string.IsNullOrWhiteSpace(context.Timezone))
                await _timezoneTimelineService.EnsureOriginAsync(context.Timezone, cancellationToken);

            var resolver = await _timezoneTimelineService.GetResolverAsync(
                context.Config.TimezoneOffset, cancellationToken);
            context.TimeMapper.UseTimeline(resolver);

            _logger.LogInformation(
                "[{ConnectorSource}] Timezone timeline configured (entries present: {HasEntries}, home zone: {Zone})",
                ConnectorSource, resolver.HasEntries, context.Timezone ?? "(none)");
        }
        catch (Exception ex)
        {
            // Never fail a sync over timeline setup — fall back to the static offset.
            _logger.LogWarning(ex, "[{ConnectorSource}] Failed to configure timezone timeline; using static offset", ConnectorSource);
        }
    }

    /// <summary>
    ///     Fetches data from v3 graph/data API — single call for all data types.
    /// </summary>
    private async Task<GlookoV3GraphResponse?> FetchV3GraphDataAsync(
        GlookoSyncContext context, DateTime fromDate, DateTime toDate)
    {
        try
        {
            var patientCode = EnsureAuthenticatedAndGetCode(context);
            if (patientCode == null) return null;

            if (string.IsNullOrEmpty(context.MeterUnits)) await FetchV3UserProfileAsync(context);

            var url = ConstructV3GraphUrl(context, fromDate, toDate);
            _logger.LogInformation("[{ConnectorSource}] Fetching v3 graph data from {StartDate:yyyy-MM-dd} to {EndDate:yyyy-MM-dd}",
                ConnectorSource, fromDate, toDate);

            var result = await FetchFromGlookoEndpointWithRetry(context, url);
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
        catch (GlookoDataForbiddenException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching Glooko v3 graph data");
            return null;
        }
    }

    /// <summary>
    ///     Fetches pump device settings from the v3 devices_and_settings API.
    /// </summary>
    private async Task<GlookoV3DeviceSettingsResponse?> FetchV3DeviceSettingsAsync(GlookoSyncContext context)
    {
        try
        {
            var patientCode = EnsureAuthenticatedAndGetCode(context);
            if (patientCode == null) return null;

            var url = $"{GlookoConstants.V3DeviceSettingsPath}?patient={patientCode}";
            _logger.LogInformation("[{ConnectorSource}] Fetching device settings from v3 API", ConnectorSource);

            var result = await FetchFromGlookoEndpointWithRetry(context, url);
            if (!result.HasValue) return null;

            var settings = JsonSerializer.Deserialize<GlookoV3DeviceSettingsResponse>(result.Value.GetRawText());

            var pumpCount = settings?.DeviceSettings?.Pumps?.Count ?? 0;
            var snapshotCount = settings?.DeviceSettings?.Pumps?.Values.Sum(p => p.Count) ?? 0;

            _logger.LogInformation("[{ConnectorSource}] Fetched device settings: {PumpCount} pumps, {SnapshotCount} settings snapshots",
                ConnectorSource, pumpCount, snapshotCount);

            return settings;
        }
        catch (GlookoDataForbiddenException) { throw; }
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
    private async Task<GlookoV3HistoriesResponse?> FetchV3HistoriesAsync(
        GlookoSyncContext context, DateTime fromDate, DateTime toDate)
    {
        try
        {
            var patientCode = EnsureAuthenticatedAndGetCode(context);
            if (patientCode == null) return null;

            var url = $"{GlookoConstants.V3HistoriesPath}?patient={patientCode}"
                    + $"&startDate={fromDate:yyyy-MM-ddTHH:mm:ss.fffZ}"
                    + $"&endDate={toDate:yyyy-MM-ddTHH:mm:ss.fffZ}";

            _logger.LogInformation("[{ConnectorSource}] Fetching v3 histories from {StartDate:yyyy-MM-dd} to {EndDate:yyyy-MM-dd}",
                ConnectorSource, fromDate, toDate);

            var result = await FetchFromGlookoEndpointWithRetry(context, url);
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
        catch (GlookoDataForbiddenException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching Glooko v3 histories");
            return null;
        }
    }

}
