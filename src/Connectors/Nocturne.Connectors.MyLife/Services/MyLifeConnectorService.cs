using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.MyLife.Configurations;
using Nocturne.Connectors.MyLife.Mappers;
using Nocturne.Connectors.MyLife.Mappers.Constants;
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
    /// Returns empty - use PerformSyncInternalAsync for glucose data.
    /// </summary>
    public override Task<IEnumerable<Entry>> FetchGlucoseDataAsync(DateTime? since = null)
    {
        return Task.FromResult(Enumerable.Empty<Entry>());
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
    /// Performs sync by streaming one calendar month at a time, mapping and publishing
    /// each batch before moving on. A configurable overlap tail preserves cross-month
    /// carb-bolus and temp-basal consolidation context.
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
            // Validate session
            if (string.IsNullOrWhiteSpace(sessionStore.ServiceUrl)
                || string.IsNullOrWhiteSpace(sessionStore.AuthToken)
                || string.IsNullOrWhiteSpace(sessionStore.PatientId))
            {
                result.Success = false;
                result.Errors.Add("MyLife session not established");
                result.EndTime = DateTimeOffset.UtcNow;
                return result;
            }

            // Determine which categories are needed
            var needGlucose = activeTypes.Contains(SyncDataType.Glucose);
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
            var needStateSpans = activeTypes.Contains(SyncDataType.StateSpans);

            // Calculate since timestamps
            var glucoseSince = await CalculateSinceTimestampAsync(config, request.From);
            var treatmentSince = await CalculateTreatmentSinceTimestampAsync(config, request.From);
            var overallSince = glucoseSince < treatmentSince ? glucoseSince : treatmentSince;
            var until = request.To ?? DateTime.UtcNow;

            // Overlap window for cross-month consolidation context
            var overlapMs = Math.Max(
                (long)MyLifeTimeConstants.CarbSuppressionWindowMs,
                (long)config.TempBasalConsolidationWindowMinutes * 60_000);

            var previousTail = new List<MyLifeEvent>();
            var glucoseSinceTicks = new DateTimeOffset(glucoseSince).ToUnixTimeMilliseconds() * 10_000;
            var treatmentSinceTicks = new DateTimeOffset(treatmentSince).ToUnixTimeMilliseconds() * 10_000;

            // Stream month by month
            await foreach (var batch in syncService.FetchEventsPerMonthAsync(
                sessionStore.ServiceUrl,
                sessionStore.AuthToken,
                sessionStore.PatientId,
                overallSince,
                until,
                cancellationToken))
            {
                // Build context from overlap tail + current month for cross-month consolidation
                var contextEvents = previousTail.Count > 0
                    ? previousTail.Concat(batch.Events).ToList()
                    : batch.Events;

                // SensorGlucose — filter by glucose since, publish inline (needs stamping)
                if (needGlucose)
                {
                    var sgList = eventProcessor
                        .MapSensorGlucose(batch.Events.Where(e => e.EventDateTime >= glucoseSinceTicks))
                        .ToList();

                    if (sgList.Count > 0)
                    {
                        var success = await PublishSensorGlucoseDataAsync(sgList, config, cancellationToken);
                        result.ItemsSynced.TryGetValue(SyncDataType.Glucose, out var prevCount);
                        result.ItemsSynced[SyncDataType.Glucose] = prevCount + sgList.Count;

                        if (!success)
                        {
                            result.Success = false;
                            result.Errors.Add("SensorGlucose publish failed");
                        }
                        else
                        {
                            var maxMills = sgList.Max(s => s.Mills);
                            var maxTime = DateTimeOffset.FromUnixTimeMilliseconds(maxMills).UtcDateTime;
                            if (!result.LastEntryTimes.TryGetValue(SyncDataType.Glucose, out var existing) || maxTime > existing)
                                result.LastEntryTimes[SyncDataType.Glucose] = maxTime;

                            _logger.LogInformation(
                                "Synced {Count} SensorGlucose records from {Month}",
                                sgList.Count, batch.Month);
                        }
                    }
                }

                // Shared treatment filtering and context for records + state spans
                if (needRecords || needStateSpans)
                {
                    var treatmentEvents = batch.Events
                        .Where(e => e.EventDateTime >= treatmentSinceTicks)
                        .ToList();

                    var treatmentContext = MyLifeContext.Create(
                        contextEvents,
                        config.EnableMealCarbConsolidation,
                        config.EnableTempBasalConsolidation,
                        config.TempBasalConsolidationWindowMinutes);

                    // Treatment records
                    if (needRecords)
                    {
                        var records = eventProcessor.MapRecords(treatmentEvents, treatmentContext);

                        // Persist decomposition batches before V4 records (FK constraint)
                        if (records.DecompositionBatches.Count > 0)
                        {
                            await PublishDecompositionBatchesAsync(
                                records.DecompositionBatches, config, cancellationToken);
                        }

                        var monthCtx = batch.Month;
                        await PublishRecordTypeAsync(result, SyncDataType.Boluses, activeTypes,
                            records.Boluses, PublishBolusDataAsync, config, cancellationToken, monthCtx);
                        await PublishRecordTypeAsync(result, SyncDataType.CarbIntake, activeTypes,
                            records.CarbIntakes, PublishCarbIntakeDataAsync, config, cancellationToken, monthCtx);
                        await PublishRecordTypeAsync(result, SyncDataType.ManualBG, activeTypes,
                            records.BGChecks, PublishBGCheckDataAsync, config, cancellationToken, monthCtx);
                        await PublishRecordTypeAsync(result, SyncDataType.BolusCalculations, activeTypes,
                            records.BolusCalculations, PublishBolusCalculationDataAsync, config, cancellationToken, monthCtx);
                        await PublishRecordTypeAsync(result, SyncDataType.Notes, activeTypes,
                            records.Notes, PublishNoteDataAsync, config, cancellationToken, monthCtx);
                        await PublishRecordTypeAsync(result, SyncDataType.DeviceEvents, activeTypes,
                            records.DeviceEvents, PublishDeviceEventDataAsync, config, cancellationToken, monthCtx);
                    }

                    // TempBasal state spans
                    if (needStateSpans)
                    {
                        var tempBasals = MyLifeStateSpanMapper.MapTempBasals(treatmentEvents, treatmentContext).ToList();

                        await PublishRecordTypeAsync(result, SyncDataType.StateSpans, activeTypes,
                            tempBasals, PublishTempBasalDataAsync, config, cancellationToken, batch.Month);
                    }
                }

                // Update overlap tail for next month's context
                UpdatePreviousTail(previousTail, batch.Events, overlapMs);
            }

            // Publish Profile records from pump settings (separate SOAP call)
            if (activeTypes.Contains(SyncDataType.Profiles))
            {
                var profiles = (await FetchPumpSettingsProfileAsync(cancellationToken)).ToList();
                await PublishRecordTypeAsync(result, SyncDataType.Profiles, activeTypes,
                    profiles, PublishProfileDataAsync, config, cancellationToken);
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

    private static void UpdatePreviousTail(
        List<MyLifeEvent> previousTail,
        IReadOnlyList<MyLifeEvent> monthEvents,
        long overlapMs)
    {
        previousTail.Clear();
        if (monthEvents.Count == 0) return;

        var maxTicks = monthEvents.Max(e => e.EventDateTime);
        var overlapTicks = (long)overlapMs * 10_000;
        var cutoff = maxTicks - overlapTicks;
        previousTail.AddRange(monthEvents.Where(e => e.EventDateTime >= cutoff));
    }
}
