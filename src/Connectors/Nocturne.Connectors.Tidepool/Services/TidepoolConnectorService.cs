using Microsoft.Extensions.Logging;
using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.Core.Utilities;
using Nocturne.Connectors.Tidepool.Configurations;
using Nocturne.Connectors.Tidepool.Mappers;
using Nocturne.Connectors.Tidepool.Models;
using Nocturne.Core.Models;

namespace Nocturne.Connectors.Tidepool.Services;

/// <summary>
///     Connector service for Tidepool data source.
///     Fetches glucose readings and bolus/food entries, writing V4 models directly.
/// </summary>
public class TidepoolConnectorService
    : TokenConnectorService<TidepoolConnectorConfiguration, TidepoolAuthTokenProvider>
{
    private readonly TidepoolSensorGlucoseMapper _sensorGlucoseMapper;
    private readonly TidepoolV4TreatmentMapper _v4TreatmentMapper;

    public TidepoolConnectorService(
        HttpClient httpClient,
        IConnectorServerResolver<TidepoolConnectorConfiguration> serverResolver,
        ILogger<TidepoolConnectorService> logger,
        IRetryDelayStrategy retryDelayStrategy,
        TidepoolAuthTokenProvider tokenProvider,
        IConnectorPublisher? publisher = null,
        TimeProvider? timeProvider = null
    )
        : base(httpClient, serverResolver, logger, retryDelayStrategy, tokenProvider, publisher, timeProvider)
    {
        _sensorGlucoseMapper = new TidepoolSensorGlucoseMapper(logger, ConnectorSource);
        _v4TreatmentMapper = new TidepoolV4TreatmentMapper(logger, ConnectorSource);
    }

    public override string ServiceName => "Tidepool";

    protected override bool NeedsSignIn(SyncRequest request, TidepoolConnectorConfiguration config) =>
        ResolveActiveTypes(request, config).Count > 0;

    protected override IEnumerable<ResumeKey> ResumesFrom(SyncRequest request, TidepoolConnectorConfiguration config) =>
        WindowResumesFrom(request, ResumeFamily.Glucose);

    protected override async Task<SyncResult> SyncWithTokenAsync(
        SyncRequest request,
        TidepoolConnectorConfiguration config,
        TokenSession session,
        CancellationToken cancellationToken)
    {
        var result = new SyncResult { Success = true };

        var activeTypes = ResolveActiveTypes(request, config);
        if (activeTypes.Count == 0)
            return result;

        // Every type is read in one window, which starts where glucose resumes.
        var from = WindowStart(request, ResumeFamily.Glucose);

        // CBG and SMBG both map to SensorGlucose.
        if (activeTypes.Contains(SyncDataType.Glucose))
        {
            try
            {
                var bgValues = await FetchDataAsync<TidepoolBgValue[]>(
                    config, session,
                    $"{TidepoolConstants.DataTypes.Cbg},{TidepoolConstants.DataTypes.Smbg}",
                    from, request.To, cancellationToken);

                if (bgValues is null)
                {
                    RecordFetchFailure(result, SyncDataType.Glucose, activeTypes);
                }
                else
                {
                    await PublishRecordTypeAsync(result, activeTypes,
                        _sensorGlucoseMapper.MapBgValues(bgValues, Clock.GetUtcNow().UtcDateTime).ToList(), config, cancellationToken);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || ConnectorFaults.IsTimeout(ex, cancellationToken))
            {
                RecordTypeSyncError(result, "Glucose", ex);
            }
        }

        SyncDataType[] treatmentTypes = [SyncDataType.Boluses, SyncDataType.CarbIntake];
        if (activeTypes.Any(t => treatmentTypes.Contains(t)))
        {
            try
            {
                var bolusTask = FetchDataAsync<TidepoolBolus[]>(config, session, TidepoolConstants.DataTypes.Bolus, from, request.To, cancellationToken);
                var foodTask = FetchDataAsync<TidepoolFood[]>(config, session, TidepoolConstants.DataTypes.Food, from, request.To, cancellationToken);
                await Task.WhenAll(bolusTask, foodTask);

                var boluses = await bolusTask;
                var foods = await foodTask;

                var (mappedBoluses, mappedCarbs, _) = _v4TreatmentMapper.MapTreatments(boluses, foods, Clock.GetUtcNow().UtcDateTime);

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
                    await PublishRecordTypeAsync(result, activeTypes,
                        mappedBoluses, config, cancellationToken);

                if (foods is not null)
                    await PublishRecordTypeAsync(result, activeTypes,
                        mappedCarbs, config, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || ConnectorFaults.IsTimeout(ex, cancellationToken))
            {
                RecordTypeSyncError(result, "Treatments", ex);
            }
        }

        return result;
    }

    /// <summary>
    ///     Fetches typed data from the Tidepool API data endpoint.
    /// </summary>
    /// <returns>
    ///     The deserialized collection, or <c>null</c> when the fetch failed: no user id, an error
    ///     response, or exhausted retries. A caller must not read that as an empty window: a type
    ///     the sync could not check has to go unreported, because the count the tenant's sync card
    ///     renders as "checked, found nothing" is a claim the source was reached.
    /// </returns>
    private async Task<T?> FetchDataAsync<T>(
        TidepoolConnectorConfiguration config, TokenSession session,
        string dataType, DateTime? startDate, DateTime? endDate, CancellationToken cancellationToken) where T : class
    {
        var cached = await TokenProvider.GetCachedSessionAsync();
        var userId = cached?.Metadata?.GetValueOrDefault("UserId");
        if (string.IsNullOrEmpty(userId))
        {
            _logger.LogWarning(
                "[{ConnectorSource}] Cannot fetch data: missing user ID",
                ConnectorSource);
            return null;
        }

        return await ExecuteWithRetryAsync(
            async () => await FetchDataCoreAsync<T>(config, session.Token, userId, dataType, startDate, endDate, cancellationToken),
            async () =>
            {
                if (!await session.ReauthenticateAsync()) return false;
                var refreshedSession = await TokenProvider.GetCachedSessionAsync();
                userId = refreshedSession?.Metadata?.GetValueOrDefault("UserId");
                return !string.IsNullOrEmpty(userId);
            },
            maxRetries: config.MaxRetryAttempts,
            operationName: $"FetchTidepoolData({dataType})",
            cancellationToken: cancellationToken
        );
    }

    private async Task<T?> FetchDataCoreAsync<T>(
        TidepoolConnectorConfiguration config,
        string token, string userId, string dataType, DateTime? startDate, DateTime? endDate,
        CancellationToken cancellationToken) where T : class
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

        var response = await GetWithHeadersAsync(url, headers, cancellationToken);

        await response.EnsureSuccessWithBodyAsync(cancellationToken);

        return await DeserializeResponseAsync<T>(response, cancellationToken);
    }
}
