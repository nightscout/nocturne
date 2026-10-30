using System.Globalization;
using Nocturne.Core.Clinical.LoopAnalysis;
using Nocturne.Core.Clinical.Statistics;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.Analytics;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Health;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.Sleep;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;

namespace Nocturne.API.Services.Analytics;

/// <inheritdoc cref="IActogramReportService"/>
/// <remarks>
/// Issues the required queries (glucose, sleep state spans, step counts,
/// heart rates) sequentially (<see cref="Nocturne.Infrastructure.Data.Services.INocturneDbContextFactory"/>).
/// Threshold resolution
/// mirrors <c>ProfileLoadStage</c>: the clinical low/high band is fixed, and the
/// active profile's personal target is carried separately in
/// <see cref="ChartThresholdsDto.TargetLow"/>/<see cref="ChartThresholdsDto.TargetHigh"/>
/// at the requested end time.
/// </remarks>
public sealed class ActogramReportService : IActogramReportService
{
    // Match ProfileLoadStage so the actogram and dashboard agree on band edges.
    private const double DefaultVeryLow = GlucoseConstants.VeryLowMgdl;
    private const double DefaultVeryHigh = GlucoseConstants.VeryHighMgdl;
    private const double DefaultLow = GlucoseConstants.TargetBottomMgdl;
    private const double DefaultHigh = GlucoseConstants.TargetTopMgdl;

    // Sleep spans are sparse (≤ a few per day). Cap is generous but bounded.
    private const int SleepSpanLimit = 10000;

    private const int HourMarkSpacing = 6;

    private readonly ISensorGlucoseRepository _sensorGlucoseRepository;
    private readonly ISleepService _sleepService;
    private readonly IStepCountService _stepCountService;
    private readonly IHeartRateService _heartRateService;
    private readonly ITherapySettingsResolver _therapySettingsResolver;
    private readonly ITargetRangeResolver _targetRangeResolver;
    private readonly TenantClockResolver _tenantClock;
    private readonly ILogger<ActogramReportService> _logger;

    public ActogramReportService(
        ISensorGlucoseRepository sensorGlucoseRepository,
        ISleepService sleepService,
        IStepCountService stepCountService,
        IHeartRateService heartRateService,
        ITherapySettingsResolver therapySettingsResolver,
        ITargetRangeResolver targetRangeResolver,
        TenantClockResolver tenantClock,
        ILogger<ActogramReportService> logger
    )
    {
        _sensorGlucoseRepository = sensorGlucoseRepository;
        _sleepService = sleepService;
        _stepCountService = stepCountService;
        _heartRateService = heartRateService;
        _therapySettingsResolver = therapySettingsResolver;
        _targetRangeResolver = targetRangeResolver;
        _tenantClock = tenantClock;
        _logger = logger;
    }

    public async Task<ActogramReportData> GetAsync(
        long startTime,
        long endTime,
        long? summaryStartTime = null,
        long? summaryEndTime = null,
        CancellationToken cancellationToken = default
    )
    {
        var summaryStart = summaryStartTime ?? startTime;
        var summaryEnd = summaryEndTime ?? endTime;

        var fromDt = DateTimeOffset.FromUnixTimeMilliseconds(startTime).UtcDateTime;
        var toDt = DateTimeOffset.FromUnixTimeMilliseconds(endTime).UtcDateTime;

        var glucoseRecords = await _sensorGlucoseRepository.GetForAnalyticsAsync(
            from: fromDt,
            to: toDt,
            device: null,
            source: null,
            limit: int.MaxValue,
            offset: 0,
            descending: false,
            ct: cancellationToken
        );

        // includeStages: the per-stage banding below is keyed off session.Stages,
        // which the list query only populates on request.
        var sleepSessions = await _sleepService.GetSessionsAsync(
            from: fromDt, to: toDt,
            limit: SleepSpanLimit, descending: false,
            includeStages: true,
            cancellationToken: cancellationToken);

        var stepRecords = (await _stepCountService.GetStepCountsByDateRangeAsync(
            fromDt,
            toDt,
            cancellationToken: cancellationToken
        )).Where(s => !s.IsPossibleRunningTotal()).ToList();

        var heartRateRecords = await _heartRateService.GetHeartRateMinuteAveragesByDateRangeAsync(
            fromDt,
            toDt,
            cancellationToken
        );

        var thresholdsRaw = await BuildThresholdsAsync(endTime, cancellationToken);

        var clock = await _tenantClock.ResolveAsync(cancellationToken);
        var zone = clock.Zone ?? TimeZoneInfo.Utc;
        var days = BuildDays(startTime, endTime, zone);

        var (glucoseData, glucoseYMax) = ChartDataService.BuildGlucoseData(
            glucoseRecords.ToList()
        );

        var thresholds = thresholdsRaw with { GlucoseYMax = glucoseYMax };

        var heartRates = heartRateRecords
            .Select(h => new HeartRatePointDto
            {
                Time = h.Mills,
                Bpm = h.Bpm,
            })
            .ToList();

        var stepCounts = stepRecords
            .Select(s => new StepBubbleDto
            {
                Time = s.Mills,
                Steps = s.Metric,
            })
            .ToList();

        var sleepSpans = sleepSessions
            .SelectMany(session =>
            {
                if (session.Stages != null && session.Stages.Count > 0)
                {
                    return session.Stages.Select(stage => new ActogramSleepSpan
                    {
                        StartMills = stage.StartTime.ToMills(),
                        EndMills = stage.EndTime.ToMills(),
                        State = stage.Stage.ToString().ToLowerInvariant(),
                    });
                }

                return
                [
                    new ActogramSleepSpan
                    {
                        StartMills = session.StartMills,
                        EndMills = session.EndMills,
                        State = "asleep",
                    }
                ];
            })
            .ToList();

        var rangeHours = (endTime - startTime) / 3_600_000.0;
        _logger.LogDebug(
            "Actogram report fetched {Glucose} glucose, {Sleep} sleep, {Steps} steps, {HeartRate} heart-rate records for {RangeHours:F1}h",
            glucoseData.Count,
            sleepSpans.Count,
            stepCounts.Count,
            heartRates.Count,
            rangeHours
        );

        return new ActogramReportData
        {
            Glucose = glucoseData,
            Thresholds = thresholds,
            HeartRates = heartRates,
            StepCounts = stepCounts,
            Days = days,
            StepDayTotals = SumStepsByDay(stepRecords, days, zone),
            TimeZoneUnavailableReason = clock.Reason,
            SleepSpans = sleepSpans,
            HeartRateSummary = SummariseHeartRates(heartRates, summaryStart, summaryEnd),
            StepSummary = SummariseSteps(stepCounts, summaryStart, summaryEnd),
        };
    }

    private static ActogramHeartRateSummary? SummariseHeartRates(
        IEnumerable<HeartRatePointDto> heartRates,
        long start,
        long end
    )
    {
        var bpms = heartRates
            .Where(h => h.Time >= start && h.Time < end)
            .Select(h => h.Bpm)
            .Order()
            .ToList();
        if (bpms.Count == 0)
            return null;

        return new ActogramHeartRateSummary
        {
            AverageBpm = (int)Math.Round(bpms.Average(), MidpointRounding.AwayFromZero),
            RestingBpm = bpms[bpms.Count / 10],
            MinBpm = bpms[0],
            MaxBpm = bpms[^1],
            ReadingCount = bpms.Count,
        };
    }

    private static ActogramStepSummary SummariseSteps(
        IEnumerable<StepBubbleDto> stepCounts,
        long start,
        long end
    )
    {
        var total = stepCounts.Where(s => s.Time >= start && s.Time < end).Sum(s => s.Steps);
        var dayCount = StatisticsService.WindowDays(
            DateTimeOffset.FromUnixTimeMilliseconds(start).UtcDateTime,
            DateTimeOffset.FromUnixTimeMilliseconds(end).UtcDateTime);

        return new ActogramStepSummary
        {
            TotalSteps = total,
            DayCount = dayCount,
            DailyAverage = (int)Math.Round((double)total / dayCount, MidpointRounding.AwayFromZero),
        };
    }

    private static Dictionary<string, int> SumStepsByDay(
        IEnumerable<StepCount> steps,
        IReadOnlyList<ActogramDay> days,
        TimeZoneInfo zone
    )
    {
        var totals = days.ToDictionary(day => day.Date, _ => 0, StringComparer.Ordinal);
        foreach (var step in steps)
        {
            var key = LocalSlotBinning.LocalDay(step.Mills, zone).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (totals.ContainsKey(key))
                totals[key] += step.Metric;
        }

        return totals;
    }

    private static List<ActogramDay> BuildDays(long startTime, long endTime, TimeZoneInfo zone)
    {
        var (firstDay, starts) = LocalSlotBinning.DayStarts(startTime, endTime, zone);
        return Enumerable.Range(0, starts.Length - 1)
            .Select(d => new ActogramDay
            {
                Date = firstDay.AddDays(d).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                StartMills = starts[d],
                EndMills = starts[d + 1],
                HourMarks = BuildHourMarks(firstDay.AddDays(d), zone),
            })
            .ToList();
    }

    private static List<long> BuildHourMarks(DateOnly day, TimeZoneInfo zone) =>
        Enumerable.Range(0, 2)
            .SelectMany(_ => Enumerable.Range(1, 3), (dayOffset, step) => (dayOffset, hour: step * HourMarkSpacing))
            .Select(mark => LocalSlotBinning.LocalInstantMills(day.AddDays(mark.dayOffset), mark.hour, zone))
            .ToList();

    private async Task<ChartThresholdsDto> BuildThresholdsAsync(long atMills, CancellationToken ct)
    {
        if (!await _therapySettingsResolver.HasDataAsync(ct))
        {
            return new ChartThresholdsDto
            {
                VeryLow = DefaultVeryLow,
                Low = DefaultLow,
                High = DefaultHigh,
                VeryHigh = DefaultVeryHigh,
            };
        }

        return new ChartThresholdsDto
        {
            VeryLow = DefaultVeryLow,
            Low = DefaultLow,
            High = DefaultHigh,
            VeryHigh = DefaultVeryHigh,
            TargetLow = await _targetRangeResolver.GetLowBGTargetAsync(atMills, ct: ct),
            TargetHigh = await _targetRangeResolver.GetHighBGTargetAsync(atMills, ct: ct),
        };
    }
}
