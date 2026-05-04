using Nocturne.Core.Contracts.Analytics;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Health;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;

namespace Nocturne.API.Services.Analytics;

/// <inheritdoc cref="IActogramReportService"/>
/// <remarks>
/// Fans the four required queries (glucose, sleep state spans, step counts,
/// heart rates) out in parallel against per-request scoped resolvers. Threshold
/// resolution mirrors <c>ProfileLoadStage</c>: very-low/very-high are fixed,
/// low/high come from the active profile at the requested end time, with
/// 70/180 fallbacks when no therapy settings exist yet.
/// </remarks>
public sealed class ActogramReportService : IActogramReportService
{
    // Match ProfileLoadStage so the actogram and dashboard agree on band edges.
    private const double DefaultVeryLow = 54;
    private const double DefaultVeryHigh = 250;
    private const double DefaultLow = 70;
    private const double DefaultHigh = 180;

    // Sleep spans are sparse (≤ a few per day). Cap is generous but bounded.
    private const int SleepSpanLimit = 10000;

    private readonly ISensorGlucoseRepository _sensorGlucoseRepository;
    private readonly IStateSpanService _stateSpanService;
    private readonly IStepCountService _stepCountService;
    private readonly IHeartRateService _heartRateService;
    private readonly ITherapySettingsResolver _therapySettingsResolver;
    private readonly ITargetRangeResolver _targetRangeResolver;
    private readonly ILogger<ActogramReportService> _logger;

    public ActogramReportService(
        ISensorGlucoseRepository sensorGlucoseRepository,
        IStateSpanService stateSpanService,
        IStepCountService stepCountService,
        IHeartRateService heartRateService,
        ITherapySettingsResolver therapySettingsResolver,
        ITargetRangeResolver targetRangeResolver,
        ILogger<ActogramReportService> logger
    )
    {
        _sensorGlucoseRepository = sensorGlucoseRepository;
        _stateSpanService = stateSpanService;
        _stepCountService = stepCountService;
        _heartRateService = heartRateService;
        _therapySettingsResolver = therapySettingsResolver;
        _targetRangeResolver = targetRangeResolver;
        _logger = logger;
    }

    public async Task<ActogramReportData> GetAsync(
        long startTime,
        long endTime,
        CancellationToken cancellationToken = default
    )
    {
        var fromDt = DateTimeOffset.FromUnixTimeMilliseconds(startTime).UtcDateTime;
        var toDt = DateTimeOffset.FromUnixTimeMilliseconds(endTime).UtcDateTime;

        // Match DataFetchStage's CGM cap: 12 readings/hour × 1.5 safety margin, floor 500.
        var rangeHours = Math.Max(1, (endTime - startTime) / (60.0 * 60 * 1000));
        var glucoseLimit = (int)Math.Max(500, Math.Ceiling(rangeHours * 12 * 1.5));

        var glucoseTask = _sensorGlucoseRepository.GetAsync(
            from: fromDt,
            to: toDt,
            device: null,
            source: null,
            limit: glucoseLimit,
            offset: 0,
            descending: false,
            ct: cancellationToken
        );

        var sleepTask = _stateSpanService.GetStateSpansAsync(
            category: StateSpanCategory.Sleep,
            from: fromDt,
            to: toDt,
            count: SleepSpanLimit,
            descending: false,
            cancellationToken: cancellationToken
        );

        var stepsTask = _stepCountService.GetStepCountsByDateRangeAsync(
            fromDt,
            toDt,
            cancellationToken
        );

        var heartRatesTask = _heartRateService.GetHeartRatesByDateRangeAsync(
            fromDt,
            toDt,
            cancellationToken
        );

        var thresholdsTask = BuildThresholdsAsync(endTime, cancellationToken);

        await Task.WhenAll(glucoseTask, sleepTask, stepsTask, heartRatesTask, thresholdsTask);

        var (glucoseData, glucoseYMax) = ChartDataService.BuildGlucoseData(
            (await glucoseTask).ToList()
        );

        var thresholds = (await thresholdsTask) with { GlucoseYMax = glucoseYMax };

        var heartRates = (await heartRatesTask)
            .Select(h => new HeartRatePointDto
            {
                Time = h.Mills,
                Bpm = h.Bpm,
            })
            .ToList();

        var stepCounts = (await stepsTask)
            .Select(s => new StepBubbleDto
            {
                Time = s.Mills,
                Steps = s.Metric,
            })
            .ToList();

        var sleepSpans = (await sleepTask)
            .Select(s => new ActogramSleepSpan
            {
                StartMills = s.StartMills,
                EndMills = s.EndMills ?? s.StartMills,
                State = s.State ?? string.Empty,
            })
            .ToList();

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
            SleepSpans = sleepSpans,
        };
    }

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
            Low = await _targetRangeResolver.GetLowBGTargetAsync(atMills, ct: ct),
            High = await _targetRangeResolver.GetHighBGTargetAsync(atMills, ct: ct),
            VeryHigh = DefaultVeryHigh,
        };
    }
}
