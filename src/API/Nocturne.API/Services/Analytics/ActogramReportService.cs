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
/// heart rates) sequentially: every dependency resolves through the same
/// per-request scoped <c>NocturneDbContext</c>, and EF Core's
/// <c>ConcurrencyDetector</c> rejects parallel operations on a single
/// context with <c>InvalidOperationException</c>. Threshold resolution
/// mirrors <c>ProfileLoadStage</c>: very-low/very-high are fixed, low/high
/// come from the active profile at the requested end time, falling back to
/// the consensus in-range band when no therapy settings exist yet.
/// </remarks>
public sealed class ActogramReportService : IActogramReportService
{
    // Match ProfileLoadStage so the actogram and dashboard agree on band edges.
    private const double DefaultVeryLow = 54;
    private const double DefaultVeryHigh = 250;
    private const double DefaultLow = GlucoseConstants.TargetBottomMgdl;
    private const double DefaultHigh = GlucoseConstants.TargetTopMgdl;

    // Sleep spans are sparse (≤ a few per day). Cap is generous but bounded.
    private const int SleepSpanLimit = 10000;

    private readonly ISensorGlucoseRepository _sensorGlucoseRepository;
    private readonly ISleepService _sleepService;
    private readonly IStepCountService _stepCountService;
    private readonly IHeartRateService _heartRateService;
    private readonly ITherapySettingsResolver _therapySettingsResolver;
    private readonly ITargetRangeResolver _targetRangeResolver;
    private readonly ILogger<ActogramReportService> _logger;

    public ActogramReportService(
        ISensorGlucoseRepository sensorGlucoseRepository,
        ISleepService sleepService,
        IStepCountService stepCountService,
        IHeartRateService heartRateService,
        ITherapySettingsResolver therapySettingsResolver,
        ITargetRangeResolver targetRangeResolver,
        ILogger<ActogramReportService> logger
    )
    {
        _sensorGlucoseRepository = sensorGlucoseRepository;
        _sleepService = sleepService;
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

        // Sequential awaits: every query below resolves through the same
        // per-request scoped NocturneDbContext, and EF Core's ConcurrencyDetector
        // rejects overlapping operations on a single context. Npgsql also
        // serializes commands per connection, so Task.WhenAll buys no real
        // throughput here even when it doesn't crash.
        var glucoseRecords = await _sensorGlucoseRepository.GetAsync(
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

        var stepRecords = await _stepCountService.GetStepCountsByDateRangeAsync(
            fromDt,
            toDt,
            cancellationToken: cancellationToken
        );

        var heartRateRecords = await _heartRateService.GetHeartRatesByDateRangeAsync(
            fromDt,
            toDt,
            cancellationToken: cancellationToken
        );

        var thresholdsRaw = await BuildThresholdsAsync(endTime, cancellationToken);

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
                        StartMills = new DateTimeOffset(stage.StartTime, TimeSpan.Zero).ToUnixTimeMilliseconds(),
                        EndMills = new DateTimeOffset(stage.EndTime, TimeSpan.Zero).ToUnixTimeMilliseconds(),
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
