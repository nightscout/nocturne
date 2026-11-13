using Microsoft.Extensions.Logging;
using Nocturne.Core.Clinical.Treatments;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Core.Contracts.Health;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Contracts.Sleep;
using Nocturne.Infrastructure.Data.Abstractions;

namespace Nocturne.API.Services.ChartData.Stages;

/// <summary>
/// Chart data pipeline stage that fetches all raw data required for the dashboard chart.
/// Repositories that open their own context per call through <c>INocturneDbContextFactory</c> run
/// concurrently; the ones that share the request-scoped
/// <see cref="Microsoft.EntityFrameworkCore.DbContext"/>, which is not thread-safe, run one after
/// another alongside them.
/// </summary>
/// <remarks>
/// <para>
/// Dynamic query limits are derived from the requested time range to avoid over-fetching on
/// wide windows while still guaranteeing coverage on narrow ones. The baseline is 12 CGM
/// readings per hour (5-minute intervals) with a 50% safety margin.
/// </para>
/// <para>
/// The reads the IOB/COB series feeds on take their bounds from <see cref="ChartDataContext.SeriesWindow"/>
/// (<see cref="IobCobSeriesWindow"/>). <see cref="ChartDataContext.DisplayBoluses"/> and
/// <see cref="ChartDataContext.DisplayCarbIntakes"/> are subsets trimmed to the display window.
/// Temp basals are read under the same cap as
/// <see cref="Nocturne.API.Services.Analytics.ChartDataService"/> because the ascending read truncates the newest
/// rows. Consumers sort for themselves. <see cref="ChartDataContext.DisplayTempBasals"/> is the
/// delivered pieces of the whole fetch that overlap the display window, chosen by
/// <see cref="Nocturne.API.Services.Analytics.ChartDataService.SelectDisplayTempBasals"/>.
/// </para>
/// <para>
/// All <see cref="StateSpanCategory"/> variants are fetched in one call to
/// <c>IStateSpanRepository.GetByCategories</c>, which runs one query for the window and one per
/// category for spans that started before it.
/// </para>
/// </remarks>
/// <seealso cref="IChartDataStage"/>
/// <seealso cref="ChartDataContext"/>
internal sealed class DataFetchStage(
    ISensorGlucoseRepository sensorGlucoseRepository,
    IBolusRepository bolusRepository,
    ICarbIntakeRepository carbIntakeRepository,
    IBGCheckRepository bgCheckRepository,
    IDeviceEventRepository deviceEventRepository,
    ITempBasalRepository tempBasalRepository,
    IApsSnapshotRepository apsSnapshotRepository,
    IPumpSnapshotRepository pumpSnapshotRepository,
    IStateSpanRepository stateSpanRepository,
    IProfileActivationResolver profileActivationResolver,
    ISystemEventRepository systemEventRepository,
    ITrackerRepository trackerRepository,
    IBasalInjectionRepository basalInjectionRepository,
    ILogger<DataFetchStage> logger,
    IHeartRateService heartRateService,
    IStepCountService stepCountService,
    ISleepService sleepService,
    TimeProvider timeProvider
) : IChartDataStage
{
    public async Task<ChartDataContext> ExecuteAsync(ChartDataContext context, CancellationToken cancellationToken)
    {
        var startTime = context.StartTime;
        var endTime = context.EndTime;
        var window = context.SeriesWindow;

        // Helper to convert mills to DateTime for V4 repository calls
        static DateTime? MillsToDateTime(long mills) => DateTimeOffset.FromUnixTimeMilliseconds(mills).UtcDateTime;

        // Calculate reasonable limits based on the actual time range
        var rangeHours = (endTime - startTime) / (60.0 * 60 * 1000);
        // The canonical read returns one stream per bucket: twice a 1-minute stream's 60 readings an
        // hour leaves room for a denser uploader without removing the limit entirely.
        var entryLimit = (int)Math.Max(500, Math.Ceiling(rangeHours * 2 * 60));
        // Treatments are less frequent but are read from the series' insulin look-back.
        var treatmentRangeHours = (window.ReadEnd - window.InsulinReadStart) / (60.0 * 60 * 1000);
        var treatmentLimit = (int)Math.Max(500, Math.Ceiling(treatmentRangeHours * 10));
        var displayRangeLimit = (int)Math.Max(500, Math.Ceiling(rangeHours * 10));

        // Fetch glucose data from v4 SensorGlucose table; the dashboard renders the canonical
        // stream, not blended concurrent CGMs.
        var sensorGlucoseTask = FetchCanonicalGlucoseAsync();
        async Task<List<SensorGlucose>> FetchCanonicalGlucoseAsync() =>
            (await sensorGlucoseRepository.GetForAnalyticsAsync(
                from: MillsToDateTime(startTime),
                to: MillsToDateTime(endTime),
                device: null,
                source: null,
                limit: entryLimit,
                offset: 0,
                descending: true,
                ct: cancellationToken,
                streams: GlucoseStreams.Canonical
            )).ToList();

        var bolusTask = bolusRepository.GetAsync(
            from: MillsToDateTime(window.InsulinReadStart),
            to: MillsToDateTime(window.ReadEnd),
            device: null,
            source: null,
            limit: treatmentLimit,
            offset: 0,
            descending: true,
            ct: cancellationToken
        );

        var carbIntakeTask = carbIntakeRepository.GetAsync(
            from: MillsToDateTime(window.InsulinReadStart),
            to: MillsToDateTime(window.ReadEnd),
            device: null,
            source: null,
            limit: treatmentLimit,
            offset: 0,
            descending: true,
            ct: cancellationToken
        );

        // Fetch BG checks from v4 BGCheck table (display range only)
        var bgCheckTask = bgCheckRepository.GetAsync(
            from: MillsToDateTime(startTime),
            to: MillsToDateTime(endTime),
            device: null,
            source: null,
            limit: treatmentLimit,
            offset: 0,
            descending: true,
            ct: cancellationToken
        );

        // Fetch device events from v4 DeviceEvent table (display range only)
        var deviceEventTask = deviceEventRepository.GetAsync(
            from: MillsToDateTime(startTime),
            to: MillsToDateTime(endTime),
            device: null,
            source: null,
            limit: displayRangeLimit,
            offset: 0,
            descending: true,
            ct: cancellationToken
        );

        // Fetch basal injections from v4 BasalInjection table (display range only)
        var basalInjectionTask = basalInjectionRepository.GetAsync(
            from: MillsToDateTime(startTime),
            to: MillsToDateTime(endTime),
            device: null,
            source: null,
            limit: displayRangeLimit,
            offset: 0,
            descending: true,
            ct: cancellationToken
        );

        var tempBasalTask = tempBasalRepository.GetAsync(
            from: MillsToDateTime(window.TempBasalReadStart),
            to: MillsToDateTime(window.ReadEnd),
            device: null,
            source: null,
            limit: Nocturne.API.Services.Analytics.ChartDataService.TempBasalQueryLimit,
            offset: 0,
            descending: false,
            ct: cancellationToken
        );

        // The slim projections are deliberate: full snapshots carry multi-KB JSON blob columns, and
        // a limit heuristic would truncate the newest rows for high-cadence uploaders.
        var deviceReadUntil = MillsToDateTime(window.DeviceReadEnd)!.Value;
        var apsSnapshotTask = apsSnapshotRepository.GetIobCobPointsAsync(
            from: MillsToDateTime(window.DeviceReadStart)!.Value,
            to: deviceReadUntil,
            ct: cancellationToken
        );
        var pumpIobTask = pumpSnapshotRepository.GetIobPointsAsync(
            from: MillsToDateTime(window.DeviceReadStart)!.Value,
            to: deviceReadUntil,
            ct: cancellationToken
        );

        var latestPumpSnapshotTask = pumpSnapshotRepository.GetLatestAsync(
            asOf: MillsToDateTime(endTime),
            ct: cancellationToken
        );

        var sleepSessionTask = sleepService.GetSessionsAsync(
            from: MillsToDateTime(startTime),
            to: MillsToDateTime(endTime),
            limit: displayRangeLimit,
            cancellationToken: cancellationToken
        );

        async Task<ChartDataContext> FetchScopedContextReadsAsync()
        {
            StateSpanCategory[] stateSpanCategories =
            [
                StateSpanCategory.PumpMode,
                StateSpanCategory.Override,
                .. StateSpanCategories.DrawnActivities,
            ];

            var allStateSpans = await stateSpanRepository.GetByCategories(
                stateSpanCategories,
                MillsToDateTime(startTime),
                MillsToDateTime(endTime),
                cancellationToken
            );

            var overrides = await ScheduledBasal.OverridesAsync(
                stateSpanRepository, window.TherapyStart, window.OverrideEnd, cancellationToken);

            var profileSegments = await profileActivationResolver.GetRangeAsync(startTime, endTime, cancellationToken);

            var systemEventsResult = await systemEventRepository.GetSystemEventsAsync(
                eventType: null,
                category: null,
                from: startTime,
                to: endTime,
                source: null,
                count: 500,
                skip: 0,
                cancellationToken: cancellationToken
            );

            var trackerDefs = await trackerRepository.GetAllDefinitionsAsync(cancellationToken);
            var trackerInstances = await trackerRepository.GetActiveInstancesAsync(cancellationToken);

            List<HeartRate> heartRateList = [];
            List<StepCount> stepCountList = [];
            if (context.IncludeHealthSeries)
            {
                heartRateList = (await heartRateService.GetHeartRatesByDateRangeAsync(
                    MillsToDateTime(startTime)!.Value,
                    MillsToDateTime(endTime)!.Value,
                    cancellationToken: cancellationToken
                )).ToList();

                stepCountList = (await stepCountService.GetStepCountsByDateRangeAsync(
                    MillsToDateTime(startTime)!.Value,
                    MillsToDateTime(endTime)!.Value,
                    cancellationToken: cancellationToken
                )).ToList();
            }

            var stateSpansReadOnly = allStateSpans
                .ToDictionary(
                    kvp => kvp.Key,
                    kvp => (IEnumerable<StateSpan>)kvp.Value
                );

            return context with
            {
                StateSpans = stateSpansReadOnly,
                Overrides = overrides,
                ProfileSegments = profileSegments,
                SystemEvents = systemEventsResult?.ToList() ?? [],
                TrackerDefinitions = trackerDefs?.ToList() ?? [],
                TrackerInstances = trackerInstances?.ToList() ?? [],
                HeartRateList = heartRateList,
                StepCountList = stepCountList,
            };
        }
        var scopedTask = FetchScopedContextReadsAsync();

        await Task.WhenAll(
            sensorGlucoseTask, bolusTask, carbIntakeTask, bgCheckTask, deviceEventTask,
            basalInjectionTask, tempBasalTask, apsSnapshotTask, pumpIobTask, latestPumpSnapshotTask, sleepSessionTask, scopedTask);

        var sensorGlucoseList = await sensorGlucoseTask;
        var bolusList = (await bolusTask).ToList();
        var carbIntakeList = (await carbIntakeTask).ToList();
        var bgCheckList = (await bgCheckTask).ToList();
        var deviceEventList = (await deviceEventTask).ToList();
        var basalInjectionList = (await basalInjectionTask).ToList();
        var tempBasalList = (await tempBasalTask).ToList();
        var apsSnapshotList = await apsSnapshotTask;
        var sleepSessionList = (await sleepSessionTask).ToList();
        var scopedReads = await scopedTask;
        var heartRateList = scopedReads.HeartRateList;
        var stepCountList = scopedReads.StepCountList;

        // Display-range subsets for markers
        var displayBoluses = bolusList
            .Where(b => b.Mills >= startTime && b.Mills <= endTime)
            .ToList();
        var displayCarbIntakes = carbIntakeList
            .Where(c => c.Mills >= startTime && c.Mills <= endTime)
            .ToList();
        var nowMills = timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var displayTempBasals = Nocturne.API.Services.Analytics.ChartDataService
            .SelectDisplayTempBasals(tempBasalList, startTime, endTime, nowMills);

        logger.LogDebug(
            "DataFetchStage: fetched {Glucose} glucose, {Bolus} bolus, {Carb} carb, {BgCheck} bg-check, {DeviceEvent} device-event, {TempBasal} temp-basal, {HeartRate} heart-rate, {StepCount} step-count, {Sleep} sleep records",
            sensorGlucoseList.Count,
            bolusList.Count,
            carbIntakeList.Count,
            bgCheckList.Count,
            deviceEventList.Count,
            tempBasalList.Count,
            heartRateList.Count,
            stepCountList.Count,
            sleepSessionList.Count
        );

        return scopedReads with
        {
            SensorGlucoseList = sensorGlucoseList,
            BolusList = bolusList,
            DisplayBoluses = displayBoluses,
            CarbIntakeList = carbIntakeList,
            DisplayCarbIntakes = displayCarbIntakes,
            BgCheckList = bgCheckList,
            DeviceEventList = deviceEventList,
            TempBasalList = tempBasalList,
            DisplayTempBasals = displayTempBasals,
            NowMills = nowMills,
            ApsSnapshotList = apsSnapshotList,
            PumpIobPointList = await pumpIobTask,
            LatestPumpSnapshot = await latestPumpSnapshotTask,
            BasalInjectionList = basalInjectionList,
            SleepSessions = sleepSessionList,
        };
    }
}
