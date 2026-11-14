using System.Text;
using Microsoft.Extensions.Logging;
using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.Core.Utilities;
using Nocturne.Connectors.Dexcom.Configurations;
using Nocturne.Connectors.Dexcom.Mappers;
using Nocturne.Connectors.Dexcom.Models;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.Connectors.Dexcom.Services;

/// <summary>
///     Connector service for Dexcom Share data source.
/// </summary>
public class DexcomConnectorService
    : TokenConnectorService<DexcomConnectorConfiguration, DexcomAuthTokenProvider>
{
    private readonly DexcomSensorGlucoseMapper _sensorGlucoseMapper;

    public DexcomConnectorService(
        HttpClient httpClient,
        IConnectorServerResolver<DexcomConnectorConfiguration> serverResolver,
        ILogger<DexcomConnectorService> logger,
        IRetryDelayStrategy retryDelayStrategy,
        DexcomAuthTokenProvider tokenProvider,
        IConnectorPublisher? publisher = null,
        TimeProvider? timeProvider = null
    )
        : base(httpClient, serverResolver, logger, retryDelayStrategy, tokenProvider, publisher, timeProvider)
    {
        _sensorGlucoseMapper = new DexcomSensorGlucoseMapper(logger);
    }

    public override string ServiceName => "Dexcom Share";

    /// <summary>
    ///     Fetches SensorGlucose records from the Dexcom Share API.
    /// </summary>
    private async Task<IEnumerable<SensorGlucose>> FetchSensorGlucoseAsync(
        DexcomConnectorConfiguration config, DateTime? since, CancellationToken cancellationToken)
    {
        var batchData = await FetchBatchDataAsync(config, since, cancellationToken);
        if (batchData == null) return [];

        var records = _sensorGlucoseMapper.MapBatchData(batchData, Clock.GetUtcNow().UtcDateTime).ToList();
        _logger.LogInformation(
            "[{ConnectorSource}] Retrieved {Count} SensorGlucose records from Dexcom",
            ConnectorSource,
            records.Count
        );
        return records;
    }

    /// <summary>
    ///     Performs sync, publishing SensorGlucose records directly to the V4 data store.
    /// </summary>
    protected override async Task<SyncResult> PerformSyncInternalAsync(
        SyncRequest request,
        DexcomConnectorConfiguration config,
        CancellationToken cancellationToken)
    {
        var result = new SyncResult { Success = true };

        var activeTypes = ResolveActiveTypes(request, config);
        if (!activeTypes.Contains(SyncDataType.Glucose))
        {
            return result;
        }

        if (await ValidTokenAsync(config, cancellationToken) is null)
            return AuthenticationFailedResult();

        try
        {
            var sensorGlucose = await FetchSensorGlucoseAsync(config, request.From, cancellationToken);

            await PublishRecordTypeAsync(result, activeTypes,
                sensorGlucose.ToList(), config, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || ConnectorFaults.IsTimeout(ex, cancellationToken))
        {
            RecordSyncError(result, ex);
        }

        return result;
    }

    private async Task<DexcomEntry[]?> FetchBatchDataAsync(
        DexcomConnectorConfiguration config, DateTime? since, CancellationToken cancellationToken)
    {
        var sessionId = await ValidTokenAsync(config, cancellationToken);
        if (sessionId is null)
        {
            _logger.LogWarning(
                "[{ConnectorSource}] Failed to get valid session, authentication failed",
                ConnectorSource
            );
            return null;
        }

        var fetched = await ExecuteWithRetryAsync(
            async () => await FetchRawDataCoreAsync(config, sessionId, since, cancellationToken),
            ReauthenticateWith(config, t => sessionId = t, cancellationToken),
            maxRetries: config.MaxRetryAttempts,
            operationName: "FetchDexcomData",
            cancellationToken: cancellationToken
        );

        if (fetched == null) return null;
        var result = ConnectorFaults.NonNullRecords(fetched, _logger, ConnectorSource, "Dexcom entry").ToArray();
        var validEntries = result.Where(e => e.Value > 0).ToArray();
        var minDate = validEntries.Length > 0 ? validEntries.Min(e => e.Wt) : "N/A";
        var maxDate = validEntries.Length > 0 ? validEntries.Max(e => e.Wt) : "N/A";

        _logger.LogInformation(
            "[{ConnectorSource}] Fetched Dexcom batch data: TotalEntries={TotalCount}, ValidEntries={ValidCount}, DateRange={MinDate} to {MaxDate}",
            ConnectorSource,
            result.Length,
            validEntries.Length,
            minDate,
            maxDate
        );

        return result;
    }

    internal static DateTime CalculateStartTime(DateTime? since, DateTime now) =>
        since ?? now.AddDays(-2);

    private async Task<DexcomEntry[]?> FetchRawDataCoreAsync(
        DexcomConnectorConfiguration config, string sessionId, DateTime? since, CancellationToken cancellationToken)
    {
        var now = Clock.GetUtcNow().UtcDateTime;
        var startTime = CalculateStartTime(since, now);

        var timeDiff = now - startTime;
        var maxCount = Math.Ceiling(timeDiff.TotalMinutes / 5);
        var minutes = (int)(maxCount * 5);

        var path =
            $"/ShareWebServices/Services/Publisher/ReadPublisherLatestGlucoseValues?sessionID={sessionId}&minutes={minutes}&maxCount={(int)maxCount}";

        var response = await _httpClient.PostAsync(
            _serverResolver.BuildUrl(config, path),
            new StringContent("{}", Encoding.UTF8, "application/json"),
            cancellationToken
        );

        await response.EnsureSuccessWithBodyAsync(cancellationToken);

        var dexcomEntries = await DeserializeResponseAsync<DexcomEntry[]>(response, cancellationToken);
        return dexcomEntries ?? [];
    }
}
