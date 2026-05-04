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
/// DeviceEvent, and TempBasal records directly instead of legacy Entry/Treatment.
/// </summary>
public class MyLifeConnectorService(
    HttpClient httpClient,
    IOptions<MyLifeConnectorConfiguration> config,
    ILogger<MyLifeConnectorService> logger,
    MyLifeAuthTokenProvider tokenProvider,
    MyLifeEventsCache eventsCache,
    MyLifeEventProcessor eventProcessor,
    MyLifeSessionStore sessionStore,
    MyLifeSyncService syncService,
    IConnectorPublisher? publisher = null
) : BaseConnectorService<MyLifeConnectorConfiguration>(httpClient, logger, publisher)
{
    private readonly MyLifeConnectorConfiguration _config = config.Value;

    public override string ServiceName => "MyLife";
    protected override string ConnectorSource => DataSources.MyLifeConnector;

    public override List<SyncDataType> SupportedDataTypes =>
    [
        SyncDataType.Glucose,
        SyncDataType.ManualBG,
        SyncDataType.Boluses,
        SyncDataType.CarbIntake,
        SyncDataType.BolusCalculations,
        SyncDataType.Notes,
        SyncDataType.DeviceEvents,
        SyncDataType.StateSpans,
        SyncDataType.Profiles
    ];

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
        return eventProcessor.MapSensorGlucose(filtered);
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
            _config.EnableMealCarbConsolidation,
            _config.EnableTempBasalConsolidation,
            _config.TempBasalConsolidationWindowMinutes
        );
    }

    /// <summary>
    /// Fetches TempBasal records from MyLife basal delivery events.
    /// </summary>
    public async Task<IEnumerable<TempBasal>> FetchTempBasalsAsync(DateTime? from, DateTime? to)
    {
        var actualSince = await CalculateTreatmentSinceTimestampAsync(_config, from);
        var actualUntil = to ?? DateTime.UtcNow;
        var events = await eventsCache.GetEventsAsync(
            actualSince,
            actualUntil,
            CancellationToken.None
        );

        var filtered = FilterEventsBySince(events, actualSince);
        return MyLifeStateSpanMapper.MapTempBasals(
            filtered,
            _config.EnableTempBasalConsolidation,
            _config.TempBasalConsolidationWindowMinutes
        );
    }

    /// <summary>
    /// Fetches pump settings from MyLife and maps them to Profile records.
    /// </summary>
    public async Task<IEnumerable<Profile>> FetchPumpSettingsProfileAsync(
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sessionStore.ServiceUrl)
            || string.IsNullOrWhiteSpace(sessionStore.AuthToken)
            || string.IsNullOrWhiteSpace(sessionStore.PatientId))
        {
            return [];
        }

        var readouts = await syncService.FetchPumpSettingsAsync(
            sessionStore.ServiceUrl,
            sessionStore.AuthToken,
            sessionStore.PatientId,
            cancellationToken
        );

        return MyLifePumpSettingsMapper.MapToProfiles(readouts);
    }

    /// <summary>
    /// Performs sync, publishing all granular model types directly.
    /// </summary>
    protected override async Task<SyncResult> PerformSyncInternalAsync(
        SyncRequest request,
        MyLifeConnectorConfiguration config,
        CancellationToken cancellationToken,
        ISyncProgressReporter? progressReporter = null
    )
    {
        var result = new SyncResult { StartTime = DateTimeOffset.UtcNow, Success = true };

        if (!request.DataTypes.Any())
            request.DataTypes = SupportedDataTypes;

        var enabledTypes = config.GetEnabledDataTypes(SupportedDataTypes);
        var activeTypes = request.DataTypes.Where(t => enabledTypes.Contains(t)).ToHashSet();

        try
        {
            // Sync glucose data as SensorGlucose
            if (activeTypes.Contains(SyncDataType.Glucose))
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

            // Determine if any treatment sub-type is active
            var treatmentSubTypes = new[]
            {
                SyncDataType.ManualBG,
                SyncDataType.Boluses,
                SyncDataType.CarbIntake,
                SyncDataType.BolusCalculations,
                SyncDataType.Notes,
                SyncDataType.DeviceEvents
            };
            var needRecords = treatmentSubTypes.Any(t => activeTypes.Contains(t));

            if (needRecords)
            {
                var records = await FetchRecordsAsync(request.From, request.To);

                // Persist decomposition batches before V4 records (FK constraint)
                if (records.DecompositionBatches.Count > 0)
                {
                    await PublishDecompositionBatchesAsync(
                        records.DecompositionBatches,
                        config,
                        cancellationToken
                    );
                }

                // Publish Boluses
                if (activeTypes.Contains(SyncDataType.Boluses) && records.Boluses.Count > 0)
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
                        result.ItemsSynced[SyncDataType.Boluses] = records.Boluses.Count;
                    }
                    else
                    {
                        result.Success = false;
                        result.Errors.Add("Bolus publish failed");
                    }
                }

                // Publish CarbIntakes
                if (activeTypes.Contains(SyncDataType.CarbIntake) && records.CarbIntakes.Count > 0)
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
                        result.ItemsSynced[SyncDataType.CarbIntake] = records.CarbIntakes.Count;
                    }
                    else
                    {
                        result.Success = false;
                        result.Errors.Add("CarbIntake publish failed");
                    }
                }

                // Publish BGChecks
                if (activeTypes.Contains(SyncDataType.ManualBG) && records.BGChecks.Count > 0)
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
                        result.ItemsSynced[SyncDataType.ManualBG] = records.BGChecks.Count;
                    }
                    else
                    {
                        result.Success = false;
                        result.Errors.Add("BGCheck publish failed");
                    }
                }

                // Publish BolusCalculations
                if (activeTypes.Contains(SyncDataType.BolusCalculations) && records.BolusCalculations.Count > 0)
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
                        result.ItemsSynced[SyncDataType.BolusCalculations] = records.BolusCalculations.Count;
                    }
                    else
                    {
                        result.Success = false;
                        result.Errors.Add("BolusCalculation publish failed");
                    }
                }

                // Publish Notes
                if (activeTypes.Contains(SyncDataType.Notes) && records.Notes.Count > 0)
                {
                    var success = await PublishNoteDataAsync(
                        records.Notes,
                        config,
                        cancellationToken
                    );
                    if (success)
                    {
                        _logger.LogInformation("Synced {Count} Note records", records.Notes.Count);
                        result.ItemsSynced[SyncDataType.Notes] = records.Notes.Count;
                    }
                    else
                    {
                        result.Success = false;
                        result.Errors.Add("Note publish failed");
                    }
                }

                // Publish DeviceEvents
                if (activeTypes.Contains(SyncDataType.DeviceEvents) && records.DeviceEvents.Count > 0)
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
                        result.ItemsSynced[SyncDataType.DeviceEvents] = records.DeviceEvents.Count;
                    }
                    else
                    {
                        result.Success = false;
                        result.Errors.Add("DeviceEvent publish failed");
                    }
                }
            }

            // Publish TempBasal records for basal delivery
            if (activeTypes.Contains(SyncDataType.StateSpans))
            {
                var tempBasals = await FetchTempBasalsAsync(request.From, request.To);
                var tempBasalList = tempBasals.ToList();

                if (tempBasalList.Count > 0)
                {
                    var success = await PublishTempBasalDataAsync(
                        tempBasalList,
                        config,
                        cancellationToken
                    );
                    if (success)
                    {
                        _logger.LogInformation(
                            "Synced {Count} TempBasal records",
                            tempBasalList.Count
                        );
                        result.ItemsSynced[SyncDataType.StateSpans] = tempBasalList.Count;
                    }
                    else
                    {
                        result.Success = false;
                        result.Errors.Add("TempBasal publish failed");
                    }
                }
            }

            // Publish Profile records from pump settings
            if (activeTypes.Contains(SyncDataType.Profiles))
            {
                var profiles = await FetchPumpSettingsProfileAsync(cancellationToken);
                var profileList = profiles.ToList();

                if (profileList.Count > 0)
                {
                    var success = await PublishProfileDataAsync(
                        profileList,
                        config,
                        cancellationToken
                    );
                    if (success)
                    {
                        _logger.LogInformation(
                            "Synced {Count} Profile records from pump settings",
                            profileList.Count
                        );
                        result.ItemsSynced[SyncDataType.Profiles] = profileList.Count;
                    }
                    else
                    {
                        result.Success = false;
                        result.Errors.Add("Profile publish failed");
                    }
                }
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
