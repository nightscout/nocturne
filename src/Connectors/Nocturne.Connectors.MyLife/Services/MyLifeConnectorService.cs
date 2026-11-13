using System.Net;
using Microsoft.Extensions.Logging;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.MyLife.Configurations;
using Nocturne.Connectors.MyLife.Mappers;
using Nocturne.Connectors.MyLife.Mappers.Constants;
using Nocturne.Connectors.MyLife.Models;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.Connectors.MyLife.Services;

/// <summary>
/// MyLife connector service. Publishes SensorGlucose, Bolus, CarbIntake, BGCheck, Note,
/// DeviceEvent and TempBasal records.
/// </summary>
public class MyLifeConnectorService(
    HttpClient httpClient,
    IConnectorServerResolver<MyLifeConnectorConfiguration> serverResolver,
    ILogger<MyLifeConnectorService> logger,
    MyLifeAuthTokenProvider tokenProvider,
    MyLifeEventProcessor eventProcessor,
    IMyLifeSessionCache sessionCache,
    ITenantAccessor tenantAccessor,
    MyLifeSyncService syncService,
    IConnectorPublisher? publisher = null,
    TimeProvider? timeProvider = null
) : BaseConnectorService<MyLifeConnectorConfiguration>(httpClient, serverResolver, logger, publisher, timeProvider)
{

    public override string ServiceName => "MyLife";
    protected override string ConnectorSource => DataSources.MyLifeConnector;

    /// <summary>
    /// Fetches pump settings readouts from MyLife. Returns an empty list when no valid session
    /// is established.
    /// </summary>
    private async Task<IReadOnlyList<MyLifePumpSettingsReadout>> FetchPumpSettingsReadoutsAsync(
        CancellationToken cancellationToken)
    {
        var session = sessionCache.Get(tenantAccessor.TenantId);
        if (session == null
            || string.IsNullOrWhiteSpace(session.ServiceUrl)
            || string.IsNullOrWhiteSpace(session.AuthToken)
            || string.IsNullOrWhiteSpace(session.PatientId))
        {
            return [];
        }

        return await syncService.FetchPumpSettingsAsync(
            session.ServiceUrl,
            session.AuthToken,
            session.PatientId,
            cancellationToken
        );
    }

    /// <summary>
    /// Fetches pump settings from MyLife and maps them to Profile records.
    /// </summary>
    public async Task<IEnumerable<Profile>> FetchPumpSettingsProfileAsync(
        CancellationToken cancellationToken)
    {
        var readouts = await FetchPumpSettingsReadoutsAsync(cancellationToken);
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
        CancellationToken cancellationToken)
    {
        var result = new SyncResult { Success = true };

        var activeTypes = ResolveActiveTypes(request, config);

        try
        {
            // Establish the MyLife session up front. AcquireTokenAsync (reached via the token
            // provider) performs the SOAP login and populates the session cache as a side effect;
            // the token is cached so subsequent cycles reuse it. Without this call the session cache
            // is never populated and every sync fails with "session not established".
            var token = await tokenProvider.GetValidTokenAsync(config, cancellationToken);

            var session = sessionCache.Get(tenantAccessor.TenantId);
            if (string.IsNullOrEmpty(token)
                || session == null
                || string.IsNullOrWhiteSpace(session.ServiceUrl)
                || string.IsNullOrWhiteSpace(session.AuthToken)
                || string.IsNullOrWhiteSpace(session.PatientId))
            {
                result.Success = false;
                result.Errors.Add("MyLife authentication failed; see connector logs for the failing step");
                _logger.LogWarning(
                    "[{ConnectorSource}] Sync failed: MyLife authentication unsuccessful",
                    ConnectorSource);
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

            // MyLife streams the source month by month, so every bound below has to be concrete.
            // The initial-sync floor is the fallback, and as far back as this connector reaches for
            // a range naming no lower bound; whether the source itself holds more is unverified.
            var floor = InitialSyncFloor ?? DefaultInitialSyncFloor();
            var glucoseSince = ResumeFrom(
                request, await CalculateSinceTimestampAsync(config) ?? floor, floor);
            var treatmentSince = ResumeFrom(
                request, await CalculateTreatmentSinceTimestampAsync(config) ?? floor, floor);

            var overallSince = glucoseSince < treatmentSince ? glucoseSince : treatmentSince;
            var now = Clock.GetUtcNow().UtcDateTime;
            var until = request.To ?? now;

            // Overlap window for cross-month consolidation context
            var overlapMs = Math.Max(
                (long)MyLifeTimeConstants.CarbSuppressionWindowMs,
                (long)config.TempBasalConsolidationWindowMinutes * 60_000);

            var previousTail = new List<MyLifeEvent>();
            var glucoseSinceTicks = glucoseSince.ToMills() * 10_000;
            var treatmentSinceTicks = treatmentSince.ToMills() * 10_000;

            // Stream month by month
            await foreach (var batch in syncService.FetchEventsPerMonthAsync(
                session.ServiceUrl,
                session.AuthToken,
                session.PatientId,
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
                        .MapSensorGlucose(batch.Events.Where(e => e.EventDateTime >= glucoseSinceTicks), now)
                        .ToList();

                    await PublishRecordTypeAsync(result, activeTypes,
                        sgList, config, cancellationToken, batch.Month);
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
                        config.TempBasalConsolidationWindowMinutes,
                        now);

                    // Treatment records
                    if (needRecords)
                    {
                        var records = eventProcessor.MapRecords(treatmentEvents, treatmentContext);

                        var monthCtx = batch.Month;
                        await PublishRecordTypeAsync(result, activeTypes,
                            records.Boluses, config, cancellationToken, monthCtx);
                        await PublishRecordTypeAsync(result, activeTypes,
                            records.CarbIntakes, config, cancellationToken, monthCtx);
                        await PublishRecordTypeAsync(result, activeTypes,
                            records.BGChecks, config, cancellationToken, monthCtx);
                        await PublishRecordTypeAsync(result, activeTypes,
                            records.BolusCalculations, config, cancellationToken, monthCtx);
                        await PublishRecordTypeAsync(result, activeTypes,
                            records.Notes, config, cancellationToken, monthCtx);
                        await PublishRecordTypeAsync(result, activeTypes,
                            records.DeviceEvents, config, cancellationToken, monthCtx);
                    }

                    // TempBasal state spans
                    if (needStateSpans)
                    {
                        var tempBasals = MyLifeStateSpanMapper.MapTempBasals(treatmentEvents, treatmentContext).ToList();

                        await PublishRecordTypeAsync(result, activeTypes,
                            tempBasals, config, cancellationToken, batch.Month, dataType: SyncDataType.StateSpans);
                    }
                }

                // Update overlap tail for next month's context
                UpdatePreviousTail(previousTail, batch.Events, overlapMs);
            }

            // Publish Profile records and profile activations from pump settings
            // (one SOAP call, two derived data shapes).
            if (activeTypes.Contains(SyncDataType.Profiles))
            {
                var readouts = await FetchPumpSettingsReadoutsAsync(cancellationToken);

                var profiles = MyLifePumpSettingsMapper.MapToProfiles(readouts);
                await PublishRecordTypeAsync(result, activeTypes,
                    profiles, config, cancellationToken);

                var activations = MyLifePumpSettingsMapper.MapToProfileActivations(readouts, ConnectorSource);
                await PublishRecordTypeAsync(result, activeTypes,
                    activations, config, cancellationToken, "from pump settings");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // A token MyLife has already rejected would otherwise stay cached until its nominal
            // 24-hour expiry, failing every sync in between.
            if (ex is HttpRequestException { StatusCode: HttpStatusCode.Unauthorized })
                tokenProvider.InvalidateToken();

            RecordSyncError(result, ex);
        }

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
