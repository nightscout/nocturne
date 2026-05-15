using System.Text;
using Microsoft.Extensions.Logging;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.Dexcom.Configurations;
using Nocturne.Connectors.Dexcom.Mappers;
using Nocturne.Connectors.Dexcom.Models;
using Nocturne.Core.Constants;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.Connectors.Dexcom.Services;

/// <summary>
///     Connector service for Dexcom Share data source.
///     Writes SensorGlucose records directly instead of legacy Entry objects.
/// </summary>
public class DexcomConnectorService : BaseConnectorService<DexcomConnectorConfiguration>
{
    private readonly DexcomSensorGlucoseMapper _sensorGlucoseMapper;
    private readonly IRateLimitingStrategy _rateLimitingStrategy;
    private readonly IRetryDelayStrategy _retryDelayStrategy;
    private readonly DexcomAuthTokenProvider _tokenProvider;

    public DexcomConnectorService(
        HttpClient httpClient,
        IConnectorServerResolver<DexcomConnectorConfiguration> serverResolver,
        ILogger<DexcomConnectorService> logger,
        IRetryDelayStrategy retryDelayStrategy,
        IRateLimitingStrategy rateLimitingStrategy,
        DexcomAuthTokenProvider tokenProvider,
        IConnectorPublisher? publisher = null
    )
        : base(httpClient, serverResolver, logger, publisher)
    {
        _retryDelayStrategy =
            retryDelayStrategy ?? throw new ArgumentNullException(nameof(retryDelayStrategy));
        _rateLimitingStrategy =
            rateLimitingStrategy
            ?? throw new ArgumentNullException(nameof(rateLimitingStrategy));
        _tokenProvider =
            tokenProvider ?? throw new ArgumentNullException(nameof(tokenProvider));
        _sensorGlucoseMapper = new DexcomSensorGlucoseMapper(logger);
    }

    protected override string ConnectorSource => DataSources.DexcomConnector;
    public override string ServiceName => "Dexcom Share";
    public override List<SyncDataType> SupportedDataTypes => [SyncDataType.Glucose];

    public override async Task<bool> AuthenticateAsync()
    {
        // AuthenticateAsync is a legacy method; actual auth happens per-tenant in sync flow
        TrackSuccessfulRequest();
        return true;
    }

    /// <summary>
    ///     Fetches SensorGlucose records from the Dexcom Share API.
    /// </summary>
    private async Task<IEnumerable<SensorGlucose>> FetchSensorGlucoseAsync(
        DexcomConnectorConfiguration config, DateTime? since = null)
    {
        var batchData = await FetchBatchDataAsync(config, since);
        if (batchData == null) return [];

        var records = _sensorGlucoseMapper.MapBatchData(batchData).ToList();
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
        CancellationToken cancellationToken,
        ISyncProgressReporter? progressReporter = null
    )
    {
        var result = new SyncResult { StartTime = DateTimeOffset.UtcNow, Success = true };

        var enabledTypes = config.GetEnabledDataTypes(SupportedDataTypes);
        if (!enabledTypes.Contains(SyncDataType.Glucose))
        {
            result.EndTime = DateTimeOffset.UtcNow;
            return result;
        }

        try
        {
            var sensorGlucose = await FetchSensorGlucoseAsync(config, request.From);
            var sgList = sensorGlucose.ToList();

            if (sgList.Count > 0)
            {
                var success = await PublishSensorGlucoseDataAsync(sgList, config, cancellationToken);
                result.ItemsSynced[SyncDataType.Glucose] = sgList.Count;
                result.LastEntryTimes[SyncDataType.Glucose] = DateTimeOffset
                    .FromUnixTimeMilliseconds(sgList.Max(s => s.Mills))
                    .UtcDateTime;

                if (!success)
                {
                    result.Success = false;
                    result.Errors.Add("SensorGlucose publish failed");
                }
                else
                {
                    _logger.LogInformation("Synced {Count} SensorGlucose records from Dexcom", sgList.Count);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during Dexcom sync");
            result.Success = false;
            result.Errors.Add($"Sync error: {ex.Message}");
        }

        result.EndTime = DateTimeOffset.UtcNow;
        return result;
    }

    private async Task<DexcomEntry[]?> FetchBatchDataAsync(
        DexcomConnectorConfiguration config, DateTime? since = null)
    {
        var sessionId = await _tokenProvider.GetValidTokenAsync(config);
        if (string.IsNullOrEmpty(sessionId))
        {
            _logger.LogWarning(
                "[{ConnectorSource}] Failed to get valid session, authentication failed",
                ConnectorSource
            );
            TrackFailedRequest("Failed to get valid session");
            return null;
        }

        await _rateLimitingStrategy.ApplyDelayAsync(0);

        var result = await ExecuteWithRetryAsync(
            async () => await FetchRawDataCoreAsync(config, sessionId, since),
            _retryDelayStrategy,
            async () =>
            {
                _tokenProvider.InvalidateToken();
                var newToken = await _tokenProvider.GetValidTokenAsync(config);
                if (string.IsNullOrEmpty(newToken)) return false;
                sessionId = newToken;
                return true;
            },
            operationName: "FetchDexcomData"
        );

        if (result == null) return result;
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

    private async Task<DexcomEntry[]?> FetchRawDataCoreAsync(
        DexcomConnectorConfiguration config, string sessionId, DateTime? since = null)
    {
        var twoDaysAgo = DateTime.UtcNow.AddDays(-2);
        var startTime = since.HasValue
            ? since.Value > twoDaysAgo ? since.Value : twoDaysAgo
            : twoDaysAgo;

        var timeDiff = DateTime.UtcNow - startTime;
        var maxCount = Math.Ceiling(timeDiff.TotalMinutes / 5);
        var minutes = (int)(maxCount * 5);

        var path =
            $"/ShareWebServices/Services/Publisher/ReadPublisherLatestGlucoseValues?sessionID={sessionId}&minutes={minutes}&maxCount={(int)maxCount}";

        var response = await _httpClient.PostAsync(
            _serverResolver.BuildUrl(config, path),
            new StringContent("{}", Encoding.UTF8, "application/json")
        );

        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync();
            throw new HttpRequestException(
                $"HTTP {(int)response.StatusCode} {response.StatusCode}: {errorContent}",
                null,
                response.StatusCode
            );
        }

        var dexcomEntries = await DeserializeResponseAsync<DexcomEntry[]>(response);
        return dexcomEntries ?? [];
    }
}
