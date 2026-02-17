using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.MyLife.Configurations;
using Nocturne.Connectors.MyLife.Mappers;
using Nocturne.Connectors.MyLife.Models;
using Nocturne.Core.Constants;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.Connectors.MyLife.Services;

/// <summary>
/// MyLife connector service that syncs data using granular models.
/// This connector creates SensorGlucose, Bolus, CarbIntake, BGCheck, Note,
/// DeviceEvent, and StateSpan records directly instead of legacy Entry/Treatment.
/// </summary>
public class MyLifeConnectorService(
    HttpClient httpClient,
    IOptions<MyLifeConnectorConfiguration> config,
    ILogger<MyLifeConnectorService> logger,
    MyLifeAuthTokenProvider tokenProvider,
    MyLifeEventsCache eventsCache,
    MyLifeEventProcessor eventProcessor,
    MyLifeSessionStore sessionStore,
    IConnectorPublisher? publisher = null
) : BaseConnectorService<MyLifeConnectorConfiguration>(httpClient, logger, publisher)
{
    private readonly MyLifeConnectorConfiguration _config = config.Value;

    public override string ServiceName => "MyLife";
    protected override string ConnectorSource => DataSources.MyLifeConnector;

    public override List<SyncDataType> SupportedDataTypes =>
        [SyncDataType.Glucose, SyncDataType.Treatments];

    public override bool IsHealthy =>
        FailedRequestCount < MaxFailedRequestsBeforeUnhealthy && !tokenProvider.IsTokenExpired;

    public override async Task<bool> AuthenticateAsync()
    {
        var token = await tokenProvider.GetValidTokenAsync();
        if (string.IsNullOrWhiteSpace(token))
        {
            sessionStore.Clear();
            TrackFailedRequest("Token missing");
            return false;
        }

        TrackSuccessfulRequest();
        return true;
    }

    /// <summary>
    /// Legacy method required by IConnectorService interface.
    /// Returns empty - use FetchSensorGlucoseAsync for glucose data.
    /// </summary>
    public override Task<IEnumerable<Entry>> FetchGlucoseDataAsync(DateTime? since = null)
    {
        // Connectors don't create Entry objects - return empty
        return Task.FromResult(Enumerable.Empty<Entry>());
    }

    /// <summary>
    /// Fetches SensorGlucose records from MyLife events.
    /// </summary>
    public async Task<IEnumerable<SensorGlucose>> FetchSensorGlucoseAsync(DateTime? since = null)
    {
        var actualSince = await CalculateSinceTimestampAsync(_config, since);
        var events = await eventsCache.GetEventsAsync(
            actualSince,
            DateTime.UtcNow,
            CancellationToken.None
        );

        var filtered = FilterEventsBySince(events, actualSince);
        return eventProcessor.MapSensorGlucose(filtered, _config.EnableGlucoseSync);
    }

    /// <summary>
    /// Fetches all records (Bolus, CarbIntake, BGCheck, Note, DeviceEvent, etc.) from MyLife events.
    /// </summary>
    public async Task<MyLifeResult> FetchRecordsAsync(DateTime? from, DateTime? to)
    {
        var actualSince = await CalculateTreatmentSinceTimestampAsync(_config, from);
        var actualUntil = to ?? DateTime.UtcNow;
        var events = await eventsCache.GetEventsAsync(
            actualSince,
            actualUntil,
            CancellationToken.None
        );

        var filtered = FilterEventsBySince(events, actualSince);
        return eventProcessor.MapRecords(
            filtered,
            _config.EnableManualBgSync,
            _config.EnableMealCarbConsolidation,
            _config.EnableTempBasalConsolidation,
            _config.TempBasalConsolidationWindowMinutes
        );
    }

    /// <summary>
    /// Fetches BasalDelivery StateSpans from MyLife events.
    /// </summary>
    public async Task<IEnumerable<StateSpan>> FetchStateSpansAsync(DateTime? from, DateTime? to)
    {
        var actualSince = await CalculateTreatmentSinceTimestampAsync(_config, from);
        var actualUntil = to ?? DateTime.UtcNow;
        var events = await eventsCache.GetEventsAsync(
            actualSince,
            actualUntil,
            CancellationToken.None
        );

        var filtered = FilterEventsBySince(events, actualSince);
        return MyLifeStateSpanMapper.MapStateSpans(
            filtered,
            _config.EnableTempBasalConsolidation,
            _config.TempBasalConsolidationWindowMinutes
        );
    }

    /// <summary>
    /// Performs sync, publishing all granular model types directly.
    /// </summary>
    protected override async Task<SyncResult> PerformSyncInternalAsync(
        SyncRequest request,
        MyLifeConnectorConfiguration config,
        CancellationToken cancellationToken
    )
    {
        var result = new SyncResult { StartTime = DateTimeOffset.UtcNow, Success = true };

        if (!request.DataTypes.Any())
            request.DataTypes = SupportedDataTypes;

        try
        {
            // Sync glucose data as SensorGlucose
            if (request.DataTypes.Contains(SyncDataType.Glucose))
            {
                var sensorGlucose = await FetchSensorGlucoseAsync(request.From);
                var sgList = sensorGlucose.ToList();

                if (sgList.Count > 0)
                {
                    var success = await PublishSensorGlucoseDataAsync(
                        sgList,
                        config,
                        cancellationToken
                    );
                    result.ItemsSynced[SyncDataType.Glucose] = sgList.Count;
                    if (sgList.Count > 0)
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
                        _logger.LogInformation(
                            "Synced {Count} SensorGlucose records",
                            sgList.Count
                        );
                    }
                }
            }

            // Sync treatment data as granular models
            if (request.DataTypes.Contains(SyncDataType.Treatments))
            {
                var records = await FetchRecordsAsync(request.From, request.To);
                var totalCount = 0;
                var allSuccess = true;

                // Publish Boluses
                if (records.Boluses.Count > 0)
                {
                    var success = await PublishBolusDataAsync(
                        records.Boluses,
                        config,
                        cancellationToken
                    );
                    if (success)
                    {
                        _logger.LogInformation(
                            "Synced {Count} Bolus records",
                            records.Boluses.Count
                        );
                        totalCount += records.Boluses.Count;
                    }
                    else
                    {
                        allSuccess = false;
                        result.Errors.Add("Bolus publish failed");
                    }
                }

                // Publish CarbIntakes
                if (records.CarbIntakes.Count > 0)
                {
                    var success = await PublishCarbIntakeDataAsync(
                        records.CarbIntakes,
                        config,
                        cancellationToken
                    );
                    if (success)
                    {
                        _logger.LogInformation(
                            "Synced {Count} CarbIntake records",
                            records.CarbIntakes.Count
                        );
                        totalCount += records.CarbIntakes.Count;
                    }
                    else
                    {
                        allSuccess = false;
                        result.Errors.Add("CarbIntake publish failed");
                    }
                }

                // Publish BGChecks
                if (records.BGChecks.Count > 0)
                {
                    var success = await PublishBGCheckDataAsync(
                        records.BGChecks,
                        config,
                        cancellationToken
                    );
                    if (success)
                    {
                        _logger.LogInformation(
                            "Synced {Count} BGCheck records",
                            records.BGChecks.Count
                        );
                        totalCount += records.BGChecks.Count;
                    }
                    else
                    {
                        allSuccess = false;
                        result.Errors.Add("BGCheck publish failed");
                    }
                }

                // Publish BolusCalculations
                if (records.BolusCalculations.Count > 0)
                {
                    var success = await PublishBolusCalculationDataAsync(
                        records.BolusCalculations,
                        config,
                        cancellationToken
                    );
                    if (success)
                    {
                        _logger.LogInformation(
                            "Synced {Count} BolusCalculation records",
                            records.BolusCalculations.Count
                        );
                        totalCount += records.BolusCalculations.Count;
                    }
                    else
                    {
                        allSuccess = false;
                        result.Errors.Add("BolusCalculation publish failed");
                    }
                }

                // Publish Notes
                if (records.Notes.Count > 0)
                {
                    var success = await PublishNoteDataAsync(
                        records.Notes,
                        config,
                        cancellationToken
                    );
                    if (success)
                    {
                        _logger.LogInformation("Synced {Count} Note records", records.Notes.Count);
                        totalCount += records.Notes.Count;
                    }
                    else
                    {
                        allSuccess = false;
                        result.Errors.Add("Note publish failed");
                    }
                }

                // Publish DeviceEvents
                if (records.DeviceEvents.Count > 0)
                {
                    var success = await PublishDeviceEventDataAsync(
                        records.DeviceEvents,
                        config,
                        cancellationToken
                    );
                    if (success)
                    {
                        _logger.LogInformation(
                            "Synced {Count} DeviceEvent records",
                            records.DeviceEvents.Count
                        );
                        totalCount += records.DeviceEvents.Count;
                    }
                    else
                    {
                        allSuccess = false;
                        result.Errors.Add("DeviceEvent publish failed");
                    }
                }

                // Publish StateSpans for basal delivery
                var stateSpans = await FetchStateSpansAsync(request.From, request.To);
                var stateSpanList = stateSpans.ToList();

                if (stateSpanList.Count > 0)
                {
                    var success = await PublishStateSpanDataAsync(
                        stateSpanList,
                        config,
                        cancellationToken
                    );
                    if (success)
                    {
                        _logger.LogInformation(
                            "Synced {Count} StateSpan records",
                            stateSpanList.Count
                        );
                    }
                    else
                    {
                        _logger.LogWarning("Failed to sync some StateSpan records");
                    }
                }

                result.ItemsSynced[SyncDataType.Treatments] = totalCount;
                if (!allSuccess)
                    result.Success = false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during sync");
            result.Success = false;
            result.Errors.Add($"Sync error: {ex.Message}");
        }

        result.EndTime = DateTimeOffset.UtcNow;
        return result;
    }

    private static IEnumerable<MyLifeEvent> FilterEventsBySince(
        IEnumerable<MyLifeEvent> events,
        DateTime since
    )
    {
        var sinceTicks = new DateTimeOffset(since).ToUnixTimeMilliseconds() * 10_000;
        return events.Where(e => e.EventDateTime >= sinceTicks);
    }
}
