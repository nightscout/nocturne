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
    private readonly IDeviceClockService? _deviceClockService;
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
        IDeviceClockService? deviceClockService = null,
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
        _deviceClockService = deviceClockService;
        _cursorStore = cursorStore;
        _glookoLogger = logger;
    }

    public override string ServiceName => "Glooko";
    protected override string ConnectorSource => DataSources.GlookoConnector;

    private const string SyncSucceededMessage = "Sync completed successfully";

    private async Task<bool> AuthenticateWithConfigAsync(GlookoSyncContext context)
    {
        var token = await _tokenProvider.GetValidTokenAsync(context.Config, context.CancellationToken);
        if (token == null)
        {
            TrackFailedAuthentication(_tokenProvider.SignInFailureReason);
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

        var response = await _httpClient.SendAsync(request, context.CancellationToken);

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
                catch (Exception ex) when (ConnectorFaults.IsSourceFailure(ex, context.CancellationToken))
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
            context.CancellationToken,
            attempt => _logger.LogInformation("Applying retry backoff before retry {RetryNumber}", attempt + 2));
    }

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
        catch (Exception ex) when (ex is GlookoDataForbiddenException || ConnectorFaults.IsSourceFailure(ex, context.CancellationToken))
        {
            _logger.LogWarning(ex,
                "[{ConnectorSource}] Failed to fetch v3 pump-mode series; mode state spans skipped this pass",
                ConnectorSource);
            return null;
        }
    }

    protected override async Task<SyncResult> PerformSyncInternalAsync(
        SyncRequest request,
        GlookoConnectorConfiguration config,
        CancellationToken cancellationToken)
    {
        var result = new SyncResult
        {
            Success = true,
            Message = SyncSucceededMessage
        };

        try
        {
            // See GlookoSyncContext: this run's entire working state lives here, never on the service.
            var context = new GlookoSyncContext(config, ConnectorSource, _glookoLogger)
            {
                CancellationToken = cancellationToken
            };

            await ReportSyncMessageAsync(SyncMessageType.Authenticating, null, cancellationToken);

            if (!await AuthenticateWithConfigAsync(context))
                return AuthenticationFailedResult();

            var activeTypes = ResolveActiveTypes(request, config);

            // Resolve the tenant's timezone timeline before mapping any records. The account's home
            // zone (from the V3 profile) seeds the timeline's origin on first sync; thereafter the
            // user's travel/relocation entries drive per-record conversion. Falls back to the legacy
            // static offset when the timeline is empty (e.g. V2-only accounts, or profile tz unknown).
            await ConfigureTimezoneTimelineAsync(context, cancellationToken);

            // The request window is real-UTC; Glooko queries expect fake-UTC (local wall-clock). Pad by
            // a day each side so a non-zero offset between the two never clips edge data (dedup absorbs
            // the overlap). A range naming no lower bound reaches back the full history floor.
            var now = DateTime.UtcNow;
            var from = context.TimeMapper.ToGlookoTime(
                request.From ?? now.AddMonths(-GlookoConstants.FullWalkMonths)).AddDays(-1);
            var to = context.TimeMapper.ToGlookoTime(request.To ?? now).AddDays(1);

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

            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogError(ex, "Error during Glooko batch sync");
            result.Success = false;
            result.Message = "Sync failed with exception";
            result.Errors.Add(ex.Message);
            return result;
        }
    }

    /// <summary>
    ///     Entry point for the scheduled sync.
    /// </summary>
    /// <remarks>
    ///     The base derives the window from the tenant's newest glucose reading. Most Glooko accounts
    ///     never store one (CGM backfill is off by default), so that resolves to the six-month
    ///     <see cref="BaseConnectorService{TConfig}.InitialSyncFloor"/> on every run; and where one
    ///     exists it says nothing about what is still to come, because Glooko posts pump data in
    ///     batches days after the fact. The connector therefore reads a fixed
    ///     <see cref="GlookoConnectorConfiguration.LookbackDays"/> each run, and the floor once per
    ///     <see cref="GlookoConstants.FullWalkInterval"/>. The walk is recorded only once it has
    ///     succeeded, so a run that stopped at a failed chunk is walked again — after
    ///     <see cref="GlookoConstants.FullWalkRetryInterval"/>, not on every cycle, so a persistently
    ///     failing window cannot reinstate the per-cycle cost the schedule exists to remove.
    ///     <para>
    ///     SSV2 resumes every resource from its own cursor and ignores the bound on a scheduled run,
    ///     and a caller naming its own <paramref name="since"/> has already chosen; both take the base
    ///     window. A service without a cursor store has nowhere to remember a walk, so it resumes
    ///     instead, from the oldest of the glucose and treatment resume points: every family is
    ///     fetched inside the one window, and the base's glucose watermark alone would leave
    ///     treatments that a failed chunk never published below every later run's bound.
    ///     </para>
    /// </remarks>
    public override async Task<SyncResult> SyncDataAsync(
        GlookoConnectorConfiguration config,
        CancellationToken cancellationToken = default,
        DateTime? since = null,
        ISyncProgressReporter? progressReporter = null)
    {
        if (since.HasValue || config.UseSsv2Sync)
            return await base.SyncDataAsync(config, cancellationToken, since, progressReporter);

        if (_cursorStore is null)
        {
            var resumeFrom = ResumeFrom(
                await CalculateSinceTimestampAsync(config),
                await CalculateTreatmentSinceTimestampAsync(config));
            return await base.SyncDataAsync(config, cancellationToken, resumeFrom, progressReporter);
        }

        var now = DateTime.UtcNow;
        var fullWalk = await IsFullWalkDueAsync(now, cancellationToken);
        var from = fullWalk
            ? now.AddMonths(-GlookoConstants.FullWalkMonths)
            : now.AddDays(-config.LookbackDays);

        _logger.LogInformation(
            "[{ConnectorSource}] Scheduled sync reaches back to {From:yyyy-MM-dd} ({Mode})",
            ConnectorSource, from, fullWalk ? "full walk" : "incremental");

        if (fullWalk)
            await RecordFullWalkAsync(GlookoConstants.FullWalkAttemptCursorResource, now, cancellationToken);

        var result = await base.SyncDataAsync(config, cancellationToken, from, progressReporter);

        if (fullWalk && result.Success)
            await RecordFullWalkAsync(GlookoConstants.FullWalkCursorResource, now, cancellationToken);

        return result;
    }

    /// <summary>
    ///     Whether the scheduled run should walk the full history: the last completed walk is older
    ///     than the interval and the last attempt older than the retry interval. A store that cannot
    ///     be read walks, so a transient fault costs one extra walk rather than a missed one.
    /// </summary>
    private async Task<bool> IsFullWalkDueAsync(DateTime now, CancellationToken cancellationToken)
    {
        try
        {
            var completed = await _cursorStore!.GetAsync(
                ServiceName, GlookoConstants.FullWalkCursorResource, cancellationToken);
            if (!FullWalkSchedule.IsDue(completed?.LastUpdatedAt, GlookoConstants.FullWalkInterval, now))
                return false;

            var attempted = await _cursorStore.GetAsync(
                ServiceName, GlookoConstants.FullWalkAttemptCursorResource, cancellationToken);
            return FullWalkSchedule.IsDue(
                attempted?.LastUpdatedAt, GlookoConstants.FullWalkRetryInterval, now);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "[{ConnectorSource}] Could not read the full-walk schedule; walking", ConnectorSource);
            return true;
        }
    }

    /// <summary>
    ///     Stamps <paramref name="resource"/> with <paramref name="at"/>. A store that cannot be
    ///     written does not fail the sync; while it stays unwritable the schedule cannot remember a
    ///     walk and every scheduled run walks, which is what the warning is for.
    /// </summary>
    private async Task RecordFullWalkAsync(string resource, DateTime at, CancellationToken cancellationToken)
    {
        try
        {
            await _cursorStore!.SetAsync(
                ServiceName, resource,
                new ConnectorSyncCursor(FullWalkSchedule.Stamp(new DateTimeOffset(at, TimeSpan.Zero)), null),
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "[{ConnectorSource}] Could not record the full walk ({Resource})", ConnectorSource, resource);
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

                    await PublishRecordTypeAsync(result, SyncDataType.StateSpans, activeTypes,
                        context.ProfileMapper.TransformDeviceSettingsToProfileActivations(deviceSettings),
                        PublishProfileActivationDataAsync, context.Config, cancellationToken,
                        "device settings");
                }
            }
            catch (Exception profileEx) when (profileEx is not GlookoDataForbiddenException
                                              && (profileEx is not OperationCanceledException
                                                  || !cancellationToken.IsCancellationRequested))
            {
                _logger.LogWarning(profileEx, "[{ConnectorSource}] Failed to fetch/publish profile data", ConnectorSource);
                RecordFetchFailure(result, SyncDataType.Profiles, activeTypes);
            }
        }
    }

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

        await MapAndPublishV2BatchAsync(context, batchData, activeTypes, result, cancellationToken);
        return true;
    }

    /// <summary>
    ///     Maps and publishes a populated <see cref="GlookoBatchData"/> (glucose, manual BG, treatments,
    ///     foods, state spans, temp basals). Shared by the date-windowed V2 path and the SSV2 cursor
    ///     path, which differ only in how the batch is fetched.
    /// </summary>
    /// <returns>Whether every publish the batch made reached the tenant, or had nothing to publish.</returns>
    private async Task<bool> MapAndPublishV2BatchAsync(
        GlookoSyncContext context,
        GlookoBatchData batchData,
        HashSet<SyncDataType> activeTypes,
        SyncResult result,
        CancellationToken cancellationToken)
    {
        var config = context.Config;

        var landed = true;

        var sensorGlucose = context.SensorGlucoseMapper.TransformBatchDataToSensorGlucose(batchData).ToList();
        landed &= Landed(activeTypes, SyncDataType.Glucose, sensorGlucose,
            await PublishRecordTypeAsync(result, SyncDataType.Glucose, activeTypes,
                sensorGlucose, PublishSensorGlucoseDataAsync, config, cancellationToken));

        var bgChecks = context.SensorGlucoseMapper.TransformBatchDataToBGChecks(batchData).ToList();
        landed &= Landed(activeTypes, SyncDataType.ManualBG, bgChecks,
            await PublishRecordTypeAsync(result, SyncDataType.ManualBG, activeTypes,
                bgChecks, PublishBGCheckDataAsync, config, cancellationToken));

        var (boluses, carbs, _) = context.V4TreatmentMapper.MapBatchData(batchData);

        landed &= Landed(activeTypes, SyncDataType.Boluses, boluses,
            await PublishRecordTypeAsync(result, SyncDataType.Boluses, activeTypes,
                boluses, PublishBolusDataAsync, config, cancellationToken));

        landed &= Landed(activeTypes, SyncDataType.CarbIntake, carbs,
            await PublishRecordTypeAsync(result, SyncDataType.CarbIntake, activeTypes,
                carbs, PublishCarbIntakeDataAsync, config, cancellationToken));

        // Food attribution resolves against the carbs published above.
        var foodEntryImports = batchData.Foods is { Length: > 0 }
            ? context.V4TreatmentMapper.MapFoodsToConnectorEntries(batchData) : [];
        Func<string, string?> foodResolver = externalEntryId => $"glooko_food_{externalEntryId}";
        landed &= await PublishFoodEntriesAndAttributeAsync(
            foodEntryImports, carbs, foodResolver, result, activeTypes, cancellationToken);

        var stateSpans = context.StateSpanMapper.TransformV2ToStateSpans(batchData);
        landed &= Landed(activeTypes, SyncDataType.StateSpans, stateSpans,
            await PublishRecordTypeAsync(result, SyncDataType.StateSpans, activeTypes,
                stateSpans, PublishStateSpanDataAsync, config, cancellationToken));

        var tempBasals = context.TempBasalMapper.TransformV2ToTempBasals(batchData);
        landed &= Landed(activeTypes, SyncDataType.TempBasals, tempBasals,
            await PublishRecordTypeAsync(result, SyncDataType.TempBasals, activeTypes,
                tempBasals, PublishTempBasalDataAsync, config, cancellationToken));

        return landed;
    }

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
                        if (food is { SoftDeleted: not true, Guid.Length: > 0 })
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
        await PublishRecordTypeAsync(result, SyncDataType.StateSpans, activeTypes,
            context.StateSpanMapper.TransformV3ToProfileActivations(v3Data),
            PublishProfileActivationDataAsync, config, cancellationToken, "profile changes");

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

    /// <summary>
    ///     Publishes food catalog entries and attributes them to carb intakes via the meal matching service.
    /// </summary>
    /// <returns>
    ///     Whether the catalog import reached the tenant, or there was nothing to import. A failed
    ///     attribution does not count: the entries are already in the catalog.
    /// </returns>
    private async Task<bool> PublishFoodEntriesAndAttributeAsync(
        List<ConnectorFoodEntryImport> foodEntryImports,
        List<CarbIntake> carbIntakes,
        Func<string, string?>? foodEntryToCarbLegacyId,
        SyncResult result,
        HashSet<SyncDataType> activeTypes,
        CancellationToken cancellationToken)
    {
        if (!activeTypes.Contains(SyncDataType.Food))
            return true;

        if (foodEntryImports.Count == 0)
        {
            RecordPublishOutcome(result, SyncDataType.Food, 0, success: true);
            return true;
        }

        if (_connectorPublisher is not { IsAvailable: true })
        {
            _logger.LogWarning("Publisher not available for food entry submission");
            RecordPublishOutcome(result, SyncDataType.Food, foodEntryImports.Count, success: false);
            return false;
        }

        var importedEntries = await _connectorPublisher.Metadata.PublishConnectorFoodEntriesAsync(
            foodEntryImports, ConnectorSource, WriteOrigin.Live, cancellationToken); // Food is a dormant broadcast category — origin irrelevant until wired.

        // The publisher returns null only from its own catch; an import that reached the catalog
        // returns a list, empty when nothing was accepted.
        RecordPublishOutcome(result, SyncDataType.Food, foodEntryImports.Count, importedEntries is not null);

        if (importedEntries is null)
            return false;

        if (importedEntries.Count == 0
            || _mealMatchingService == null || carbIntakes.Count == 0 || foodEntryToCarbLegacyId == null)
            return true;

        var pendingEntries = importedEntries
            .Where(e => e.Status == ConnectorFoodEntryStatus.Pending)
            .ToList();

        if (pendingEntries.Count == 0) return true;

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
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(ex,
                    "[{ConnectorSource}] Failed to attribute food entry {FoodEntryId} to CarbIntake {CarbIntakeId}",
                    ConnectorSource, entry.Id, carbIntake.Id);
            }
        }

        _logger.LogInformation("[{ConnectorSource}] Attributed {Count}/{Total} food entries to carb intakes",
            ConnectorSource, attributedCount, pendingEntries.Count);
        return true;
    }

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
            catch (Exception ex) when (ConnectorFaults.IsSourceFailure(ex, context.CancellationToken))
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
        catch (Exception ex) when (ex is GlookoDataForbiddenException || ConnectorFaults.IsSourceFailure(ex, context.CancellationToken))
        {
            _logger.LogWarning(ex, "[{ConnectorSource}] Failed to fetch V2 foods for metadata enrichment", ConnectorSource);
            return null;
        }
    }

    /// <summary>
    ///     Fetches every record of an SSV2 resource via cursor pagination, returning the raw records for the
    ///     caller to map. In incremental mode the scan resumes from the stored per-resource cursor so only
    ///     server-side updates since the last sync are pulled; in explicit-range mode (the
    ///     reset/backfill path, signalled by a non-null sync window) the stored cursor is bypassed and the
    ///     scan runs from the beginning, leaving the incremental cursor untouched.
    /// </summary>
    /// <remarks>
    ///     The cursor the scan reached is left in <see cref="GlookoSyncContext.PendingSsv2Cursors"/>, not
    ///     stored: the caller stores it with <see cref="CommitSsv2CursorsAsync"/> once the records fetched
    ///     under it have been published, so a failed or cancelled publish refetches them next sync.
    /// </remarks>
    /// <param name="resource">SSV2 resource path (e.g. <c>/api/v2/cgm/egvs</c>).</param>
    /// <param name="selectRecords">Projects a deserialized page to its record array.</param>
    /// <param name="incremental">When true, resume from the stored cursor.</param>
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

        // Held for incremental scans only, and only when the cursor actually advanced from where this
        // run started (so a no-op pass never rewrites the stored watermark or the epoch default).
        if (incremental && _cursorStore != null
            && (lastUpdatedAt != initialUpdatedAt || lastGuid != initialGuid))
            context.PendingSsv2Cursors[resource] = new ConnectorSyncCursor(lastUpdatedAt, lastGuid);

        _logger.LogInformation("[{ConnectorSource}] SSV2 {Resource} fetched {Count} records (incremental={Incremental})",
            ConnectorSource, resource, all.Count, incremental);
        return all;
    }

    /// <summary>
    ///     Stores the cursors <see cref="FetchSsv2Async{TPage,TRecord}"/> reached for
    ///     <paramref name="resources"/>, once the publish their records fed has landed. A resource whose
    ///     publish did not land keeps its stored cursor, so the next sync fetches its records again.
    /// </summary>
    private async Task CommitSsv2CursorsAsync(
        GlookoSyncContext context, bool landed, params string[] resources)
    {
        if (!landed || _cursorStore is null) return;

        foreach (var resource in resources)
        {
            if (context.PendingSsv2Cursors.Remove(resource, out var cursor))
                await _cursorStore.SetAsync(ServiceName, resource, cursor);
        }
    }

    /// <summary>
    ///     Whether a publish left nothing behind to retry: it was accepted, or the type was off or the
    ///     batch empty, so there was nothing to publish.
    /// </summary>
    private static bool Landed<T>(
        HashSet<SyncDataType> activeTypes, SyncDataType dataType, ICollection<T> records, bool published) =>
        published || !activeTypes.Contains(dataType) || records.Count == 0;

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
    ///     page) its data types are reported as failed fetches in <paramref name="result"/> and the rest
    ///     of the pass still imports.
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
        context.PendingSsv2Cursors.Clear();

        // Incremental syncs resume purely from each resource's stored cursor; an explicit-range backfill
        // passes the clinical floor. (egvs ignores startDate server-side — its cursor is authoritative —
        // but it is kept consistent with the other resources rather than special-cased.)
        DateTime? batchStart = incremental ? null : from;

        if (activeTypes.Contains(SyncDataType.Glucose))
        {
            var egvGlucose = await FetchSsv2SafelyAsync(
                context,
                GlookoConstants.Ssv2EgvsPath,
                result,
                activeTypes,
                [SyncDataType.Glucose],
                () => FetchSsv2EgvsAsync(context, incremental, batchStart),
                new List<SensorGlucose>());
            await CommitSsv2CursorsAsync(context,
                Landed(activeTypes, SyncDataType.Glucose, egvGlucose,
                    await PublishRecordTypeAsync(result, SyncDataType.Glucose, activeTypes,
                        egvGlucose, PublishSensorGlucoseDataAsync, config, cancellationToken)),
                GlookoConstants.Ssv2EgvsPath);
        }

        var batchData = new GlookoBatchData
        {
            NormalBoluses = await FetchSsv2BatchResourceAsync<GlookoNormalBolusPage, GlookoBolus>(
                context,
                result,
                activeTypes,
                [SyncDataType.Boluses, SyncDataType.CarbIntake],
                GlookoConstants.NormalBolusesPath, p => p.NormalBoluses, incremental, batchStart),
            ScheduledBasals = await FetchSsv2BatchResourceAsync<GlookoScheduledBasalPage, GlookoBasal>(
                context,
                result,
                activeTypes,
                [SyncDataType.TempBasals],
                GlookoConstants.ScheduledBasalsPath, p => p.ScheduledBasals, incremental, batchStart),
            TempBasals = await FetchSsv2BatchResourceAsync<GlookoTemporaryBasalPage, GlookoTempBasal>(
                context,
                result,
                activeTypes,
                [SyncDataType.TempBasals],
                GlookoConstants.TemporaryBasalsPath, p => p.TemporaryBasals, incremental, batchStart),
            SuspendBasals = await FetchSsv2BatchResourceAsync<GlookoSuspendBasalPage, GlookoSuspendBasal>(
                context,
                result,
                activeTypes,
                [SyncDataType.StateSpans, SyncDataType.TempBasals],
                GlookoConstants.SuspendBasalsPath, p => p.SuspendBasals, incremental, batchStart),
            MeterReadings = await FetchSsv2BatchResourceAsync<GlookoMeterReadingPage, GlookoMeterReading>(
                context,
                result,
                activeTypes,
                [SyncDataType.ManualBG],
                GlookoConstants.MeterReadingsPath, p => p.Readings, incremental, batchStart),
            Foods = await FetchSsv2BatchResourceAsync<GlookoFoodPage, GlookoFood>(
                context,
                result,
                activeTypes,
                [SyncDataType.CarbIntake, SyncDataType.Food],
                GlookoConstants.FoodsPath, p => p.Foods, incremental, batchStart),
        };

        await CommitSsv2CursorsAsync(context,
            await MapAndPublishV2BatchAsync(context, batchData, activeTypes, result, cancellationToken),
            GlookoConstants.NormalBolusesPath, GlookoConstants.ScheduledBasalsPath,
            GlookoConstants.TemporaryBasalsPath, GlookoConstants.SuspendBasalsPath,
            GlookoConstants.MeterReadingsPath, GlookoConstants.FoodsPath);

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
                context,
                GlookoConstants.Ssv2InjectionBolusesPath,
                result,
                activeTypes,
                [SyncDataType.Boluses],
                () => FetchSsv2Async<GlookoInjectionBolusPage, GlookoInjectionInsulin>(
                    context, GlookoConstants.Ssv2InjectionBolusesPath, p => p.InjectionBoluses, incremental, batchStart),
                new List<GlookoInjectionInsulin>());
            var injectionBasals = await FetchSsv2SafelyAsync(
                context,
                GlookoConstants.Ssv2InjectionBasalsPath,
                result,
                activeTypes,
                [SyncDataType.BasalInjections],
                () => FetchSsv2Async<GlookoInjectionBasalPage, GlookoInjectionInsulin>(
                    context, GlookoConstants.Ssv2InjectionBasalsPath, p => p.InjectionBasals, incremental, batchStart),
                new List<GlookoInjectionInsulin>());

            var (penBasals, penBoluses) = context.V4TreatmentMapper.MapSsv2InjectionInsulin(injectionBasals, injectionBoluses);

            var penLanded = Landed(activeTypes, SyncDataType.Boluses, penBoluses,
                await PublishRecordTypeAsync(result, SyncDataType.Boluses, activeTypes,
                    penBoluses, PublishBolusDataAsync, config, cancellationToken));
            penLanded &= Landed(activeTypes, SyncDataType.BasalInjections, penBasals,
                await PublishRecordTypeAsync(result, SyncDataType.BasalInjections, activeTypes,
                    penBasals, PublishBasalInjectionDataAsync, config, cancellationToken));
            await CommitSsv2CursorsAsync(context, penLanded,
                GlookoConstants.Ssv2InjectionBolusesPath, GlookoConstants.Ssv2InjectionBasalsPath);
        }

        // App-logged insulin doses — cgm/insulin_events: doses logged in the app by CGM-only/MDI users,
        // not pump-delivered. "fast_acting" → rapid Bolus, "long_acting"/"intermediate" → BasalInjection.
        // Distinct from the pen-injection feeds above (which carry a product name); this feed has none, so
        // DIA/peak is resolved by category.
        if (activeTypes.Contains(SyncDataType.Boluses) || activeTypes.Contains(SyncDataType.BasalInjections))
        {
            var insulinEvents = await FetchSsv2SafelyAsync(
                context,
                GlookoConstants.Ssv2InsulinEventsPath,
                result,
                activeTypes,
                [SyncDataType.Boluses, SyncDataType.BasalInjections],
                () => FetchSsv2Async<GlookoInsulinEventPage, GlookoSsv2InsulinEvent>(
                    context, GlookoConstants.Ssv2InsulinEventsPath, p => p.InsulinEvents, incremental, batchStart),
                new List<GlookoSsv2InsulinEvent>());

            var (eventBasals, eventBoluses) = context.V4TreatmentMapper.MapSsv2InsulinEvents(insulinEvents);

            var eventLanded = Landed(activeTypes, SyncDataType.Boluses, eventBoluses,
                await PublishRecordTypeAsync(result, SyncDataType.Boluses, activeTypes,
                    eventBoluses, PublishBolusDataAsync, config, cancellationToken));
            eventLanded &= Landed(activeTypes, SyncDataType.BasalInjections, eventBasals,
                await PublishRecordTypeAsync(result, SyncDataType.BasalInjections, activeTypes,
                    eventBasals, PublishBasalInjectionDataAsync, config, cancellationToken));
            await CommitSsv2CursorsAsync(context, eventLanded, GlookoConstants.Ssv2InsulinEventsPath);
        }

        // Extended/dual-wave boluses — square (all-extended) or dual (immediate + extended) deliveries
        // with a duration. Net-new vs the windowed path and the v3 graph (no extended-bolus series).
        if (activeTypes.Contains(SyncDataType.Boluses))
        {
            var extended = await FetchSsv2SafelyAsync(
                context,
                GlookoConstants.Ssv2ExtendedBolusesPath,
                result,
                activeTypes,
                [SyncDataType.Boluses],
                () => FetchSsv2Async<GlookoExtendedBolusPage, GlookoExtendedBolus>(
                    context, GlookoConstants.Ssv2ExtendedBolusesPath, p => p.ExtendedBoluses, incremental, batchStart),
                new List<GlookoExtendedBolus>());
            var extendedBoluses = context.V4TreatmentMapper.MapSsv2ExtendedBoluses(extended);
            await CommitSsv2CursorsAsync(context,
                Landed(activeTypes, SyncDataType.Boluses, extendedBoluses,
                    await PublishRecordTypeAsync(result, SyncDataType.Boluses, activeTypes,
                        extendedBoluses, PublishBolusDataAsync, config, cancellationToken)),
                GlookoConstants.Ssv2ExtendedBolusesPath);
        }

        // Standalone carbs — app-logged carb entries not tied to a bolus (v3 carbAll equivalent),
        // additional to the carbs derived from bolus.carbsInput + foods in MapAndPublishV2BatchAsync
        // (PublishRecordTypeAsync accumulates the count).
        if (activeTypes.Contains(SyncDataType.CarbIntake))
        {
            var carbsEvents = await FetchSsv2SafelyAsync(
                context,
                GlookoConstants.Ssv2CarbsEventsPath,
                result,
                activeTypes,
                [SyncDataType.CarbIntake],
                () => FetchSsv2Async<GlookoCarbsEventPage, GlookoSsv2CarbsEvent>(
                    context, GlookoConstants.Ssv2CarbsEventsPath, p => p.CarbsEvents, incremental, batchStart),
                new List<GlookoSsv2CarbsEvent>());
            var standaloneCarbs = context.V4TreatmentMapper.MapSsv2CarbsEvents(carbsEvents);
            await CommitSsv2CursorsAsync(context,
                Landed(activeTypes, SyncDataType.CarbIntake, standaloneCarbs,
                    await PublishRecordTypeAsync(result, SyncDataType.CarbIntake, activeTypes,
                        standaloneCarbs, PublishCarbIntakeDataAsync, config, cancellationToken)),
                GlookoConstants.Ssv2CarbsEventsPath);
        }

        // Notes — app-logged free-text notes (camelCase /api/v2/notes) → Note.
        if (activeTypes.Contains(SyncDataType.Notes))
        {
            var rawNotes = await FetchSsv2SafelyAsync(
                context,
                GlookoConstants.Ssv2NotesPath,
                result,
                activeTypes,
                [SyncDataType.Notes],
                () => FetchSsv2Async<GlookoNotePage, GlookoSsv2Note>(
                    context, GlookoConstants.Ssv2NotesPath, p => p.Notes, incremental, batchStart),
                new List<GlookoSsv2Note>());
            var notes = context.NoteMapper.MapSsv2Notes(rawNotes);
            await CommitSsv2CursorsAsync(context,
                Landed(activeTypes, SyncDataType.Notes, notes,
                    await PublishRecordTypeAsync(result, SyncDataType.Notes, activeTypes,
                        notes, PublishNoteDataAsync, config, cancellationToken)),
                GlookoConstants.Ssv2NotesPath);
        }

        // Activities — two app-logged exercise sources mapped to Activity: exercises (seconds duration,
        // numeric intensity) and cgm/exercise_events (minutes duration, string intensity). Both normalize
        // to minutes. PublishRecordTypeAsync accumulates the count across the two sources.
        if (activeTypes.Contains(SyncDataType.Activity))
        {
            var rawExercises = await FetchSsv2SafelyAsync(
                context,
                GlookoConstants.Ssv2ExercisesPath,
                result,
                activeTypes,
                [SyncDataType.Activity],
                () => FetchSsv2Async<GlookoExercisePage, GlookoSsv2Exercise>(
                    context, GlookoConstants.Ssv2ExercisesPath, p => p.Exercises, incremental, batchStart),
                new List<GlookoSsv2Exercise>());
            var exerciseActivities = context.ActivityMapper.MapSsv2Exercises(rawExercises);
            await CommitSsv2CursorsAsync(context,
                Landed(activeTypes, SyncDataType.Activity, exerciseActivities,
                    await PublishRecordTypeAsync(result, SyncDataType.Activity, activeTypes,
                        exerciseActivities, PublishActivityDataAsync, config, cancellationToken)),
                GlookoConstants.Ssv2ExercisesPath);

            var rawExerciseEvents = await FetchSsv2SafelyAsync(
                context,
                GlookoConstants.Ssv2ExerciseEventsPath,
                result,
                activeTypes,
                [SyncDataType.Activity],
                () => FetchSsv2Async<GlookoExerciseEventPage, GlookoSsv2ExerciseEvent>(
                    context, GlookoConstants.Ssv2ExerciseEventsPath, p => p.ExerciseEvents, incremental, batchStart),
                new List<GlookoSsv2ExerciseEvent>());
            var exerciseEventActivities = context.ActivityMapper.MapSsv2ExerciseEvents(rawExerciseEvents);
            await CommitSsv2CursorsAsync(context,
                Landed(activeTypes, SyncDataType.Activity, exerciseEventActivities,
                    await PublishRecordTypeAsync(result, SyncDataType.Activity, activeTypes,
                        exerciseEventActivities, PublishActivityDataAsync, config, cancellationToken)),
                GlookoConstants.Ssv2ExerciseEventsPath);

            // Biometric/health series — body weight, daily step counts, and resting heart rate. These map to
            // the Core BodyWeight/StepCount/HeartRate models (not V4), upserted by deterministic Id via the
            // Metadata publisher. Gated under Activity (the closest existing biometric gate; there is no
            // SyncDataType for weight/steps/HR). Not routed through PublishRecordTypeAsync — its count is
            // keyed by SyncDataType, and these would otherwise inflate the Activity count.

            // Body weight — two sources: manual/HealthKit (grams) + third-party/Validic (kilograms).
            var weights = await FetchSsv2SafelyAsync(
                context,
                GlookoConstants.Ssv2WeightsPath,
                result,
                activeTypes,
                [SyncDataType.Activity],
                () => FetchSsv2Async<GlookoWeightPage, GlookoSsv2Weight>(
                    context, GlookoConstants.Ssv2WeightsPath, p => p.Weights, incremental, batchStart),
                new List<GlookoSsv2Weight>());
            var validicWeights = await FetchSsv2SafelyAsync(
                context,
                GlookoConstants.Ssv2ValidicWeightsPath,
                result,
                activeTypes,
                [SyncDataType.Activity],
                () => FetchSsv2Async<GlookoValidicWeightPage, GlookoSsv2ValidicWeight>(
                    context, GlookoConstants.Ssv2ValidicWeightsPath, p => p.Weights, incremental, batchStart),
                new List<GlookoSsv2ValidicWeight>());

            var bodyWeights = context.BodyWeightMapper.MapSsv2Weights(weights);
            bodyWeights.AddRange(context.BodyWeightMapper.MapSsv2ValidicWeights(validicWeights));
            await CommitSsv2CursorsAsync(context,
                bodyWeights.Count == 0 || await PublishBodyWeightDataAsync(bodyWeights, config, cancellationToken),
                GlookoConstants.Ssv2WeightsPath, GlookoConstants.Ssv2ValidicWeightsPath);

            // Daily step counts — validic/routines (per-day total).
            var routines = await FetchSsv2SafelyAsync(
                context,
                GlookoConstants.Ssv2RoutinesPath,
                result,
                activeTypes,
                [SyncDataType.Activity],
                () => FetchSsv2Async<GlookoRoutinePage, GlookoSsv2Routine>(
                    context, GlookoConstants.Ssv2RoutinesPath, p => p.Routines, incremental, batchStart),
                new List<GlookoSsv2Routine>());
            var stepCounts = context.StepCountMapper.MapSsv2Routines(routines);
            await CommitSsv2CursorsAsync(context,
                stepCounts.Count == 0 || await PublishStepCountDataAsync(stepCounts, config, cancellationToken),
                GlookoConstants.Ssv2RoutinesPath);

            // Resting heart rate — the only HR-bearing SSV2 source (validic/biometric_measurements);
            // most records carry other vitals and no HR, so the mapper skips those.
            var biometrics = await FetchSsv2SafelyAsync(
                context,
                GlookoConstants.Ssv2BiometricMeasurementsPath,
                result,
                activeTypes,
                [SyncDataType.Activity],
                () => FetchSsv2Async<GlookoBiometricMeasurementPage, GlookoSsv2BiometricMeasurement>(
                    context, GlookoConstants.Ssv2BiometricMeasurementsPath, p => p.BiometricMeasurements, incremental, batchStart),
                new List<GlookoSsv2BiometricMeasurement>());
            var heartRates = context.HeartRateMapper.MapSsv2BiometricMeasurements(biometrics);
            await CommitSsv2CursorsAsync(context,
                heartRates.Count == 0 || await PublishHeartRateDataAsync(heartRates, config, cancellationToken),
                GlookoConstants.Ssv2BiometricMeasurementsPath);
        }

        // Device events — granular pumps/events feed (reservoir/site/cannula changes) plus pump alarms
        // (→ system events). Net-new for SSV2 vs the windowed batch path; the v3 path derives both from
        // its graph series. Both are reported under the DeviceEvents count, matching the v3 path.
        if (activeTypes.Contains(SyncDataType.DeviceEvents))
        {
            var deviceEventCount = 0;

            var pumpEvents = await FetchSsv2SafelyAsync(
                context,
                GlookoConstants.Ssv2PumpEventsPath,
                result,
                activeTypes,
                [SyncDataType.DeviceEvents],
                () => FetchSsv2Async<GlookoPumpEventPage, GlookoPumpEvent>(
                    context, GlookoConstants.Ssv2PumpEventsPath, p => p.Events, incremental, batchStart),
                new List<GlookoPumpEvent>());
            var deviceEvents = context.PumpEventMapper.TransformPumpEventsToDeviceEvents(pumpEvents);
            var deviceEventsLanded = deviceEvents.Count == 0;
            if (deviceEvents.Count > 0 && await PublishDeviceEventDataAsync(deviceEvents, config, cancellationToken))
            {
                deviceEventCount += deviceEvents.Count;
                deviceEventsLanded = true;
            }
            await CommitSsv2CursorsAsync(context, deviceEventsLanded, GlookoConstants.Ssv2PumpEventsPath);

            var alarms = await FetchSsv2SafelyAsync(
                context,
                GlookoConstants.Ssv2AlarmsPath,
                result,
                activeTypes,
                [SyncDataType.DeviceEvents],
                () => FetchSsv2Async<GlookoSsv2AlarmPage, GlookoSsv2Alarm>(
                    context, GlookoConstants.Ssv2AlarmsPath, p => p.Alarms, incremental, batchStart),
                new List<GlookoSsv2Alarm>());
            var systemEvents = context.SystemEventMapper.TransformSsv2AlarmsToSystemEvents(alarms);
            var systemEventsLanded = systemEvents.Count == 0;
            if (systemEvents.Count > 0 && await PublishSystemEventDataAsync(systemEvents, config, cancellationToken))
            {
                deviceEventCount += systemEvents.Count;
                systemEventsLanded = true;
            }
            await CommitSsv2CursorsAsync(context, systemEventsLanded, GlookoConstants.Ssv2AlarmsPath);

            if (deviceEventCount > 0)
                result.ItemsSynced[SyncDataType.DeviceEvents] = deviceEventCount;

            // Patient hardware inventory — the pumps / cgm_devices feeds map to PatientDevice (the user's
            // pump + CGM). Gated under DeviceEvents as the closest existing device gate (there is no
            // SyncDataType for hardware inventory). Upserted via IDevicePublisher.PublishPatientDevicesAsync
            // keyed on the mapper's deterministic Id, so re-syncs update in place.
            var pumpDevices = await FetchSsv2SafelyAsync(
                context,
                GlookoConstants.Ssv2PumpsPath,
                result,
                activeTypes,
                [SyncDataType.DeviceEvents],
                () => FetchSsv2Async<GlookoPumpDevicePage, GlookoSsv2Device>(
                    context, GlookoConstants.Ssv2PumpsPath, p => p.Pumps, incremental, batchStart),
                new List<GlookoSsv2Device>());
            var cgmDevices = await FetchSsv2SafelyAsync(
                context,
                GlookoConstants.Ssv2CgmDevicesPath,
                result,
                activeTypes,
                [SyncDataType.DeviceEvents],
                () => FetchSsv2Async<GlookoCgmDevicePage, GlookoSsv2Device>(
                    context, GlookoConstants.Ssv2CgmDevicesPath, p => p.CgmDevices, incremental, batchStart),
                new List<GlookoSsv2Device>());

            var patientDevices = context.DeviceMapper.TransformPumpsToPatientDevices(pumpDevices);
            patientDevices.AddRange(context.DeviceMapper.TransformCgmDevicesToPatientDevices(cgmDevices));
            var devicesLanded = patientDevices.Count == 0;
            if (patientDevices.Count > 0 && _connectorPublisher is { IsAvailable: true })
                devicesLanded = await _connectorPublisher.Device.PublishPatientDevicesAsync(
                    patientDevices, ConnectorSource, await DevicePublishOriginAsync(), cancellationToken);
            await CommitSsv2CursorsAsync(context, devicesLanded,
                GlookoConstants.Ssv2PumpsPath, GlookoConstants.Ssv2CgmDevicesPath);
        }

        // Profiles — SSV2-native source from pumps/settings (basal/bolus programs), replacing the v3
        // devices_and_settings call the windowed paths use. The current snapshot becomes one Nocturne
        // Profile. Unlike the v3 mapper there are no profile state spans here: pumps/settings exposes only
        // the current program set, not a historical active-profile timeline.
        if (activeTypes.Contains(SyncDataType.Profiles))
        {
            var settings = await FetchSsv2SafelyAsync(
                context,
                GlookoConstants.Ssv2PumpSettingsPath,
                result,
                activeTypes,
                [SyncDataType.Profiles],
                () => FetchSsv2Async<GlookoSsv2PumpSettingsPage, GlookoSsv2PumpSettings>(
                    context, GlookoConstants.Ssv2PumpSettingsPath, p => p.Settings, incremental, batchStart),
                new List<GlookoSsv2PumpSettings>());

            var profile = context.SettingsProfileMapper.TransformSettingsToProfile(settings);
            var profileLanded = profile is null;
            if (profile != null
                && await PublishProfileDataAsync(new List<Profile> { profile }, config, cancellationToken))
            {
                result.ItemsSynced[SyncDataType.Profiles] = 1;
                _logger.LogInformation("[{ConnectorSource}] Published profile from SSV2 pump settings", ConnectorSource);
                profileLanded = true;
            }
            await CommitSsv2CursorsAsync(context, profileLanded, GlookoConstants.Ssv2PumpSettingsPath);
        }
    }

    /// <summary>
    ///     Fetches one SSV2 batch resource and returns its records as an array, or an empty array if the
    ///     fetch fails (reported and skipped, see <see cref="FetchSsv2SafelyAsync{T}"/>).
    /// </summary>
    private Task<TRecord[]> FetchSsv2BatchResourceAsync<TPage, TRecord>(
        GlookoSyncContext context,
        SyncResult result,
        HashSet<SyncDataType> activeTypes,
        SyncDataType[] dataTypes,
        string resource, Func<TPage, TRecord[]?> selectRecords, bool incremental, DateTime? startDate)
        where TPage : GlookoSsv2Page
        => FetchSsv2SafelyAsync(
            context,
            resource,
            result,
            activeTypes,
            dataTypes,
            async () => (await FetchSsv2Async<TPage, TRecord>(context, resource, selectRecords, incremental, startDate)).ToArray(),
            Array.Empty<TRecord>());

    /// <summary>
    ///     Runs an SSV2 fetch and returns its result, or, if it throws, records a fetch failure against
    ///     each of <paramref name="dataTypes"/> as the windowed batch path does and returns
    ///     <paramref name="fallback"/>, so one failing feed costs its types and the pass still runs.
    /// </summary>
    private async Task<T> FetchSsv2SafelyAsync<T>(
        GlookoSyncContext context,
        string resource,
        SyncResult result,
        HashSet<SyncDataType> activeTypes,
        SyncDataType[] dataTypes,
        Func<Task<T>> fetch,
        T fallback)
    {
        try
        {
            return await fetch();
        }
        // GlookoDataForbiddenException is left to propagate: a stale patient code fails every
        // resource, not just this one, so the pass's caller re-authenticates and retries rather than
        // degrading the whole run to "every feed returned nothing".
        catch (Exception ex) when (ConnectorFaults.IsSourceFailure(ex, context.CancellationToken))
        {
            _logger.LogWarning(ex,
                "[{ConnectorSource}] SSV2 fetch for {Resource} failed; continuing with the rest of the sync",
                ConnectorSource, resource);

            context.PendingSsv2Cursors.Remove(resource);
            foreach (var type in dataTypes)
                RecordFetchFailure(result, type, activeTypes);
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
        catch (Exception ex) when (ex is GlookoDataForbiddenException || ConnectorFaults.IsSourceFailure(ex, context.CancellationToken))
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

            // Gather device-clock evidence before building the resolver so a confirmed deviation
            // corrects the records this very sync is about to map.
            var segments = await ObserveDeviceClockAsync(context, cancellationToken);

            var entries = await _timezoneTimelineService.GetTimelineAsync(cancellationToken);
            var resolver = new TimezoneTimeline(entries, context.Config.TimezoneOffset, segments);
            context.TimeMapper.UseTimeline(resolver);

            _logger.LogInformation(
                "[{ConnectorSource}] Timezone timeline configured (entries present: {HasEntries}, home zone: {Zone}, "
                + "derived clock segments: {SegmentCount})",
                ConnectorSource, resolver.HasEntries, context.Timezone ?? "(none)", segments.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Never fail a sync over timeline setup — fall back to the static offset.
            _logger.LogWarning(ex, "[{ConnectorSource}] Failed to configure timezone timeline; using static offset", ConnectorSource);
        }
    }

    /// <summary>
    ///     Connector id device-clock evidence is scoped under — a derived correction must never leak
    ///     into another connector's conversion. Matches the sync-service id used elsewhere.
    /// </summary>
    private const string ClockConnectorId = "glooko";

    /// <summary>How far back the per-sync clock probe reads recent uploads.</summary>
    private static readonly TimeSpan ClockProbeLookback = TimeSpan.FromHours(48);

    /// <summary>
    ///     Gathers device-clock evidence for this sync: the account's profile offset (the phone app
    ///     writes the device's real offset to the vendor profile when it drifts) plus upload-batch
    ///     derivations over recent CGM and bolus records, whose server-side <c>syncTimestamp</c> is
    ///     real UTC while their clinical timestamps carry the device wall clock. Evidence is always
    ///     recorded; the derived segments are returned for the resolver only when the tenant has
    ///     enabled automatic correction. Never fails the sync.
    /// </summary>
    private async Task<IReadOnlyList<DeviceClockSegment>> ObserveDeviceClockAsync(
        GlookoSyncContext context, CancellationToken cancellationToken)
    {
        if (_deviceClockService is null)
            return [];

        try
        {
            var observations = new List<DeviceClockObservation>();

            // The user record itself rarely changes, so read it from the epoch rather than a window.
            var users = await FetchClockPageAsync<GlookoSsv2UsersPage>(
                context, GlookoConstants.V2UsersPath, DateTime.UnixEpoch);

            // SSV2 answers for the session account, not for a ?patient= code. On a linked account
            // (caregiver session, re-linked source) that is a different person, and their phone's
            // clock must never become evidence about the patient's data.
            var sessionUser = users?.Users?.FirstOrDefault(u => !u.SoftDeleted);
            if (sessionUser?.GlookoCode is { Length: > 0 } sessionCode
                && context.PatientCode is { Length: > 0 } patientCode
                && !string.Equals(sessionCode, patientCode, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation(
                    "[{ConnectorSource}] Session account is not the patient; skipping device clock evidence",
                    ConnectorSource);
                return [];
            }

            if (GlookoDeviceClockMapper.MapProfileObservation(users?.Users, ClockConnectorId) is { } profile)
                observations.Add(profile);

            var since = DateTime.UtcNow - ClockProbeLookback;
            var egvs = await FetchClockPageAsync<GlookoEgvPage>(context, GlookoConstants.Ssv2EgvsPath, since);
            var boluses = await FetchClockPageAsync<GlookoNormalBolusPage>(
                context, GlookoConstants.NormalBolusesPath, since);
            observations.AddRange(
                GlookoDeviceClockMapper.MapUploadBatches(ClockConnectorId, egvs?.Egvs, boluses?.NormalBoluses));

            // A tenant with no stored evidence gets a one-time historical scan, so deviations that
            // predate this feature (the whole point of re-correction) are discoverable at all. Bolus
            // records are sparse enough to page through the full retention window; CGM history is not,
            // so old windows carry bound-only evidence.
            if ((await _deviceClockService.GetObservationsAsync(ClockConnectorId, cancellationToken)).Count == 0)
                observations.AddRange(await ScanHistoricalBolusClocksAsync(context, cancellationToken));

            var segments = await _deviceClockService.RecordObservationsAsync(
                ClockConnectorId,
                observations,
                context.Config.TimezoneOffset,
                context.Config.AutoClockCorrection,
                cancellationToken);

            return context.Config.AutoClockCorrection ? segments : [];
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex,
                "[{ConnectorSource}] Device clock observation failed; syncing without it", ConnectorSource);
            return [];
        }
    }

    /// <summary>
    ///     Fetches a single SSV2 cursor page for the clock probe. The cursor's <c>lastUpdatedAt</c> is
    ///     real UTC (server-side), so a wall-clock offset can never clip the window.
    /// </summary>
    private Task<T?> FetchClockPageAsync<T>(GlookoSyncContext context, string path, DateTime sinceUtc)
        where T : GlookoSsv2Page =>
        FetchClockPageAsync<T>(context, path, FormatSsv2Cursor(sinceUtc), GlookoConstants.Ssv2InitialLastGuid);

    private async Task<T?> FetchClockPageAsync<T>(
        GlookoSyncContext context, string path, string cursor, string guid)
        where T : GlookoSsv2Page
    {
        var result = await FetchFromGlookoEndpoint(context, ConstructSsv2Url(path, null, cursor, guid));
        if (!result.HasValue)
            return null;

        return JsonSerializer.Deserialize<T>(result.Value.GetRawText());
    }

    private static string FormatSsv2Cursor(DateTime utc) => utc.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");

    /// <summary>Page budget for the one-time historical scan: 12 × 500 boluses ≈ years of pump use.</summary>
    private const int HistoricalScanMaxPages = 12;

    private async Task<IReadOnlyList<DeviceClockObservation>> ScanHistoricalBolusClocksAsync(
        GlookoSyncContext context, CancellationToken cancellationToken)
    {
        var records = new List<GlookoBolus>();
        var cursor = FormatSsv2Cursor(DateTime.UtcNow - TimeSpan.FromDays(IDeviceClockService.RetentionDays));
        var guid = GlookoConstants.Ssv2InitialLastGuid;

        for (var page = 0; page < HistoricalScanMaxPages; page++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var result = await FetchClockPageAsync<GlookoNormalBolusPage>(
                context, GlookoConstants.NormalBolusesPath, cursor, guid);
            if (result?.NormalBoluses is not { Length: > 0 } boluses)
                break;

            records.AddRange(boluses);

            var stalled = result.LastUpdatedAt == cursor && result.LastGuid == guid;
            if (result.LastPage || string.IsNullOrEmpty(result.LastUpdatedAt) || stalled)
                break;

            cursor = result.LastUpdatedAt;
            guid = result.LastGuid ?? GlookoConstants.Ssv2InitialLastGuid;
        }

        _logger.LogInformation(
            "[{ConnectorSource}] Historical clock scan read {Count} bolus records", ConnectorSource, records.Count);

        // Mapping once over the full set keeps upload batches whole across page boundaries.
        return GlookoDeviceClockMapper.MapUploadBatches(ClockConnectorId, null, records.ToArray());
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
                    + "PumpAlarm={Alarms}, ReservoirChange={Reservoir}, SetSiteChange={SetSite}, "
                    + "CgmSensorChange={SensorChange}, ProfileChange={Profile}",
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
                    s.CgmSensorChange?.Length ?? 0,
                    s.ProfileChange?.Length ?? 0);
            }

            return graphData;
        }
        catch (Exception ex) when (ConnectorFaults.IsSourceFailure(ex, context.CancellationToken))
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
            var snapshotCount = settings?.DeviceSettings?.Pumps?.Values.Sum(p => p?.Count ?? 0) ?? 0;

            _logger.LogInformation("[{ConnectorSource}] Fetched device settings: {PumpCount} pumps, {SnapshotCount} settings snapshots",
                ConnectorSource, pumpCount, snapshotCount);

            return settings;
        }
        catch (Exception ex) when (ConnectorFaults.IsSourceFailure(ex, context.CancellationToken))
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
        catch (Exception ex) when (ConnectorFaults.IsSourceFailure(ex, context.CancellationToken))
        {
            _logger.LogError(ex, "Error fetching Glooko v3 histories");
            return null;
        }
    }

}
