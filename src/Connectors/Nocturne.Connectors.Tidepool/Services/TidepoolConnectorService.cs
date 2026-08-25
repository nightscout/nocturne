using Microsoft.Extensions.Logging;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.Tidepool.Configurations;
using Nocturne.Connectors.Tidepool.Mappers;
using Nocturne.Connectors.Tidepool.Models;
using Nocturne.Core.Constants;
using Nocturne.Core.Models;

namespace Nocturne.Connectors.Tidepool.Services;

/// <summary>
///     Connector service for Tidepool data source.
///     Fetches glucose readings and bolus/food entries, writing V4 models directly.
/// </summary>
public class TidepoolConnectorService : BaseConnectorService<TidepoolConnectorConfiguration>
{
    private readonly IRateLimitingStrategy _rateLimitingStrategy;
    private readonly IRetryDelayStrategy _retryDelayStrategy;
    private readonly TidepoolSensorGlucoseMapper _sensorGlucoseMapper;
    private readonly TidepoolAuthTokenProvider _tokenProvider;
    private readonly TidepoolV4TreatmentMapper _v4TreatmentMapper;

    public TidepoolConnectorService(
        HttpClient httpClient,
        IConnectorServerResolver<TidepoolConnectorConfiguration> serverResolver,
        ILogger<TidepoolConnectorService> logger,
        IRetryDelayStrategy retryDelayStrategy,
        IRateLimitingStrategy rateLimitingStrategy,
        TidepoolAuthTokenProvider tokenProvider,
        IConnectorPublisher? publisher = null
    )
        : base(httpClient, serverResolver, logger, publisher)
    {
        _retryDelayStrategy =
            retryDelayStrategy ?? throw new ArgumentNullException(nameof(retryDelayStrategy));
        _rateLimitingStrategy =
            rateLimitingStrategy ?? throw new ArgumentNullException(nameof(rateLimitingStrategy));
        _tokenProvider =
            tokenProvider ?? throw new ArgumentNullException(nameof(tokenProvider));
        _sensorGlucoseMapper = new TidepoolSensorGlucoseMapper(logger, ConnectorSource);
        _v4TreatmentMapper = new TidepoolV4TreatmentMapper(logger, ConnectorSource);
    }

    protected override string ConnectorSource => DataSources.TidepoolConnector;
    public override string ServiceName => "Tidepool";

    public override Task<bool> AuthenticateAsync()
    {
        // Auth happens per-tenant inside PerformSyncInternalAsync where config is available
        TrackSuccessfulRequest();
        return Task.FromResult(true);
    }

    protected override async Task<SyncResult> PerformSyncInternalAsync(
        SyncRequest request,
        TidepoolConnectorConfiguration config,
        CancellationToken cancellationToken)
    {
        var result = new SyncResult { StartTime = DateTimeOffset.UtcNow, Success = true };

        if (!request.DataTypes.Any())
            request.DataTypes = SupportedDataTypes;

        var enabledTypes = config.GetEnabledDataTypes(SupportedDataTypes);
        var activeTypes = request.DataTypes.Where(t => enabledTypes.Contains(t)).ToHashSet();

        // Authenticate up front. The data fetches below treat a missing token as "no data" and
        // return null without raising an error, so without this gate an auth failure (e.g. bad
        // credentials returning 401) would be recorded as a successful, healthy sync — masking a
        // broken connector so it never surfaces as unhealthy. Fail the sync explicitly instead.
        // The token is cached, so the fetches below reuse it rather than re-authenticating.
        if (activeTypes.Count > 0)
        {
            var token = await _tokenProvider.GetValidTokenAsync(config, cancellationToken);
            if (string.IsNullOrEmpty(token))
            {
                _logger.LogWarning("[{ConnectorSource}] Sync failed: authentication unsuccessful", ConnectorSource);
                return AuthenticationFailedResult();
            }
        }

        // CBG and SMBG both map to SensorGlucose.
        if (activeTypes.Contains(SyncDataType.Glucose))
        {
            try
            {
                var bgValues = await FetchDataAsync<TidepoolBgValue[]>(
                    config,
                    $"{TidepoolConstants.DataTypes.Cbg},{TidepoolConstants.DataTypes.Smbg}",
                    request.From, request.To);

                if (bgValues is null)
                {
                    RecordFetchFailure(result, SyncDataType.Glucose, activeTypes);
                }
                else
                {
                    await PublishRecordTypeAsync(result, SyncDataType.Glucose, activeTypes,
                        _sensorGlucoseMapper.MapBgValues(bgValues).ToList(), PublishSensorGlucoseDataAsync,
                        config, cancellationToken);
                }
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Errors.Add($"Failed to sync Glucose: {ex.Message}");
                _logger.LogError(ex, "Failed to sync Glucose for {Connector}", ConnectorSource);
            }
        }

        SyncDataType[] treatmentTypes = [SyncDataType.Boluses, SyncDataType.CarbIntake];
        if (activeTypes.Any(t => treatmentTypes.Contains(t)))
        {
            try
            {
                var bolusTask = FetchDataAsync<TidepoolBolus[]>(config, TidepoolConstants.DataTypes.Bolus, request.From, request.To);
                var foodTask = FetchDataAsync<TidepoolFood[]>(config, TidepoolConstants.DataTypes.Food, request.From, request.To);
                await Task.WhenAll(bolusTask, foodTask);

                var boluses = await bolusTask;
                var foods = await foodTask;

                var (mappedBoluses, mappedCarbs, _) = _v4TreatmentMapper.MapTreatments(boluses, foods);

                // Both fetches are issued whenever either type is active, because the correlation
                // that pairs a carb intake with a bolus needs the boluses even when boluses
                // themselves are not being synced.
                if (boluses is null)
                    RecordFetchFailure(result, SyncDataType.Boluses, activeTypes);
                if (foods is null)
                    RecordFetchFailure(result, SyncDataType.CarbIntake, activeTypes);

                // Boluses come from the bolus fetch and carb intakes from the food fetch, so each
                // type is reported only when its own fetch came back.
                if (boluses is not null)
                    await PublishRecordTypeAsync(result, SyncDataType.Boluses, activeTypes,
                        mappedBoluses, PublishBolusDataAsync, config, cancellationToken);

                if (foods is not null)
                    await PublishRecordTypeAsync(result, SyncDataType.CarbIntake, activeTypes,
                        mappedCarbs, PublishCarbIntakeDataAsync, config, cancellationToken);
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Errors.Add($"Failed to sync Treatments: {ex.Message}");
                _logger.LogError(ex, "Failed to sync Treatments for {Connector}", ConnectorSource);
            }
        }

        result.EndTime = DateTimeOffset.UtcNow;
        return result;
    }

    /// <summary>
    ///     Fetches typed data from the Tidepool API data endpoint.
    /// </summary>
    /// <returns>
    ///     The deserialized collection, or <c>null</c> when the fetch failed — no token, an error
    ///     response, or exhausted retries. A caller must not read that as an empty window: a type
    ///     the sync could not check has to go unreported, because the count the tenant's sync card
    ///     renders as "checked, found nothing" is a claim the source was reached.
    /// </returns>
    private async Task<T?> FetchDataAsync<T>(
        TidepoolConnectorConfiguration config,
        string dataType, DateTime? startDate = null, DateTime? endDate = null) where T : class
    {
        var token = await _tokenProvider.GetValidTokenAsync(config);
        var cached = await _tokenProvider.GetCachedSessionAsync();
        var userId = cached?.Metadata?.GetValueOrDefault("UserId");
        if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(userId))
        {
            _logger.LogWarning(
                "[{ConnectorSource}] Cannot fetch data: missing token or user ID",
                ConnectorSource);
            return null;
        }

        await _rateLimitingStrategy.ApplyDelayAsync(0);

        return await ExecuteWithRetryAsync(
            async () => await FetchDataCoreAsync<T>(config, token, userId, dataType, startDate, endDate),
            _retryDelayStrategy,
            async () =>
            {
                _tokenProvider.InvalidateToken();
                var newToken = await _tokenProvider.GetValidTokenAsync(config);
                if (string.IsNullOrEmpty(newToken)) return false;
                token = newToken;
                // Re-read userId from refreshed cache
                var refreshedSession = await _tokenProvider.GetCachedSessionAsync();
                userId = refreshedSession?.Metadata?.GetValueOrDefault("UserId");
                return !string.IsNullOrEmpty(userId);
            },
            maxRetries: config.MaxRetryAttempts,
            operationName: $"FetchTidepoolData({dataType})"
        );
    }

    private async Task<T?> FetchDataCoreAsync<T>(
        TidepoolConnectorConfiguration config,
        string token, string userId, string dataType, DateTime? startDate, DateTime? endDate) where T : class
    {
        var url = $"/data/{userId}?type={dataType}";

        if (startDate.HasValue)
            url += $"&startDate={startDate.Value.ToUniversalTime():o}";
        if (endDate.HasValue)
            url += $"&endDate={endDate.Value.ToUniversalTime():o}";

        url = _serverResolver.BuildUrl(config, url);

        var headers = new Dictionary<string, string>
        {
            [TidepoolConstants.Headers.SessionToken] = token
        };

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
}
