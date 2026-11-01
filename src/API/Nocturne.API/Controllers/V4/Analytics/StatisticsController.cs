using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Nocturne.Core.Clinical.Devices;
using Nocturne.API.Attributes;
using Nocturne.API.Authorization;
using Nocturne.API.Controllers.V4.Base;
using Nocturne.API.Extensions;
using Nocturne.API.Services.Analytics;
using Nocturne.Core.Clinical.LoopAnalysis;
using Nocturne.Core.Clinical.Statistics;
using Nocturne.API.Services.Auth;
using Nocturne.API.Services.Caching;
using Nocturne.API.Services.Identity;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.Analytics;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Profiles;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Clinical.Treatments;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Basal;
using Nocturne.Core.Models.V4;
using OpenApi.Remote.Attributes;

namespace Nocturne.API.Controllers.V4.Analytics;

/// <summary>
/// Controller for comprehensive glucose and treatment statistics.
/// Provides endpoints for calculating various glucose metrics and analytics including
/// time-in-range, glycemic variability, GMI, GRI, basal/bolus ratios, and AID system metrics.
/// </summary>
/// <remarks>
/// <c>GET /periods</c> caches for five minutes through <see cref="ITenantCache"/>;
/// <c>GET /range-analytics</c>, <c>GET /period-comparison</c>, <c>GET /weekday-averages</c> and
/// <c>GET /hourly-patterns</c> carry a 60-second client-only <see cref="ResponseCacheAttribute"/>. No other action caches.
///
/// Repositories create their own DbContext per call from <c>INocturneDbContextFactory</c>, so
/// independent reads within one request are issued together under <c>Task.WhenAll</c>.
/// </remarks>
/// <seealso cref="IStatisticsService"/>
/// <seealso cref="ISensorGlucoseRepository"/>
/// <seealso cref="ICarbIntakeRepository"/>
/// <seealso cref="AidSystemMetricsReader"/>
/// <seealso cref="RangeAnalyticsReader"/>
/// <seealso cref="InsulinDeliveryReader"/>
[ApiController]
[Tags("Analytics")]
[Route("api/v4/[controller]")]
[Produces("application/json")]
[BadRequestOnInvalidInput]
[RequestSizeLimit(StatisticsController.ComputeBodyLimitBytes)]
public class StatisticsController : ControllerBase
{
    /// <summary>
    /// Body ceiling for every action here that computes over a caller-supplied collection. A report
    /// covering a year posts every reading in the range in one body, so the bound is set by the
    /// longest range a report can ask for rather than by a typical request.
    /// </summary>
    public const int ComputeBodyLimitBytes = 100 * 1024 * 1024;

    private readonly IStatisticsService _statisticsService;
    private readonly ITenantCache _cache;
    private readonly IProfileProjectionService _profileProjectionService;
    private readonly IStateSpanRepository _stateSpanRepository;
    private readonly ISensorGlucoseRepository _sensorGlucoseRepository;
    private readonly ICarbIntakeRepository _carbIntakeRepository;
    private readonly AidSystemMetricsReader _aidSystemMetrics;
    private readonly RangeAnalyticsReader _rangeAnalytics;
    private readonly InsulinDeliveryReader _insulinDelivery;
    private readonly IDeviceEventRepository _deviceEventRepository;
    private readonly ICanonicalGlucoseService _canonicalGlucose;
    private readonly TenantClockResolver _tenantClock;
    private readonly ITenantGlucoseUnits _glucoseUnits;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<StatisticsController> _logger;

    public StatisticsController(
        IStatisticsService statisticsService,
        ITenantCache cache,
        IProfileProjectionService profileProjectionService,
        IStateSpanRepository stateSpanRepository,
        ISensorGlucoseRepository sensorGlucoseRepository,
        ICarbIntakeRepository carbIntakeRepository,
        AidSystemMetricsReader aidSystemMetrics,
        RangeAnalyticsReader rangeAnalytics,
        InsulinDeliveryReader insulinDelivery,
        IDeviceEventRepository deviceEventRepository,
        ICanonicalGlucoseService canonicalGlucose,
        TenantClockResolver tenantClock,
        ITenantGlucoseUnits glucoseUnits,
        TimeProvider timeProvider,
        ILogger<StatisticsController> logger
    )
    {
        _statisticsService = statisticsService;
        _cache = cache;
        _profileProjectionService = profileProjectionService;
        _stateSpanRepository = stateSpanRepository;
        _sensorGlucoseRepository = sensorGlucoseRepository;
        _carbIntakeRepository = carbIntakeRepository;
        _aidSystemMetrics = aidSystemMetrics;
        _rangeAnalytics = rangeAnalytics;
        _insulinDelivery = insulinDelivery;
        _deviceEventRepository = deviceEventRepository;
        _canonicalGlucose = canonicalGlucose;
        _timeProvider = timeProvider;
        _tenantClock = tenantClock;
        _glucoseUnits = glucoseUnits;
        _logger = logger;
    }

    private long UntilMills(DateTime endUtc) =>
        StatisticsService.UntilMills(endUtc, _timeProvider.GetUtcNow());

    /// <summary>
    /// Appends one <see cref="TempBasalOrigin.Scheduled"/> TempBasal per step of
    /// <see cref="ScheduledBasal"/> when the pump reported none.
    /// </summary>
    /// <remarks>
    /// <paramref name="recordedBasal"/> is basal delivered by a route other than TempBasals. A
    /// non-empty one suppresses the fallback: a profile baseline on top of MDI injections would
    /// double-count the day's coverage. <c>null</c> where the caller does not read injections.
    /// <paramref name="profileSegments"/> is the schedule when the caller already read it, and
    /// <paramref name="overrides"/> the override scale factors when it already read those.
    /// </remarks>
    private async Task AddScheduledBasalFallbackAsync(
        List<TempBasal> tempBasals,
        DateTime startUtc,
        DateTime endUtc,
        IReadOnlyCollection<BasalInjection>? recordedBasal,
        CancellationToken ct,
        IReadOnlyList<BasalSegment>? profileSegments = null,
        IReadOnlyList<ScaleFactorInterval>? overrides = null)
    {
        if (recordedBasal is { Count: > 0 }
            || StatisticsService.DeliveredWithin(tempBasals, startUtc, UntilMills(endUtc)).Count > 0)
            return;

        var scheduled = new ScheduledBasal(
            profileSegments ?? await _insulinDelivery.ReadProfileBasalSegmentsAsync(startUtc, endUtc, ct),
            overrides ?? await ScheduledBasal.OverridesAsync(_stateSpanRepository, startUtc.ToMills(), endUtc.ToMills(), ct));
        foreach (var step in scheduled.Segments([]))
        {
            tempBasals.Add(new TempBasal
            {
                StartTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(step.Start).UtcDateTime,
                EndTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(step.End).UtcDateTime,
                Rate = step.Rate,
                Origin = TempBasalOrigin.Scheduled,
            });
        }
    }

    /// <summary>
    /// Calculate basic glucose statistics from provided glucose values
    /// </summary>
    /// <param name="values">Array of glucose values in mg/dL</param>
    /// <returns>Basic glucose statistics including mean, median, percentiles, etc.</returns>
    [HttpPost("basic-stats")]
    [EnableRateLimiting(ServiceRegistrationExtensions.StatisticsComputeRateLimitPolicy)]
    [RequireScope(Scope.ReportsRead)]
    public ActionResult<BasicGlucoseStats> CalculateBasicStats([FromBody] double[] values)
    {
        var result = _statisticsService.CalculateBasicStats(values);
        return Ok(result);
    }

    /// <summary>
    /// Calculate comprehensive glycemic variability metrics
    /// </summary>
    /// <param name="request">Request containing glucose values and entries</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Comprehensive glycemic variability metrics, or no content for fewer than two values</returns>
    [HttpPost("glycemic-variability")]
    [EnableRateLimiting(ServiceRegistrationExtensions.StatisticsComputeRateLimitPolicy)]
    [RequireScope(Scope.ReportsRead)]
    public async Task<ActionResult<GlycemicVariability>> CalculateGlycemicVariability(
        [FromBody] GlycemicVariabilityRequest request,
        CancellationToken cancellationToken = default
    )
    {
        var clock = await _tenantClock.ResolveAsync(cancellationToken);
        var result = _statisticsService.CalculateGlycemicVariability(
            request.Values,
            request.Entries,
            clock.Zone
        );
        if (result == null)
        {
            return NoContent();
        }
        result.TimeZoneUnavailableReason = clock.Reason;
        return Ok(result);
    }

    /// <summary>
    /// Calculate time in range metrics
    /// </summary>
    /// <param name="request">Request containing entries and optional thresholds</param>
    /// <returns>Time in range metrics including percentages, durations, and episodes</returns>
    [HttpPost("time-in-range")]
    [EnableRateLimiting(ServiceRegistrationExtensions.StatisticsComputeRateLimitPolicy)]
    [RequireScope(Scope.ReportsRead)]
    [RemoteQuery]
    public ActionResult<TimeInRangeMetrics> CalculateTimeInRange(
        [FromBody] TimeInRangeRequest request
    )
    {
        var result = _statisticsService.CalculateTimeInRange(
            request.Entries,
            request.Thresholds
        );
        return Ok(result);
    }

    /// <summary>
    /// Calculate glucose distribution across configurable bins
    /// </summary>
    /// <param name="request">Request containing entries and optional bins</param>
    /// <returns>Collection of distribution data points</returns>
    [HttpPost("glucose-distribution")]
    [EnableRateLimiting(ServiceRegistrationExtensions.StatisticsComputeRateLimitPolicy)]
    [RequireScope(Scope.ReportsRead)]
    public ActionResult<IEnumerable<DistributionDataPoint>> CalculateGlucoseDistribution(
        [FromBody] GlucoseDistributionRequest request
    )
    {
        var result = _statisticsService.CalculateGlucoseDistribution(
            request.Entries,
            request.Bins
        );
        return Ok(result);
    }

    /// <summary>
    /// Calculate averaged statistics for each hour of the day (0-23), on the tenant's local clock
    /// </summary>
    /// <param name="entries">Array of sensor glucose readings</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Collection of averaged statistics for each hour</returns>
    [HttpPost("averaged-stats")]
    [EnableRateLimiting(ServiceRegistrationExtensions.StatisticsComputeRateLimitPolicy)]
    [RequireScope(Scope.ReportsRead)]
    [RemoteQuery]
    public async Task<ActionResult<IEnumerable<AveragedStats>>> CalculateAveragedStats(
        [FromBody] SensorGlucose[] entries,
        CancellationToken cancellationToken = default
    )
    {
        var clock = await _tenantClock.ResolveAsync(cancellationToken);
        return Ok(_statisticsService.CalculateAveragedStats(entries, clock.Zone));
    }

    /// <summary>
    /// Calculate treatment summary for a collection of boluses and carb intakes
    /// </summary>
    /// <param name="request">Request containing boluses and carb intakes</param>
    /// <returns>Treatment summary with totals and counts</returns>
    [HttpPost("treatment-summary")]
    [EnableRateLimiting(ServiceRegistrationExtensions.StatisticsComputeRateLimitPolicy)]
    [RequireScope(Scope.ReportsRead)]
    public ActionResult<TreatmentSummary> CalculateTreatmentSummary(
        [FromBody] TreatmentSummaryRequest request
    )
    {
        var result = _statisticsService.CalculateTreatmentSummary(
            request.Boluses ?? Enumerable.Empty<Bolus>(),
            request.CarbIntakes ?? Enumerable.Empty<CarbIntake>(),
            dayCount: request.DayCount ?? 1
        );
        return Ok(result);
    }

    /// <summary>
    /// Calculate overall averages across multiple days
    /// </summary>
    /// <param name="dailyDataPoints">Array of daily data points</param>
    /// <returns>Overall averages or null if no data</returns>
    [HttpPost("overall-averages")]
    [EnableRateLimiting(ServiceRegistrationExtensions.StatisticsComputeRateLimitPolicy)]
    [RequireScope(Scope.ReportsRead)]
    public ActionResult<OverallAverages> CalculateOverallAverages(
        [FromBody] DayData[] dailyDataPoints
    )
    {
        var result = _statisticsService.CalculateOverallAverages(dailyDataPoints);
        if (result == null)
        {
            return NoContent();
        }
        return Ok(result);
    }

    /// <summary>
    /// Master glucose analytics function that calculates comprehensive metrics
    /// </summary>
    /// <param name="request">Request containing sensor glucose readings, boluses, carb intakes, and configuration</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Comprehensive glucose analytics</returns>
    [HttpPost("comprehensive-analytics")]
    [EnableRateLimiting(ServiceRegistrationExtensions.StatisticsComputeRateLimitPolicy)]
    [RequireScope(Scope.ReportsRead)]
    public async Task<ActionResult<GlucoseAnalytics>> AnalyzeGlucoseData(
        [FromBody] GlucoseAnalyticsRequest request,
        CancellationToken cancellationToken = default
    )
    {
        var clock = await _tenantClock.ResolveAsync(cancellationToken);
        var result = _statisticsService.AnalyzeGlucoseData(
            request.Entries,
            request.Boluses ?? Enumerable.Empty<Bolus>(),
            request.CarbIntakes ?? Enumerable.Empty<CarbIntake>(),
            StatisticsService.ClinicalMinimumDays,
            request.Config,
            timeZone: clock.Zone
        );
        return Ok(clock.Annotate(result));
    }

    /// <summary>
    /// Extended glucose analytics including GMI, GRI, and clinical target assessment
    /// </summary>
    /// <param name="request">Request containing sensor glucose readings, boluses, carb intakes, population type, and configuration</param>
    /// <returns>Extended glucose analytics with modern clinical metrics</returns>
    [HttpPost("extended-analytics")]
    [EnableRateLimiting(ServiceRegistrationExtensions.StatisticsComputeRateLimitPolicy)]
    [RequireScope(Scope.ReportsRead)]
    public async Task<ActionResult<ExtendedGlucoseAnalytics>> AnalyzeGlucoseDataExtended(
        [FromBody] ExtendedGlucoseAnalyticsRequest request,
        CancellationToken cancellationToken = default
    )
    {
        var clock = await _tenantClock.ResolveAsync(cancellationToken);
        var result = _statisticsService.AnalyzeGlucoseDataExtended(
            request.Entries,
            request.Boluses ?? Enumerable.Empty<Bolus>(),
            request.CarbIntakes ?? Enumerable.Empty<CarbIntake>(),
            request.StartDate is { } start && request.EndDate is { } end
                ? StatisticsService.WindowDays(start, end)
                : StatisticsService.ClinicalMinimumDays,
            request.Population,
            request.Config,
            clock.Zone
        );
        return Ok(clock.Annotate(
            result,
            request.StartDate is { } from && request.EndDate is { } to
                ? StatisticsService.WindowDays(from, to, clock.Zone)
                : null));
    }

    /// <summary>
    /// Extended glucose analytics for a date range, computed server-side. Fetches glucose,
    /// manual boluses, and carb intakes for the window from the database and runs
    /// <see cref="IStatisticsService.AnalyzeGlucoseDataExtended"/> plus
    /// <see cref="IStatisticsService.CalculateAveragedStats"/>.
    /// </summary>
    /// <param name="startDate">Start of the window (inclusive, UTC).</param>
    /// <param name="endDate">End of the window (exclusive, UTC).</param>
    /// <param name="population">Diabetes population for clinical target assessment. Defaults to Type 1 adult.</param>
    /// <param name="patientDeviceId">Restricts glucose to readings from this one patient device,
    /// in place of the canonical stream selected across all devices. Boluses and carb intakes are
    /// not filtered.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The extended analytics and time-of-day averaged stats for the window.</returns>
    [HttpGet("range-analytics")]
    [RequireScope(Scope.ReportsRead)]
    [RemoteQuery]
    [ResponseCache(Duration = 60, Location = ResponseCacheLocation.Client)]
    public async Task<ActionResult<ReportAnalysisResult>> GetRangeAnalytics(
        [FromQuery] DateTime startDate,
        [FromQuery] DateTime endDate,
        [FromQuery] DiabetesPopulation population = DiabetesPopulation.Type1Adult,
        [FromQuery] Guid? patientDeviceId = null,
        CancellationToken cancellationToken = default
    )
    {
        return Ok(await _rangeAnalytics.ReadAsync(startDate, endDate, population, patientDeviceId, cancellationToken));
    }

    /// <summary>
    /// Two periods' glucose and insulin figures side by side, with each metric's change from the
    /// first period to the second. Reads each period through
    /// <see cref="RangeAnalyticsReader.ReadWithInsulinDeliveryAsync"/>, and states the change per
    /// <see cref="PeriodComparisonCalculator"/>.
    /// </summary>
    /// <param name="startDateA">Start of the first period (inclusive, UTC).</param>
    /// <param name="endDateA">End of the first period (exclusive, UTC).</param>
    /// <param name="startDateB">Start of the second period (inclusive, UTC).</param>
    /// <param name="endDateB">End of the second period (exclusive, UTC).</param>
    /// <param name="units">The units glucose metrics are served in. Defaults to the tenant's.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Each period's data flag and time in range, and every metric's values, change and percent change.</returns>
    [HttpGet("period-comparison")]
    [RequireScope(Scope.ReportsRead)]
    [RemoteQuery]
    [ResponseCache(Duration = 60, Location = ResponseCacheLocation.Client)]
    public async Task<ActionResult<PeriodComparison>> GetPeriodComparison(
        [FromQuery] DateTime startDateA,
        [FromQuery] DateTime endDateA,
        [FromQuery] DateTime startDateB,
        [FromQuery] DateTime endDateB,
        [FromQuery] GlucoseUnit? units = null,
        CancellationToken cancellationToken = default
    )
    {
        if (this.RejectDateSpan(startDateA, endDateA) is { } overlongA)
            return overlongA;
        if (this.RejectDateSpan(startDateB, endDateB) is { } overlongB)
            return overlongB;

        var periodA = _rangeAnalytics.ReadWithInsulinDeliveryAsync(startDateA, endDateA, DiabetesPopulation.Type1Adult, cancellationToken);
        var periodB = _rangeAnalytics.ReadWithInsulinDeliveryAsync(startDateB, endDateB, DiabetesPopulation.Type1Adult, cancellationToken);
        var resolvedUnits = units is { } requested
            ? Task.FromResult(requested)
            : TenantGlucoseUnitAsync(cancellationToken);

        await Task.WhenAll(periodA, periodB, resolvedUnits);

        var (analysisA, insulinA) = await periodA;
        var (analysisB, insulinB) = await periodB;
        return Ok(PeriodComparisonCalculator.Build(
            new(analysisA, insulinA),
            new(analysisB, insulinB),
            await resolvedUnits));
    }

    private async Task<GlucoseUnit> TenantGlucoseUnitAsync(CancellationToken cancellationToken) =>
        await _glucoseUnits.UsesMmolAsync(cancellationToken)
            ? GlucoseUnit.Mmol
            : GlucoseUnit.MgDl;

    /// <summary>
    /// Mean glucose per weekday in each five-minute slot of the day for a date range, bucketed on
    /// the tenant's local clock. Backs the week-to-week report.
    /// </summary>
    /// <param name="startDate">Start of the window (inclusive, UTC).</param>
    /// <param name="endDate">End of the window (exclusive, UTC).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The populated slots in time-of-day order, or no slots and why when the tenant has no timezone.</returns>
    [HttpGet("weekday-averages")]
    [RequireScope(Scope.ReportsRead)]
    [RemoteQuery]
    [ResponseCache(Duration = 60, Location = ResponseCacheLocation.Client)]
    public async Task<ActionResult<WeekdayAveragesResponse>> GetWeekdayAverages(
        [FromQuery] DateTime startDate,
        [FromQuery] DateTime endDate,
        CancellationToken cancellationToken = default
    )
    {
        var clock = await _tenantClock.ResolveAsync(cancellationToken);
        if (clock.Zone is not { } tz)
            return Ok(new WeekdayAveragesResponse { TimeZoneUnavailableReason = clock.Reason });

        var startDt = DateTime.SpecifyKind(startDate, DateTimeKind.Utc);
        var endDt = DateTime.SpecifyKind(endDate, DateTimeKind.Utc);

        // Uncapped and canonicalised for the same reasons as RangeAnalyticsReader.
        var rawGlucose = (await _sensorGlucoseRepository.GetForAnalyticsAsync(startDt, endDt, null, null, int.MaxValue, descending: false, ct: cancellationToken)).ToList();
        var entries = await _canonicalGlucose.SelectAsync(rawGlucose, cancellationToken);

        return Ok(new WeekdayAveragesResponse
        {
            Slots = _statisticsService.CalculateWeekdayAverages(entries, tz).ToList(),
        });
    }

    /// <summary>
    /// Which hours of the day go best and worst for a date range, bucketed on the tenant's local
    /// clock. Backs the hourly-patterns report.
    /// </summary>
    /// <param name="startDate">Start of the window (inclusive, UTC).</param>
    /// <param name="endDate">End of the window (exclusive, UTC).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Every hour's figures plus the ranked best, worst and most-below-range hours.</returns>
    [HttpGet("hourly-patterns")]
    [RequireScope(Scope.ReportsRead)]
    [RemoteQuery]
    [ResponseCache(Duration = 60, Location = ResponseCacheLocation.Client)]
    public async Task<ActionResult<HourlyPatterns>> GetHourlyPatterns(
        [FromQuery] DateTime startDate,
        [FromQuery] DateTime endDate,
        CancellationToken cancellationToken = default
    )
    {
        var startDt = DateTime.SpecifyKind(startDate, DateTimeKind.Utc);
        var endDt = DateTime.SpecifyKind(endDate, DateTimeKind.Utc);

        // Uncapped and canonicalised for the same reasons as RangeAnalyticsReader.
        var rawGlucose = (await _sensorGlucoseRepository.GetForAnalyticsAsync(startDt, endDt, null, null, int.MaxValue, descending: false, ct: cancellationToken)).ToList();
        var entries = await _canonicalGlucose.SelectAsync(rawGlucose, cancellationToken);

        var clock = await _tenantClock.ResolveAsync(cancellationToken);

        var patterns = _statisticsService.CalculateHourlyPatterns(entries, clock.Zone);
        patterns.TimeZoneUnavailableReason = clock.Reason;
        return Ok(patterns);
    }

    /// <summary>
    /// Calculate Glucose Management Indicator (GMI)
    /// </summary>
    /// <param name="meanGlucose">Mean glucose in mg/dL</param>
    /// <returns>GMI with value and interpretation</returns>
    [HttpGet("gmi/{meanGlucose:double}")]
    [RequireScope(Scope.ReportsRead)]
    public ActionResult<GlucoseManagementIndicator> CalculateGMI(double meanGlucose)
    {
        var result = _statisticsService.CalculateGMI(meanGlucose);
        return Ok(result);
    }

    /// <summary>
    /// Calculate Glycemic Risk Index (GRI) from time in range metrics
    /// </summary>
    /// <param name="timeInRange">Time in range metrics</param>
    /// <returns>GRI with score, zone, and interpretation</returns>
    [HttpPost("gri")]
    [EnableRateLimiting(ServiceRegistrationExtensions.StatisticsComputeRateLimitPolicy)]
    [RequireScope(Scope.ReportsRead)]
    public ActionResult<GlycemicRiskIndex> CalculateGRI([FromBody] TimeInRangeMetrics timeInRange)
    {
        var result = _statisticsService.CalculateGRI(timeInRange);
        return Ok(result);
    }

    /// <summary>
    /// Assess glucose data against clinical targets for a specific population
    /// </summary>
    /// <param name="request">Request containing analytics and population type</param>
    /// <returns>Clinical target assessment with actionable insights</returns>
    [HttpPost("clinical-assessment")]
    [EnableRateLimiting(ServiceRegistrationExtensions.StatisticsComputeRateLimitPolicy)]
    [RequireScope(Scope.ReportsRead)]
    public ActionResult<ClinicalTargetAssessment> AssessAgainstTargets(
        [FromBody] ClinicalAssessmentRequest request
    )
    {
        var result = _statisticsService.AssessAgainstTargets(
            request.Analytics,
            request.Population
        );
        return Ok(result);
    }

    /// <summary>
    /// Assess data sufficiency for a valid clinical report
    /// </summary>
    /// <param name="request">Request containing entries and optional period settings</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Data sufficiency assessment, counting days with data on the tenant's local calendar (UTC when it has no zone)</returns>
    [HttpPost("data-sufficiency")]
    [EnableRateLimiting(ServiceRegistrationExtensions.StatisticsComputeRateLimitPolicy)]
    [RequireScope(Scope.ReportsRead)]
    public async Task<ActionResult<DataSufficiencyAssessment>> AssessDataSufficiency(
        [FromBody] DataSufficiencyRequest request,
        CancellationToken cancellationToken = default
    )
    {
        var clock = await _tenantClock.ResolveAsync(cancellationToken);
        var result = _statisticsService.AssessDataSufficiency(
            request.Entries,
            request.Days,
            request.ExpectedReadingsPerDay,
            clock.Zone
        );
        result.TimeZoneUnavailableReason = clock.Reason;
        return Ok(result);
    }

    /// <summary>
    /// Get clinical targets for a specific diabetes population
    /// </summary>
    /// <param name="population">Population type (Type1Adult, Type2Adult, Elderly, Pregnancy, etc.)</param>
    /// <returns>Clinical targets for the specified population</returns>
    [HttpGet("clinical-targets/{population}")]
    [RequireScope(Scope.ReportsRead)]
    public ActionResult<ClinicalTargets> GetClinicalTargets(DiabetesPopulation population)
    {
        var result = ClinicalTargets.ForPopulation(population);
        return Ok(result);
    }

    /// <summary>
    /// Calculate estimated A1C from average glucose
    /// </summary>
    /// <param name="averageGlucose">Average glucose in mg/dL</param>
    /// <returns>Estimated A1C percentage</returns>
    [HttpGet("estimated-a1c/{averageGlucose:double}")]
    [RequireScope(Scope.ReportsRead)]
    public ActionResult<double> CalculateEstimatedA1C(double averageGlucose)
    {
        var result = _statisticsService.CalculateEstimatedA1C(averageGlucose);
        return Ok(result);
    }

    /// <summary>
    /// Convert mg/dL to mmol/L
    /// </summary>
    /// <param name="mgdl">Glucose value in mg/dL</param>
    /// <returns>Glucose value in mmol/L</returns>
    [HttpGet("convert/mgdl-to-mmol/{mgdl:double}")]
    [RequireScope(Scope.ReportsRead)]
    public ActionResult<double> MgdlToMMOL(double mgdl)
    {
        var result = _statisticsService.MgdlToMMOL(mgdl);
        return Ok(result);
    }

    /// <summary>
    /// Convert mmol/L to mg/dL
    /// </summary>
    /// <param name="mmol">Glucose value in mmol/L</param>
    /// <returns>Glucose value in mg/dL</returns>
    [HttpGet("convert/mmol-to-mgdl/{mmol:double}")]
    [RequireScope(Scope.ReportsRead)]
    public ActionResult<double> MmolToMGDL(double mmol)
    {
        var result = _statisticsService.MmolToMGDL(mmol);
        return Ok(result);
    }

    /// <summary>
    /// Validate treatment data for completeness and consistency
    /// </summary>
    /// <param name="treatment">Treatment to validate</param>
    /// <returns>True if treatment data is valid</returns>
    [HttpPost("validate/treatment")]
    [EnableRateLimiting(ServiceRegistrationExtensions.StatisticsComputeRateLimitPolicy)]
    [RequireScope(Scope.ReportsRead)]
    public ActionResult<bool> ValidateTreatmentData([FromBody] Treatment treatment)
    {
        var result = _statisticsService.ValidateTreatmentData(treatment);
        return Ok(result);
    }

    /// <summary>
    /// Clean and filter treatment data
    /// </summary>
    /// <param name="treatments">Array of treatments to clean</param>
    /// <returns>Cleaned collection of treatments</returns>
    [HttpPost("clean/treatments")]
    [EnableRateLimiting(ServiceRegistrationExtensions.StatisticsComputeRateLimitPolicy)]
    [RequireScope(Scope.ReportsRead)]
    public ActionResult<IEnumerable<Treatment>> CleanTreatmentData(
        [FromBody] Treatment[] treatments
    )
    {
        var result = _statisticsService.CleanTreatmentData(treatments);
        return Ok(result);
    }

    /// <summary>
    /// Gets comprehensive statistics for multiple time periods (1, 3, 7, 30, and 90 days).
    /// Reads sensor glucose, bolus, carb, and temp-basal data for the 90-day window once and slices each period from it,
    /// computes <see cref="GlucoseAnalytics"/>, <see cref="TreatmentSummary"/>, and
    /// <see cref="InsulinDeliveryStatistics"/>, and caches the result for 5 minutes.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="MultiPeriodStatistics"/> containing a <see cref="PeriodStatistics"/>
    /// entry for each of the five standard periods.</returns>
    /// <remarks>
    /// When a period has no temp basals, profile basal may stand in for scheduled basal; see
    /// <see cref="AddScheduledBasalFallbackAsync"/> for the rest of the gate.
    /// GMI reliability is assessed per-period using context-appropriate recommended-day minimums
    /// (e.g., 1-day periods cannot require 14 days of data).
    /// The cached value holds every category; <see cref="StatisticsReadScopeGuard"/> narrows each
    /// response to the caller's scopes.
    /// </remarks>
    [HttpGet("periods")]
    [RequireScope(Scope.GlucoseRead, Scope.ReportsRead)]
    [RemoteQuery]
    public async Task<ActionResult<MultiPeriodStatistics>> GetMultiPeriodStatistics(
        CancellationToken cancellationToken = default
    )
    {
        // Cache for 5 minutes: long enough to absorb rapid dashboard refreshes,
        // short enough that newly-imported connector data (basal StateSpans, etc.) appears promptly.
        var result = await _cache.GetOrSetAsync(
            $"{TenantCacheFamily.Statistics}:multi-period",
            () => ComputeMultiPeriodStatisticsAsync(cancellationToken),
            TimeSpan.FromMinutes(5),
            cancellationToken);

        return Ok(StatisticsReadScopeGuard.Redact(result, HttpContext.GetGrantedScopes()));
    }

    private async Task<MultiPeriodStatistics> ComputeMultiPeriodStatisticsAsync(CancellationToken cancellationToken)
    {
        // Calculate statistics for each period
        var periods = new[] { 1, 3, 7, 30, 90 };
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var result = new MultiPeriodStatistics { LastUpdated = now };

        var periodResults = new List<(int Days, PeriodStatistics Statistics)>();

        // Every window ends at `now`, so the 90-day read contains every shorter window and the repositories'
        // inclusive lower bound on Timestamp is reproduced by the slices below.
        var widestStart = now.AddDays(-periods.Max());
        var glucoseTask = _sensorGlucoseRepository.GetForAnalyticsAsync(from: (DateTime?)widestStart, to: (DateTime?)now, device: null, source: null, limit: int.MaxValue, descending: false, ct: cancellationToken);
        var carbTask    = _carbIntakeRepository.GetAsync(from: (DateTime?)widestStart, to: (DateTime?)now, device: null, source: null, limit: int.MaxValue, descending: false, ct: cancellationToken);
        var sessionTask = _deviceEventRepository.GetAllCopiesAsync(widestStart, now, SensorSessionInference.SessionEventTypes, cancellationToken);
        var insulin = await _insulinDelivery.ReadRecordsAsync(widestStart, now, cancellationToken, glucoseTask, carbTask, sessionTask);
        var allGlucose = (await glucoseTask).ToList();
        var allCarbs = (await carbTask).ToList();
        var allDeviceEvents = await sessionTask;
        var clock = await _tenantClock.ResolveAsync(cancellationToken);
        var overrides = await ScheduledBasal.OverridesAsync(
            _stateSpanRepository, widestStart.ToMills(), now.ToMills(), cancellationToken);

        foreach (var days in periods)
        {
            var startDate = now.AddDays(-days);
            var endDate = now;
            var tempBasalReadStart = StatisticsService.TempBasalReadStart(startDate);

            var filteredBoluses  = insulin.ManualBoluses.Where(b => b.Timestamp >= startDate).ToList();
            var algorithmBoluses = insulin.AlgorithmBoluses.Where(b => b.Timestamp >= startDate).ToList();
            var tempBasals       = insulin.TempBasals.Where(t => t.StartTimestamp >= tempBasalReadStart).ToList();
            var basalInjections  = insulin.BasalInjections.Where(i => i.Timestamp >= startDate).ToList();
            var filteredEntries  = (await _canonicalGlucose.SelectAsync(allGlucose.Where(g => g.Timestamp >= startDate).ToList(), cancellationToken)).ToList();
            var filteredCarbs    = allCarbs.Where(c => c.Timestamp >= startDate).ToList();

            if (StatisticsService.HasSufficientPeriodData(filteredEntries.Count))
                await AddScheduledBasalFallbackAsync(
                    tempBasals, startDate, endDate, basalInjections, cancellationToken, overrides: overrides);

            periodResults.Add(
                (
                    days,
                    _statisticsService.CalculatePeriodStatistics(
                        days,
                        startDate,
                        endDate,
                        filteredEntries,
                        filteredBoluses,
                        algorithmBoluses,
                        tempBasals,
                        basalInjections,
                        filteredCarbs,
                        allDeviceEvents.Where(e => e.Timestamp >= startDate).ToList(),
                        UntilMills(endDate),
                        clock.Zone
                    )
                )
            );
        }

        // Map results to the response object
        foreach (var periodResult in periodResults)
        {
            if (periodResult.Statistics.Analytics is { } analytics)
                clock.Annotate(analytics);

            switch (periodResult.Days)
            {
                case 1:
                    result.LastDay = periodResult.Statistics;
                    break;
                case 3:
                    result.Last3Days = periodResult.Statistics;
                    break;
                case 7:
                    result.LastWeek = periodResult.Statistics;
                    break;
                case 30:
                    result.LastMonth = periodResult.Statistics;
                    break;
                case 90:
                    result.Last90Days = periodResult.Statistics;
                    break;
            }
        }

        return result;
    }

    /// <summary>
    /// Analyze glucose patterns around site changes to identify impact of site age on control
    /// </summary>
    /// <param name="request">Request containing sensor glucose readings, device events, and analysis parameters</param>
    /// <returns>Site change impact analysis with averaged glucose patterns</returns>
    [HttpPost("site-change-impact")]
    [EnableRateLimiting(ServiceRegistrationExtensions.StatisticsComputeRateLimitPolicy)]
    [RequireScope(Scope.ReportsRead)]
    public ActionResult<SiteChangeImpactAnalysis> CalculateSiteChangeImpact(
        [FromBody] SiteChangeImpactRequest request
    )
    {
        var result = _statisticsService.CalculateSiteChangeImpact(
            request.Entries,
            request.DeviceEvents,
            request.HoursBeforeChange,
            request.HoursAfterChange,
            request.BucketSizeMinutes
        );
        return Ok(result);
    }

    /// <summary>
    /// Calculate daily basal/bolus ratio statistics for a date range, with each day's carbs
    /// </summary>
    /// <returns>Daily basal/bolus ratio breakdown and carb totals, with averages; no days and the reason when the tenant has no timezone</returns>
    [HttpGet("daily-basal-bolus-ratios")]
    [RequireScope(Scope.ReportsRead)]
    [RemoteQuery]
    public async Task<ActionResult<DailyBasalBolusRatioResponse>> GetDailyBasalBolusRatios(
        [FromQuery] DateTime startDate,
        [FromQuery] DateTime endDate,
        CancellationToken cancellationToken = default
    )
    {
        var clock = await _tenantClock.ResolveAsync(cancellationToken);
        if (clock.Zone is not { } tz)
            return Ok(new DailyBasalBolusRatioResponse { TimeZoneUnavailableReason = clock.Reason });

        var startDt = DateTime.SpecifyKind(startDate, DateTimeKind.Utc);
        var endDt = DateTime.SpecifyKind(endDate, DateTimeKind.Utc);

        var carbTask = _carbIntakeRepository.GetAsync(startDt, endDt, null, null, int.MaxValue, descending: false, ct: cancellationToken);

        var (boluses, algorithmBoluses, tempBasals, basalInjections) =
            await _insulinDelivery.ReadRecordsAsync(startDt, endDt, cancellationToken, carbTask);
        var carbs = await carbTask;

        var result = _statisticsService.CalculateDailyBasalBolusRatios(
            boluses,
            algorithmBoluses,
            tempBasals,
            startDt,
            UntilMills(endDt),
            tz,
            basalInjections,
            carbs
        );
        return Ok(result);
    }

    /// <summary>
    /// Pre-aggregated month-by-day statistics for the calendar punch-card view. Fetches glucose,
    /// boluses, carb intakes, and basal records in a single batch for
    /// <see cref="IStatisticsService.CalculatePunchCard"/> (no per-day round-trips).
    /// </summary>
    /// <param name="startDate">Inclusive start calendar date in the tenant's timezone.</param>
    /// <param name="endDate">Inclusive end calendar date in the tenant's timezone.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see cref="PunchCardResponse"/> with months, days, and global maxes for chart scaling.</returns>
    /// <remarks>
    /// For a caller without <c>reports.read</c> or <c>treatments.read</c> no insulin or carbs are read,
    /// so every carb, insulin and ratio figure is zero (<see cref="StatisticsReadScopeGuard"/>).
    /// </remarks>
    [HttpGet("punch-card")]
    [RequireScope(Scope.GlucoseRead, Scope.ReportsRead)]
    [RemoteQuery]
    public async Task<ActionResult<PunchCardResponse>> GetPunchCardData(
        [FromQuery] DateOnly startDate,
        [FromQuery] DateOnly endDate,
        CancellationToken cancellationToken = default
    )
    {
        var clock = await _tenantClock.ResolveAsync(cancellationToken);
        if (clock.Zone is not { } tz)
            return Ok(new PunchCardResponse { TimeZoneUnavailableReason = clock.Reason });

        var (startDt, endDt) = StatisticsService.PunchCardRange(startDate, endDate, tz);

        var rawGlucoseTask = _sensorGlucoseRepository.GetForAnalyticsAsync(startDt, endDt, null, null, int.MaxValue, descending: false, ct: cancellationToken);
        var treatmentsAllowed = StatisticsReadScopeGuard.AllowsTreatmentAggregates(HttpContext.GetGrantedScopes());
        var carbTask = treatmentsAllowed
            ? _carbIntakeRepository.GetAsync(startDt, endDt, null, null, int.MaxValue, descending: false, ct: cancellationToken)
            : Task.FromResult(Enumerable.Empty<CarbIntake>());

        var (manualBoluses, algorithmBoluses, tempBasals, basalInjections) = treatmentsAllowed
            ? await _insulinDelivery.ReadRecordsAsync(startDt, endDt, cancellationToken, rawGlucoseTask, carbTask)
            : new InsulinRecords([], [], [], []);

        var rawGlucose  = (await rawGlucoseTask).ToList();
        var glucoseData = await _canonicalGlucose.SelectAsync(rawGlucose, cancellationToken);
        var carbs       = await carbTask;

        return Ok(_statisticsService.CalculatePunchCard(
            glucoseData,
            manualBoluses,
            algorithmBoluses,
            carbs,
            tempBasals,
            basalInjections,
            startDate,
            endDate,
            tz,
            _timeProvider.GetUtcNow(),
            cancellationToken));
    }

    /// <summary>
    /// Calculate comprehensive insulin delivery statistics for a date range
    /// </summary>
    /// <param name="startDate">Start of the period.</param>
    /// <param name="endDate">End of the period.</param>
    /// <param name="includeBoluses">Fill <see cref="InsulinDeliveryStatistics.ManualBoluses"/> with the records behind <see cref="InsulinDeliveryStatistics.ManualBolus"/>; ignored unless the caller holds treatments.read.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Comprehensive insulin delivery statistics</returns>
    [HttpGet("insulin-delivery-stats")]
    [RequireScope(Scope.ReportsRead)]
    [RemoteQuery]
    public async Task<ActionResult<InsulinDeliveryStatistics>> GetInsulinDeliveryStatistics(
        [FromQuery] DateTime startDate,
        [FromQuery] DateTime endDate,
        [FromQuery] bool includeBoluses = false,
        CancellationToken cancellationToken = default
    )
    {
        var (result, boluses) = await _insulinDelivery.ReadAsync(startDate, endDate, cancellationToken);
        if (includeBoluses && AnalyticsReadScopes.Allows(HttpContext.GetGrantedScopes(), Scope.TreatmentsRead))
            result.ManualBoluses = StatisticsService.CountedBoluses(boluses).ToList();
        return Ok(result);
    }

    /// <summary>
    /// Calculate comprehensive basal analysis statistics for a date range
    /// </summary>
    /// <param name="startDate">Start date of the analysis period</param>
    /// <param name="endDate">End date of the analysis period</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Comprehensive basal analysis with stats, temp basal info, and hourly percentiles</returns>
    [HttpGet("basal-analysis")]
    [RequireScope(Scope.ReportsRead)]
    [RemoteQuery]
    public async Task<ActionResult<BasalAnalysisResponse>> GetBasalAnalysis(
        [FromQuery] DateTime startDate,
        [FromQuery] DateTime endDate,
        CancellationToken cancellationToken = default
    )
    {
        // Force UTC kind to avoid DateTimeOffset throwing when the server's local
        // timezone offset would push DateTime.MinValue/MaxValue out of the valid range.
        var startUtc = DateTime.SpecifyKind(startDate, DateTimeKind.Utc);
        var endUtc = DateTime.SpecifyKind(endDate, DateTimeKind.Utc);

        // Uncapped for the reason given on InsulinDeliveryReader.ReadRecordsAsync.
        var tempBasals = (await _insulinDelivery.ReadTempBasalsAsync(startUtc, endUtc, cancellationToken)).ToList();

        await _insulinDelivery.FillMissingScheduledRatesAsync(tempBasals, startUtc, endUtc, cancellationToken);

        await AddScheduledBasalFallbackAsync(tempBasals, startUtc, endUtc, recordedBasal: null, cancellationToken);

        var clock = await _tenantClock.ResolveAsync(cancellationToken);

        var result = _statisticsService.CalculateBasalAnalysis(
            tempBasals,
            startUtc,
            endUtc,
            UntilMills(endUtc),
            clock.Zone
        );
        result.TimeZoneUnavailableReason = clock.Reason;
        return Ok(result);
    }

    /// <summary>
    /// Calculates the average insulin delivered per hour of day, split by scheduled
    /// basal, temp adjustments, and boluses, from pump-confirmed delivery records.
    /// Basal insulin is duration-weighted across the user-local hours each TempBasal
    /// overlaps, and averages are taken over the days that have delivery data — a
    /// window that extends before the first record does not distort the pattern. Also serves the
    /// profile's scheduled rate per hour and the period totals of total and basal delivery.
    /// </summary>
    /// <param name="startDate">Start date of the analysis period (UTC)</param>
    /// <param name="endDate">End date of the analysis period (UTC)</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>24 hourly averages, the period totals and the number of days with data; none of them and the reason when the tenant has no timezone</returns>
    [HttpGet("hourly-insulin-delivery")]
    [RequireScope(Scope.ReportsRead)]
    [RemoteQuery]
    public async Task<ActionResult<HourlyInsulinDeliveryResponse>> GetHourlyInsulinDelivery(
        [FromQuery] DateTime startDate,
        [FromQuery] DateTime endDate,
        CancellationToken cancellationToken = default
    )
    {
        var startUtc = DateTime.SpecifyKind(startDate, DateTimeKind.Utc);
        var endUtc = DateTime.SpecifyKind(endDate, DateTimeKind.Utc);

        var clock = await _tenantClock.ResolveAsync(cancellationToken);
        if (clock.Zone is not { } userTz)
            return Ok(new HourlyInsulinDeliveryResponse
            {
                StartDate = startUtc.ToString("yyyy-MM-dd"),
                EndDate = endUtc.ToString("yyyy-MM-dd"),
                TimeZoneUnavailableReason = clock.Reason,
            });

        var profileSegmentsTask = _insulinDelivery.ReadProfileBasalSegmentsAsync(startUtc, endUtc, cancellationToken);
        var (boluses, algorithmBoluses, tempBasals, basalInjections) =
            await _insulinDelivery.ReadRecordsAsync(startUtc, endUtc, cancellationToken, profileSegmentsTask);

        var profileSegments = await profileSegmentsTask;
        await AddScheduledBasalFallbackAsync(
            tempBasals, startUtc, endUtc, basalInjections, cancellationToken, profileSegments);

        var result = _statisticsService.CalculateHourlyInsulinDelivery(
            tempBasals,
            boluses,
            algorithmBoluses,
            startUtc,
            endUtc,
            UntilMills(endUtc),
            userTz,
            basalInjections,
            profileSegments
        );
        return Ok(result);
    }

    /// <summary>
    /// Calculates AID (Automated Insulin Delivery) system metrics for a date range.
    /// Uses patient device records to segment the period by algorithm and compute time-weighted metrics
    /// via <see cref="AidSystemMetricsReader"/>.
    /// </summary>
    /// <param name="startDate">Inclusive start of the analysis period (UTC).</param>
    /// <param name="endDate">Inclusive end of the analysis period (UTC).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An <see cref="AidSystemMetrics"/> object containing loop-on time, site-change counts,
    /// CGM active percent, and per-algorithm segment breakdowns.</returns>
    /// <remarks>
    /// CGM active time comes from <see cref="IStatisticsService.CalculateCgmActivePercent"/>,
    /// measured against the registered CGMs' windows where the tenant has registered any. The target
    /// range is optional; the report goes without it if it cannot be read. Each figure is narrowed to
    /// the caller's categories by <see cref="AidSystemMetricsReadScopeGuard"/>. A history-clamped
    /// caller reads the last <see cref="HistoryClamp.WindowHours"/> hours whatever the dates, as in
    /// <see cref="LoopAnalysisRanges.Clamped"/>, and a window ending in the future stops at now.
    /// </remarks>
    [HttpGet("aid-system-metrics")]
    [RequireScope(Scope.ReportsRead)]
    [RemoteQuery]
    public async Task<ActionResult<AidSystemMetrics>> GetAidSystemMetrics(
        [FromQuery] DateTime startDate,
        [FromQuery] DateTime endDate,
        CancellationToken cancellationToken = default
    )
    {
        var startDt = DateTime.SpecifyKind(startDate, DateTimeKind.Utc);
        var endDt = DateTime.SpecifyKind(endDate, DateTimeKind.Utc);
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        if (HttpContext.IsCallerHistoryClamped())
        {
            var window = LoopAnalysisRanges.Clamped(now, HistoryClamp.Window, TimeZoneInfo.Utc);
            (startDt, endDt) = (window.From, window.To);
        }
        else
        {
            if (this.RejectDateSpan(startDt, endDt) is { } overlong)
                return overlong;
            if (endDt > now)
                endDt = now;
        }

        return Ok(AidSystemMetricsReadScopeGuard.Redact(
            await _aidSystemMetrics.ReadAsync(startDt, endDt, cancellationToken),
            HttpContext.GetGrantedScopes()));
    }
}

/// <summary>
/// Request model for glycemic variability calculation
/// </summary>
public class GlycemicVariabilityRequest
{
    /// <summary>
    /// Collection of glucose values in mg/dL
    /// </summary>
    public IEnumerable<double> Values { get; set; } = Enumerable.Empty<double>();

    /// <summary>
    /// Collection of sensor glucose readings with timestamps
    /// </summary>
    public IEnumerable<SensorGlucose> Entries { get; set; } = Enumerable.Empty<SensorGlucose>();
}

/// <summary>
/// Request model for time in range calculation
/// </summary>
public class TimeInRangeRequest
{
    /// <summary>
    /// Collection of sensor glucose readings
    /// </summary>
    public IEnumerable<SensorGlucose> Entries { get; set; } = Enumerable.Empty<SensorGlucose>();

    /// <summary>
    /// Optional glycemic thresholds
    /// </summary>
    public GlycemicThresholds? Thresholds { get; set; }
}

/// <summary>
/// Request model for glucose distribution calculation
/// </summary>
public class GlucoseDistributionRequest
{
    /// <summary>
    /// Collection of sensor glucose readings
    /// </summary>
    public IEnumerable<SensorGlucose> Entries { get; set; } = Enumerable.Empty<SensorGlucose>();

    /// <summary>
    /// Optional distribution bins
    /// </summary>
    public IEnumerable<DistributionBin>? Bins { get; set; }
}

/// <summary>
/// Request model for comprehensive glucose analytics
/// </summary>
public class GlucoseAnalyticsRequest
{
    /// <summary>
    /// Collection of sensor glucose readings
    /// </summary>
    public IEnumerable<SensorGlucose> Entries { get; set; } = Enumerable.Empty<SensorGlucose>();

    /// <summary>
    /// Optional collection of bolus deliveries
    /// </summary>
    public IEnumerable<Bolus>? Boluses { get; set; }

    /// <summary>
    /// Optional collection of carb intakes
    /// </summary>
    public IEnumerable<CarbIntake>? CarbIntakes { get; set; }

    /// <summary>
    /// Optional extended analysis configuration
    /// </summary>
    public ExtendedAnalysisConfig? Config { get; set; }
}

/// <summary>
/// Request model for extended glucose analytics with GMI, GRI, and clinical assessment
/// </summary>
public class ExtendedGlucoseAnalyticsRequest
{
    /// <summary>
    /// Collection of sensor glucose readings
    /// </summary>
    public IEnumerable<SensorGlucose> Entries { get; set; } = Enumerable.Empty<SensorGlucose>();

    /// <summary>
    /// Optional collection of bolus deliveries
    /// </summary>
    public IEnumerable<Bolus>? Boluses { get; set; }

    /// <summary>
    /// Optional collection of carb intakes
    /// </summary>
    public IEnumerable<CarbIntake>? CarbIntakes { get; set; }

    /// <summary>
    /// Diabetes population type for clinical target assessment
    /// </summary>
    public DiabetesPopulation Population { get; set; } = DiabetesPopulation.Type1Adult;

    /// <summary>
    /// Optional extended analysis configuration
    /// </summary>
    public ExtendedAnalysisConfig? Config { get; set; }

    /// <summary>
    /// Start of the window the readings were requested for. With <see cref="EndDate"/> it sets the reliability minimum.
    /// </summary>
    public DateTime? StartDate { get; set; }

    /// <summary>
    /// End of the window the readings were requested for.
    /// </summary>
    public DateTime? EndDate { get; set; }
}

/// <summary>
/// Request model for treatment summary calculation
/// </summary>
public class TreatmentSummaryRequest
{
    /// <summary>
    /// Optional collection of bolus deliveries
    /// </summary>
    public IEnumerable<Bolus>? Boluses { get; set; }

    /// <summary>
    /// Optional collection of carb intakes
    /// </summary>
    public IEnumerable<CarbIntake>? CarbIntakes { get; set; }

    /// <summary>
    /// Calendar days the records cover, for the per-day averages. Defaults to one day.
    /// </summary>
    public int? DayCount { get; set; }
}

/// <summary>
/// Request model for clinical assessment
/// </summary>
public class ClinicalAssessmentRequest
{
    /// <summary>
    /// Glucose analytics to assess
    /// </summary>
    public GlucoseAnalytics Analytics { get; set; } = new();

    /// <summary>
    /// Diabetes population type for clinical target assessment
    /// </summary>
    public DiabetesPopulation Population { get; set; } = DiabetesPopulation.Type1Adult;
}

/// <summary>
/// Request model for data sufficiency assessment
/// </summary>
public class DataSufficiencyRequest
{
    /// <summary>
    /// Collection of sensor glucose readings
    /// </summary>
    public IEnumerable<SensorGlucose> Entries { get; set; } = Enumerable.Empty<SensorGlucose>();

    /// <summary>
    /// Number of days to assess (default: 14)
    /// </summary>
    public int Days { get; set; } = 14;

    /// <summary>
    /// Expected readings per day based on sensor type (default: 288 for 5-minute intervals)
    /// </summary>
    public int ExpectedReadingsPerDay { get; set; } = 288;
}

/// <summary>
/// Request model for site change impact analysis
/// </summary>
public class SiteChangeImpactRequest
{
    /// <summary>
    /// Collection of sensor glucose readings
    /// </summary>
    public IEnumerable<SensorGlucose> Entries { get; set; } = Enumerable.Empty<SensorGlucose>();

    /// <summary>
    /// Collection of device events (must include site changes)
    /// </summary>
    public IEnumerable<DeviceEvent> DeviceEvents { get; set; } = Enumerable.Empty<DeviceEvent>();

    /// <summary>
    /// Hours before site change to analyze (default: 12)
    /// </summary>
    public int HoursBeforeChange { get; set; } = 12;

    /// <summary>
    /// Hours after site change to analyze (default: 24)
    /// </summary>
    public int HoursAfterChange { get; set; } = 24;

    /// <summary>
    /// Time bucket size for averaging in minutes (default: 30)
    /// </summary>
    public int BucketSizeMinutes { get; set; } = 30;
}
