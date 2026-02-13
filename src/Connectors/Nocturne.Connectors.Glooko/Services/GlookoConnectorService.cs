using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.Glooko.Configurations;
using Nocturne.Connectors.Glooko.Mappers;
using Nocturne.Connectors.Glooko.Models;
using Nocturne.Core.Constants;
using Nocturne.Core.Models;

namespace Nocturne.Connectors.Glooko.Services;

/// <summary>
///     Connector service for Glooko data source
///     Based on the original nightscout-connect Glooko implementation
/// </summary>
public class GlookoConnectorService : BaseConnectorService<GlookoConnectorConfiguration>
{
    private readonly ITreatmentClassificationService _classificationService;
    private readonly GlookoConnectorConfiguration _config;
    private readonly GlookoEntryMapper _entryMapper;
    private readonly IRateLimitingStrategy _rateLimitingStrategy;
    private readonly IRetryDelayStrategy _retryDelayStrategy;
    private readonly GlookoStateSpanMapper _stateSpanMapper;
    private readonly GlookoSystemEventMapper _systemEventMapper;
    private readonly GlookoTimeMapper _timeMapper;
    private readonly GlookoAuthTokenProvider _tokenProvider;
    private readonly GlookoTreatmentMapper _treatmentMapper;

    public GlookoConnectorService(
        HttpClient httpClient,
        IOptions<GlookoConnectorConfiguration> config,
        ILogger<GlookoConnectorService> logger,
        IRetryDelayStrategy retryDelayStrategy,
        IRateLimitingStrategy rateLimitingStrategy,
        GlookoAuthTokenProvider tokenProvider,
        ITreatmentClassificationService classificationService,
        IConnectorPublisher? publisher = null
    )
        : base(httpClient, logger, publisher)
    {
        _config = config?.Value ?? throw new ArgumentNullException(nameof(config));
        _retryDelayStrategy =
            retryDelayStrategy ?? throw new ArgumentNullException(nameof(retryDelayStrategy));
        _rateLimitingStrategy =
            rateLimitingStrategy
            ?? throw new ArgumentNullException(nameof(rateLimitingStrategy));
        _tokenProvider =
            tokenProvider ?? throw new ArgumentNullException(nameof(tokenProvider));
        _classificationService =
            classificationService ?? throw new ArgumentNullException(nameof(classificationService));
        _entryMapper = new GlookoEntryMapper(_config, ConnectorSource, logger);
        _timeMapper = new GlookoTimeMapper(_config, logger);
        _treatmentMapper = new GlookoTreatmentMapper(
            ConnectorSource,
            _classificationService,
            _timeMapper,
            logger
        );
        _stateSpanMapper = new GlookoStateSpanMapper(ConnectorSource, _timeMapper, logger);
        _systemEventMapper = new GlookoSystemEventMapper(ConnectorSource, _timeMapper, logger);
    }

    public override string ServiceName => "Glooko";
    protected override string ConnectorSource => DataSources.GlookoConnector;

    public override List<SyncDataType> SupportedDataTypes =>
    [
        SyncDataType.Glucose,
        SyncDataType.Treatments
    ];

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

    protected override async Task<SyncResult> PerformSyncInternalAsync(
        SyncRequest request,
        GlookoConnectorConfiguration config,
        CancellationToken cancellationToken
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
            if (IsSessionExpired())
                if (!await AuthenticateAsync())
                {
                    result.Success = false;
                    result.Message = "Authentication failed";
                    result.Errors.Add("Authentication failed");
                    return result;
                }

            // Glooko fetches everything in one go, so determine the earliest 'From' date needed
            var from = request.From;

            var batchData = await FetchBatchDataAsync(from);

            if (batchData == null)
            {
                result.Success = false;
                result.Message = "Failed to fetch data";
                result.Errors.Add("No data returned from Glooko");
                return result;
            }

            // 1. Process Glucose
            if (request.DataTypes.Contains(SyncDataType.Glucose))
            {
                var entries = _entryMapper.TransformBatchDataToEntries(batchData).ToList();
                if (entries.Any())
                {
                    var success = await PublishGlucoseDataInBatchesAsync(
                        entries,
                        config,
                        cancellationToken
                    );
                    if (success)
                    {
                        result.ItemsSynced[SyncDataType.Glucose] = entries.Count;
                        result.LastEntryTimes[SyncDataType.Glucose] = entries.Max(e => e.Date);
                    }
                }
            }

            // 2. Process Treatments
            if (request.DataTypes.Contains(SyncDataType.Treatments))
            {
                var treatments = _treatmentMapper.TransformBatchDataToTreatments(batchData);

                // V3 API Integration: Fetch additional data types not available in v2
                if (_config.UseV3Api)
                    try
                    {
                        _logger.LogInformation(
                            "[{ConnectorSource}] Fetching additional data from v3 API...",
                            ConnectorSource
                        );
                        var v3Data = await FetchV3GraphDataAsync(from);
                        if (v3Data != null)
                        {
                            var v3Treatments = _treatmentMapper.TransformV3ToTreatments(v3Data);
                            // Add v3 treatments that don't already exist (based on Id)
                            var existingIds = treatments.Select(t => t.Id).ToHashSet();
                            var newV3Treatments = v3Treatments
                                .Where(t => !existingIds.Contains(t.Id))
                                .ToList();
                            treatments.AddRange(newV3Treatments);
                            _logger.LogInformation(
                                "[{ConnectorSource}] Added {Count} unique treatments from v3 API",
                                ConnectorSource,
                                newV3Treatments.Count
                            );

                            // Optionally add CGM backfill entries
                            if (
                                _config.V3IncludeCgmBackfill
                                && request.DataTypes.Contains(SyncDataType.Glucose)
                            )
                            {
                                var v3Entries = _entryMapper
                                    .TransformV3ToEntries(v3Data, _meterUnits)
                                    .ToList();
                                if (v3Entries.Any())
                                {
                                    await PublishGlucoseDataInBatchesAsync(
                                        v3Entries,
                                        config,
                                        cancellationToken
                                    );
                                    _logger.LogInformation(
                                        "[{ConnectorSource}] Published {Count} CGM backfill entries from v3",
                                        ConnectorSource,
                                        v3Entries.Count
                                    );
                                }
                            }

                            // Transform and publish StateSpans (pump modes, connectivity, profiles)
                            var stateSpans = _stateSpanMapper.TransformV3ToStateSpans(v3Data);
                            if (stateSpans.Any())
                            {
                                var stateSpanSuccess = await PublishStateSpanDataAsync(
                                    stateSpans,
                                    config,
                                    cancellationToken
                                );
                                if (stateSpanSuccess)
                                    _logger.LogInformation(
                                        "[{ConnectorSource}] Published {Count} state spans from v3",
                                        ConnectorSource,
                                        stateSpans.Count
                                    );
                            }

                            // Transform and publish SystemEvents (alarms, warnings)
                            var systemEvents = _systemEventMapper.TransformV3ToSystemEvents(v3Data);
                            if (systemEvents.Any())
                            {
                                var eventSuccess = await PublishSystemEventDataAsync(
                                    systemEvents,
                                    config,
                                    cancellationToken
                                );
                                if (eventSuccess)
                                    _logger.LogInformation(
                                        "[{ConnectorSource}] Published {Count} system events from v3",
                                        ConnectorSource,
                                        systemEvents.Count
                                    );
                            }
                        }
                    }
                    catch (Exception v3Ex)
                    {
                        _logger.LogWarning(
                            v3Ex,
                            "[{ConnectorSource}] V3 API fetch failed, continuing with v2 data only",
                            ConnectorSource
                        );
                    }

                // Transform and publish V2 StateSpans (temp basals, suspend basals)
                // These come from the V2 API which is always fetched
                var v2StateSpans = _stateSpanMapper.TransformV2ToStateSpans(batchData);
                if (v2StateSpans.Any())
                {
                    var v2StateSpanSuccess = await PublishStateSpanDataAsync(
                        v2StateSpans,
                        config,
                        cancellationToken
                    );
                    if (v2StateSpanSuccess)
                        _logger.LogInformation(
                            "[{ConnectorSource}] Published {Count} state spans from v2",
                            ConnectorSource,
                            v2StateSpans.Count
                        );
                }

                if (treatments.Any())
                {
                    var coreTreatments = treatments.ToList();
                    var success = await PublishTreatmentDataInBatchesAsync(
                        coreTreatments,
                        config,
                        cancellationToken
                    );

                    if (success)
                    {
                        result.ItemsSynced[SyncDataType.Treatments] = coreTreatments.Count;

                        // Parse timestamp safely
                        var maxDateStr = treatments.Max(t => t.CreatedAt);
                        if (DateTime.TryParse(maxDateStr, out var maxDate))
                            result.LastEntryTimes[SyncDataType.Treatments] = maxDate;
                    }
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

    public override async Task<IEnumerable<Entry>> FetchGlucoseDataAsync(DateTime? since = null)
    {
        var batchData = await FetchBatchDataAsync(since);
        var entriesList = batchData == null
            ? []
            : _entryMapper.TransformBatchDataToEntries(batchData).ToList();
        _logger.LogInformation(
            "[{ConnectorSource}] Retrieved {Count} glucose entries from Glooko (since: {Since})",
            ConnectorSource,
            entriesList.Count,
            since?.ToString("yyyy-MM-dd HH:mm:ss") ?? "default lookback"
        );

        return entriesList;
    }

    /// <summary>
    ///     Fetch comprehensive batch data from all Glooko endpoints
    ///     This matches the legacy implementation's dataFromSession method
    /// </summary>
    /// <summary>
    ///     Fetch comprehensive batch data from all Glooko endpoints
    ///     This matches the legacy implementation's dataFromSession method
    /// </summary>
    public async Task<GlookoBatchData?> FetchBatchDataAsync(DateTime? since = null)
    {
        try
        {
            if (string.IsNullOrEmpty(_tokenProvider.SessionCookie))
                throw new InvalidOperationException(
                    "Not authenticated with Glooko. Call AuthenticateAsync first."
                );
            if (_tokenProvider.UserData?.UserLogin?.GlookoCode == null)
            {
                _logger.LogWarning("Missing Glooko user code, cannot fetch data");
                return null;
            }

            // Calculate date range - fetch from specified date or last 24 hours
            var fromDate = since ?? DateTime.UtcNow.AddDays(-1);
            var toDate = DateTime.UtcNow;

            _logger.LogInformation(
                $"Fetching comprehensive Glooko data from {fromDate:yyyy-MM-dd} to {toDate:yyyy-MM-dd}"
            );

            var batchData = new GlookoBatchData();

            // Define endpoints and their handlers
            var endpointDefinitions = new[]
            {
                new
                {
                    Endpoint = "/api/v2/foods",
                    Handler = new Action<JsonElement>(json =>
                    {
                        if (json.TryGetProperty("foods", out var element))
                            batchData.Foods =
                                JsonSerializer.Deserialize<GlookoFood[]>(element.GetRawText())
                                ?? Array.Empty<GlookoFood>();
                    })
                },
                new
                {
                    Endpoint = "/api/v2/pumps/scheduled_basals",
                    Handler = new Action<JsonElement>(json =>
                    {
                        if (json.TryGetProperty("scheduledBasals", out var element))
                            batchData.ScheduledBasals =
                                JsonSerializer.Deserialize<GlookoBasal[]>(element.GetRawText())
                                ?? Array.Empty<GlookoBasal>();
                    })
                },
                new
                {
                    Endpoint = "/api/v2/pumps/normal_boluses",
                    Handler = new Action<JsonElement>(json =>
                    {
                        if (json.TryGetProperty("normalBoluses", out var element))
                            batchData.NormalBoluses =
                                JsonSerializer.Deserialize<GlookoBolus[]>(element.GetRawText())
                                ?? Array.Empty<GlookoBolus>();
                    })
                },
                new
                {
                    Endpoint = "/api/v2/cgm/readings",
                    Handler = new Action<JsonElement>(json =>
                    {
                        if (json.TryGetProperty("readings", out var element))
                            batchData.Readings =
                                JsonSerializer.Deserialize<GlookoCgmReading[]>(
                                    element.GetRawText()
                                ) ?? Array.Empty<GlookoCgmReading>();
                    })
                },
                new
                {
                    Endpoint = "/api/v2/pumps/suspend_basals",
                    Handler = new Action<JsonElement>(json =>
                    {
                        if (json.TryGetProperty("suspendBasals", out var element))
                            batchData.SuspendBasals =
                                JsonSerializer.Deserialize<GlookoSuspendBasal[]>(
                                    element.GetRawText()
                                ) ?? Array.Empty<GlookoSuspendBasal>();
                    })
                },
                new
                {
                    Endpoint = "/api/v2/pumps/temporary_basals",
                    Handler = new Action<JsonElement>(json =>
                    {
                        if (json.TryGetProperty("temporaryBasals", out var element))
                            batchData.TempBasals =
                                JsonSerializer.Deserialize<GlookoTempBasal[]>(
                                    element.GetRawText()
                                ) ?? Array.Empty<GlookoTempBasal>();
                    })
                }
            };

            // Fetch endpoints sequentially with rate limiting
            for (var i = 0; i < endpointDefinitions.Length; i++)
            {
                var def = endpointDefinitions[i];
                var url = ConstructGlookoUrl(def.Endpoint, fromDate, toDate);

                // Apply rate limiting strategy
                await _rateLimitingStrategy.ApplyDelayAsync(i);

                try
                {
                    var result = await FetchFromGlookoEndpointWithRetry(url);
                    if (result.HasValue)
                        try
                        {
                            def.Handler(result.Value);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(
                                ex,
                                "Error parsing data from {Endpoint}",
                                def.Endpoint
                            );
                        }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Failed to fetch from {Url}. Continuing with other endpoints.",
                        url
                    );
                }
            }

            // Log a summary of fetched data with source identifier
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
                batchData.SuspendBasals?.Length ?? 0
            );

            return batchData;
        }
        catch (InvalidOperationException)
        {
            // Re-throw authentication-related exceptions
            throw;
        }
        catch (HttpRequestException)
        {
            // Re-throw HTTP-related exceptions (including rate limiting)
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error fetching Glooko batch data: {ex.Message}");
            return null;
        }
    }

    private string ConstructGlookoUrl(string endpoint, DateTime startDate, DateTime endDate)
    {
        var patientCode = _tokenProvider.UserData?.UserLogin?.GlookoCode;

        // Add the required parameters matching the legacy implementation
        var lastGuid = "1e0c094e-1e54-4a4f-8e6a-f94484b53789"; // hardcoded as per legacy
        var maxCount = Math.Max(1, (int)Math.Ceiling((endDate - startDate).TotalMinutes / 5)); // 5-minute intervals

        return
            $"{endpoint}?patient={patientCode}&startDate={startDate:yyyy-MM-ddTHH:mm:ss.fffZ}&endDate={endDate:yyyy-MM-ddTHH:mm:ss.fffZ}&lastGuid={lastGuid}&lastUpdatedAt={startDate:yyyy-MM-ddTHH:mm:ss.fffZ}&limit={maxCount}";
    }

    private async Task<JsonElement?> FetchFromGlookoEndpoint(string url)
    {
        try
        {
            _logger.LogDebug($"GLOOKO FETCHER LOADING {url}");

            var request = new HttpRequestMessage(HttpMethod.Get, url);

            // Add required headers (matching legacy implementation)
            request.Headers.TryAddWithoutValidation(
                "Accept",
                "application/json, text/plain, */*"
            );
            request.Headers.TryAddWithoutValidation("Accept-Encoding", "gzip, deflate, br");
            request.Headers.TryAddWithoutValidation(
                "User-Agent",
                "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/16.5 Safari/605.1.15"
            );
            request.Headers.TryAddWithoutValidation("Referer", "https://eu.my.glooko.com/");
            request.Headers.TryAddWithoutValidation("Origin", "https://eu.my.glooko.com");
            request.Headers.TryAddWithoutValidation("Connection", "keep-alive");
            request.Headers.TryAddWithoutValidation("Accept-Language", "en-GB,en;q=0.9");
            request.Headers.TryAddWithoutValidation("Cookie", _tokenProvider.SessionCookie);
            request.Headers.TryAddWithoutValidation("Host", _config.Server);
            request.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", "empty");
            request.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "cors");
            request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "same-site");

            var response = await _httpClient.SendAsync(request);
            if (response.IsSuccessStatusCode)
            {
                // Read response as bytes first to handle compression properly
                var responseBytes = await response.Content.ReadAsByteArrayAsync();

                // Decompress if needed (check for gzip magic number 0x1F 0x8B)
                string responseJson;
                if (
                    responseBytes.Length >= 2
                    && responseBytes[0] == 0x1F
                    && responseBytes[1] == 0x8B
                )
                {
                    using var compressedStream = new MemoryStream(responseBytes);
                    using var gzipStream = new GZipStream(
                        compressedStream,
                        CompressionMode.Decompress
                    );
                    using var decompressedStream = new MemoryStream();
                    await gzipStream.CopyToAsync(decompressedStream);
                    responseJson = Encoding.UTF8.GetString(decompressedStream.ToArray());
                }
                else
                {
                    responseJson = Encoding.UTF8.GetString(responseBytes);
                }

                return JsonSerializer.Deserialize<JsonElement>(responseJson);
            }

            if (response.StatusCode == HttpStatusCode.UnprocessableEntity) // 422
            {
                _logger.LogWarning($"Rate limited (422) fetching from {url}");
                throw new HttpRequestException("422 UnprocessableEntity - Rate limited");
            }

            _logger.LogWarning($"Failed to fetch from {url}: {response.StatusCode}");
            throw new HttpRequestException(
                $"HTTP {(int)response.StatusCode} {response.StatusCode}"
            );
        }
        catch (HttpRequestException)
        {
            // Re-throw HTTP exceptions (including rate limiting) to be handled by retry logic
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error fetching from {url}: {ex.Message}");
            throw new HttpRequestException($"Request failed: {ex.Message}", ex);
        }
    }

    /// <summary>
    ///     Generate a unique ID for a treatment based on its data and timestamp
    /// </summary>
    /// <summary>
    ///     Normalize alarm type strings to standard values
    ///     Handles various Glooko alarm type formats (different devices may use different naming)
    /// </summary>
    /// <summary>
    ///     Fetch from Glooko endpoint with retry logic and exponential backoff
    ///     Implements the legacy rate limiting strategy to avoid 422 errors
    /// </summary>
    private async Task<JsonElement?> FetchFromGlookoEndpointWithRetry(
        string url,
        int maxRetries = 3
    )
    {
        HttpRequestException? lastException = null;

        for (var attempt = 0; attempt < maxRetries; attempt++)
        {
            try
            {
                var result = await FetchFromGlookoEndpoint(url);
                if (result.HasValue) return result;

                // If we get here, the request failed but didn't throw
                _logger.LogWarning($"Attempt {attempt + 1} failed for {url}");
            }
            catch (HttpRequestException ex) when (ex.Message.Contains("422"))
            {
                lastException = ex;
                _logger.LogWarning($"Rate limited (422) on attempt {attempt + 1} for {url}");
            }
            catch (HttpRequestException ex)
            {
                lastException = ex;
                _logger.LogError($"Attempt {attempt + 1} failed for {url}: {ex.Message}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Attempt {attempt + 1} failed for {url}: {ex.Message}");
                lastException = new HttpRequestException($"Request failed: {ex.Message}", ex);
            } // Don't delay after the last attempt

            if (attempt < maxRetries - 1)
            {
                _logger.LogInformation($"Applying retry backoff before retry {attempt + 2}");
                await _retryDelayStrategy.ApplyRetryDelayAsync(attempt);
            }
        }

        _logger.LogError($"All {maxRetries} attempts failed for {url}");

        // Throw the last exception if we have one, otherwise throw a generic exception
        if (lastException != null) throw lastException;
        throw new HttpRequestException($"All {maxRetries} attempts failed for {url}");
    }

    /// <summary>
    ///     Get corrected Glooko timestamp. Glooko reports local time as if it were UTC.
    /// </summary>
    /// <summary>
    ///     Get corrected Glooko timestamp from unix seconds.
    /// </summary>
    private bool IsSessionExpired()
    {
        return string.IsNullOrEmpty(_tokenProvider.SessionCookie);
    }

    #region V3 API Methods

    private string? _meterUnits;

    /// <summary>
    ///     Fetch user profile from v3 API to get meter units setting
    /// </summary>
    public async Task<GlookoV3UsersResponse?> FetchV3UserProfileAsync()
    {
        try
        {
            if (string.IsNullOrEmpty(_tokenProvider.SessionCookie))
                throw new InvalidOperationException(
                    "Not authenticated with Glooko. Call AuthenticateAsync first."
                );

            var url = "/api/v3/session/users";
            _logger.LogDebug("Fetching Glooko v3 user profile from {Url}", url);

            var result = await FetchFromGlookoEndpoint(url);
            if (result.HasValue)
            {
                var profile = JsonSerializer.Deserialize<GlookoV3UsersResponse>(
                    result.Value.GetRawText()
                );
                if (profile?.CurrentUser != null)
                {
                    _meterUnits = profile.CurrentUser.MeterUnits;
                    _logger.LogInformation(
                        "[{ConnectorSource}] User profile loaded. MeterUnits: {Units}",
                        ConnectorSource,
                        _meterUnits
                    );
                }

                return profile;
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching Glooko v3 user profile");
            return null;
        }
    }

    /// <summary>
    ///     Fetch data from v3 graph/data API - single call for all data types
    /// </summary>
    public async Task<GlookoV3GraphResponse?> FetchV3GraphDataAsync(DateTime? since = null)
    {
        try
        {
            if (string.IsNullOrEmpty(_tokenProvider.SessionCookie))
                throw new InvalidOperationException(
                    "Not authenticated with Glooko. Call AuthenticateAsync first."
                );

            if (_tokenProvider.UserData?.UserLogin?.GlookoCode == null)
            {
                _logger.LogWarning("Missing Glooko user code, cannot fetch v3 data");
                return null;
            }

            // Ensure we have meter units
            if (string.IsNullOrEmpty(_meterUnits)) await FetchV3UserProfileAsync();

            var fromDate = since ?? DateTime.UtcNow.AddDays(-1);
            var toDate = DateTime.UtcNow;

            var url = ConstructV3GraphUrl(fromDate, toDate);
            _logger.LogInformation(
                "[{ConnectorSource}] Fetching v3 graph data from {StartDate:yyyy-MM-dd} to {EndDate:yyyy-MM-dd}",
                ConnectorSource,
                fromDate,
                toDate
            );

            var result = await FetchFromGlookoEndpointWithRetry(url);
            if (result.HasValue)
            {
                var graphData = JsonSerializer.Deserialize<GlookoV3GraphResponse>(
                    result.Value.GetRawText()
                );

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
                        + (graphData.Series.CgmLow?.Length ?? 0)
                    );

                return graphData;
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching Glooko v3 graph data");
            return null;
        }
    }

    /// <summary>
    ///     Construct URL for v3 graph/data endpoint with all requested series
    /// </summary>
    private string ConstructV3GraphUrl(DateTime startDate, DateTime endDate)
    {
        var patientCode = _tokenProvider.UserData?.UserLogin?.GlookoCode;

        // Series to request
        var series = new[]
        {
            "automaticBolus",
            "deliveredBolus",
            "injectionBolus",
            "pumpAlarm",
            "reservoirChange",
            "setSiteChange",
            "carbAll",
            "scheduledBasal",
            "temporaryBasal",
            "suspendBasal",
            "profileChange"
        };

        // Add CGM series if backfill is enabled
        if (_config.V3IncludeCgmBackfill) series = series.Concat(new[] { "cgmHigh", "cgmNormal", "cgmLow" }).ToArray();

        var seriesParams = string.Join("&", series.Select(s => $"series[]={s}"));

        return $"/api/v3/graph/data?patient={patientCode}"
               + $"&startDate={startDate:yyyy-MM-ddTHH:mm:ss.fffZ}"
               + $"&endDate={endDate:yyyy-MM-ddTHH:mm:ss.fffZ}"
               + $"&{seriesParams}"
               + "&locale=en&insulinTooltips=false&filterBgReadings=false&splitByDay=false";
    }

    #endregion
}