using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nocturne.Core.Contracts.Analytics;
using Nocturne.Core.Contracts.Profiles;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Cache.Abstractions;
using OpenApi.Remote.Attributes;

namespace Nocturne.API.Controllers.V4.Analytics;

/// <summary>
/// Controller for comprehensive glucose and treatment statistics.
/// Provides endpoints for calculating various glucose metrics and analytics including
/// time-in-range, glycemic variability, GMI, GRI, basal/bolus ratios, and AID system metrics.
/// </summary>
/// <remarks>
/// Several computation endpoints accept large payloads (up to 100 MB) to accommodate
/// 90-day datasets. These are decorated with <c>[RequestSizeLimit(100 * 1024 * 1024)]</c>.
///
/// <c>GET /periods</c> and <c>GET /basal-analysis</c> fetch data directly from the database
/// using the injected V4 repositories, apply profile-based scheduled-basal fallback when no
/// TempBasal records exist, and cache results for 5 minutes to absorb rapid dashboard refreshes.
///
/// DbContext is not thread-safe; all repository queries within a single request are executed
/// sequentially.
/// </remarks>
/// <seealso cref="IStatisticsService"/>
/// <seealso cref="ISensorGlucoseRepository"/>
/// <seealso cref="IBolusRepository"/>
/// <seealso cref="ICarbIntakeRepository"/>
/// <seealso cref="ITempBasalRepository"/>
/// <seealso cref="IAidMetricsService"/>
[ApiController]
[Route("api/v4/[controller]")]
[Produces("application/json")]
[Authorize]
public class StatisticsController : ControllerBase
{
    private readonly IStatisticsService _statisticsService;
    private readonly ICacheService _cacheService;
    private readonly IProfileProjectionService _profileProjectionService;
    private readonly IBasalRateResolver _basalRateResolver;
    private readonly ITherapySettingsResolver _therapySettingsResolver;
    private readonly ISensorGlucoseRepository _sensorGlucoseRepository;
    private readonly IBolusRepository _bolusRepository;
    private readonly ICarbIntakeRepository _carbIntakeRepository;
    private readonly ITempBasalRepository _tempBasalRepository;
    private readonly ITenantAccessor _tenantAccessor;
    private readonly IAidMetricsService _aidMetricsService;
    private readonly IPatientDeviceRepository _patientDeviceRepository;
    private readonly IApsSnapshotRepository _apsSnapshotRepository;
    private readonly IDeviceEventRepository _deviceEventRepository;
    private readonly ITargetRangeScheduleRepository _targetRangeScheduleRepository;

    private string TenantCacheId =>
        _tenantAccessor.Context?.TenantId.ToString()
        ?? throw new InvalidOperationException("Tenant context is not resolved");

    public StatisticsController(
        IStatisticsService statisticsService,
        ICacheService cacheService,
        IProfileProjectionService profileProjectionService,
        IBasalRateResolver basalRateResolver,
        ITherapySettingsResolver therapySettingsResolver,
        ISensorGlucoseRepository sensorGlucoseRepository,
        IBolusRepository bolusRepository,
        ICarbIntakeRepository carbIntakeRepository,
        ITempBasalRepository tempBasalRepository,
        ITenantAccessor tenantAccessor,
        IAidMetricsService aidMetricsService,
        IPatientDeviceRepository patientDeviceRepository,
        IApsSnapshotRepository apsSnapshotRepository,
        IDeviceEventRepository deviceEventRepository,
        ITargetRangeScheduleRepository targetRangeScheduleRepository
    )
    {
        _statisticsService = statisticsService;
        _cacheService = cacheService;
        _profileProjectionService = profileProjectionService;
        _basalRateResolver = basalRateResolver;
        _therapySettingsResolver = therapySettingsResolver;
        _sensorGlucoseRepository = sensorGlucoseRepository;
        _bolusRepository = bolusRepository;
        _carbIntakeRepository = carbIntakeRepository;
        _tempBasalRepository = tempBasalRepository;
        _tenantAccessor = tenantAccessor;
        _aidMetricsService = aidMetricsService;
        _patientDeviceRepository = patientDeviceRepository;
        _apsSnapshotRepository = apsSnapshotRepository;
        _deviceEventRepository = deviceEventRepository;
        _targetRangeScheduleRepository = targetRangeScheduleRepository;
    }

    /// <summary>
    /// Calculate basic glucose statistics from provided glucose values
    /// </summary>
    /// <param name="values">Array of glucose values in mg/dL</param>
    /// <returns>Basic glucose statistics including mean, median, percentiles, etc.</returns>
    [HttpPost("basic-stats")]
    public ActionResult<BasicGlucoseStats> CalculateBasicStats([FromBody] double[] values)
    {
        try
        {
            var result = _statisticsService.CalculateBasicStats(values);
            return Ok(result);
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Calculate comprehensive glycemic variability metrics
    /// </summary>
    /// <param name="request">Request containing glucose values and entries</param>
    /// <returns>Comprehensive glycemic variability metrics</returns>
    [HttpPost("glycemic-variability")]
    public ActionResult<GlycemicVariability> CalculateGlycemicVariability(
        [FromBody] GlycemicVariabilityRequest request
    )
    {
        try
        {
            var result = _statisticsService.CalculateGlycemicVariability(
                request.Values,
                request.Entries
            );
            return Ok(result);
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Calculate time in range metrics
    /// </summary>
    /// <param name="request">Request containing entries and optional thresholds</param>
    /// <returns>Time in range metrics including percentages, durations, and episodes</returns>
    [HttpPost("time-in-range")]
    [RemoteQuery]
    [RequestSizeLimit(100 * 1024 * 1024)] // 100 MB
    public ActionResult<TimeInRangeMetrics> CalculateTimeInRange(
        [FromBody] TimeInRangeRequest request
    )
    {
        try
        {
            var result = _statisticsService.CalculateTimeInRange(
                request.Entries,
                request.Thresholds
            );
            return Ok(result);
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Calculate glucose distribution across configurable bins
    /// </summary>
    /// <param name="request">Request containing entries and optional bins</param>
    /// <returns>Collection of distribution data points</returns>
    [HttpPost("glucose-distribution")]
    [RequestSizeLimit(100 * 1024 * 1024)] // 100 MB
    public ActionResult<IEnumerable<DistributionDataPoint>> CalculateGlucoseDistribution(
        [FromBody] GlucoseDistributionRequest request
    )
    {
        try
        {
            var result = _statisticsService.CalculateGlucoseDistribution(
                request.Entries,
                request.Bins
            );
            return Ok(result);
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Calculate averaged statistics for each hour of the day (0-23)
    /// </summary>
    /// <param name="entries">Array of sensor glucose readings</param>
    /// <returns>Collection of averaged statistics for each hour</returns>
    [HttpPost("averaged-stats")]
    [RemoteQuery]
    [RequestSizeLimit(100 * 1024 * 1024)] // 100 MB
    public ActionResult<IEnumerable<AveragedStats>> CalculateAveragedStats(
        [FromBody] SensorGlucose[] entries
    )
    {
        try
        {
            var result = _statisticsService.CalculateAveragedStats(entries);
            return Ok(result);
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Calculate treatment summary for a collection of boluses and carb intakes
    /// </summary>
    /// <param name="request">Request containing boluses and carb intakes</param>
    /// <returns>Treatment summary with totals and counts</returns>
    [HttpPost("treatment-summary")]
    public ActionResult<TreatmentSummary> CalculateTreatmentSummary(
        [FromBody] TreatmentSummaryRequest request
    )
    {
        try
        {
            var result = _statisticsService.CalculateTreatmentSummary(
                request.Boluses ?? Enumerable.Empty<Bolus>(),
                request.CarbIntakes ?? Enumerable.Empty<CarbIntake>()
            );
            return Ok(result);
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Calculate overall averages across multiple days
    /// </summary>
    /// <param name="dailyDataPoints">Array of daily data points</param>
    /// <returns>Overall averages or null if no data</returns>
    [HttpPost("overall-averages")]
    public ActionResult<OverallAverages> CalculateOverallAverages(
        [FromBody] DayData[] dailyDataPoints
    )
    {
        try
        {
            var result = _statisticsService.CalculateOverallAverages(dailyDataPoints);
            if (result == null)
            {
                return NoContent();
            }
            return Ok(result);
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Master glucose analytics function that calculates comprehensive metrics
    /// </summary>
    /// <param name="request">Request containing sensor glucose readings, boluses, carb intakes, and configuration</param>
    /// <returns>Comprehensive glucose analytics</returns>
    [HttpPost("comprehensive-analytics")]
    [RequestSizeLimit(100 * 1024 * 1024)] // 100 MB
    public ActionResult<GlucoseAnalytics> AnalyzeGlucoseData(
        [FromBody] GlucoseAnalyticsRequest request
    )
    {
        try
        {
            var result = _statisticsService.AnalyzeGlucoseData(
                request.Entries,
                request.Boluses ?? Enumerable.Empty<Bolus>(),
                request.CarbIntakes ?? Enumerable.Empty<CarbIntake>(),
                request.Config
            );
            return Ok(result);
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Extended glucose analytics including GMI, GRI, and clinical target assessment
    /// </summary>
    /// <param name="request">Request containing sensor glucose readings, boluses, carb intakes, population type, and configuration</param>
    /// <returns>Extended glucose analytics with modern clinical metrics</returns>
    [HttpPost("extended-analytics")]
    [RequestSizeLimit(100 * 1024 * 1024)] // 100 MB - needed for large datasets (90+ days)
    public ActionResult<ExtendedGlucoseAnalytics> AnalyzeGlucoseDataExtended(
        [FromBody] ExtendedGlucoseAnalyticsRequest request
    )
    {
        try
        {
            var result = _statisticsService.AnalyzeGlucoseDataExtended(
                request.Entries,
                request.Boluses ?? Enumerable.Empty<Bolus>(),
                request.CarbIntakes ?? Enumerable.Empty<CarbIntake>(),
                request.Population,
                request.Config
            );
            return Ok(result);
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Calculate Glucose Management Indicator (GMI)
    /// </summary>
    /// <param name="meanGlucose">Mean glucose in mg/dL</param>
    /// <returns>GMI with value and interpretation</returns>
    [HttpGet("gmi/{meanGlucose:double}")]
    public ActionResult<GlucoseManagementIndicator> CalculateGMI(double meanGlucose)
    {
        try
        {
            var result = _statisticsService.CalculateGMI(meanGlucose);
            return Ok(result);
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Calculate Glycemic Risk Index (GRI) from time in range metrics
    /// </summary>
    /// <param name="timeInRange">Time in range metrics</param>
    /// <returns>GRI with score, zone, and interpretation</returns>
    [HttpPost("gri")]
    public ActionResult<GlycemicRiskIndex> CalculateGRI([FromBody] TimeInRangeMetrics timeInRange)
    {
        try
        {
            var result = _statisticsService.CalculateGRI(timeInRange);
            return Ok(result);
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Assess glucose data against clinical targets for a specific population
    /// </summary>
    /// <param name="request">Request containing analytics and population type</param>
    /// <returns>Clinical target assessment with actionable insights</returns>
    [HttpPost("clinical-assessment")]
    public ActionResult<ClinicalTargetAssessment> AssessAgainstTargets(
        [FromBody] ClinicalAssessmentRequest request
    )
    {
        try
        {
            var result = _statisticsService.AssessAgainstTargets(
                request.Analytics,
                request.Population
            );
            return Ok(result);
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Assess data sufficiency for a valid clinical report
    /// </summary>
    /// <param name="request">Request containing entries and optional period settings</param>
    /// <returns>Data sufficiency assessment</returns>
    [HttpPost("data-sufficiency")]
    public ActionResult<DataSufficiencyAssessment> AssessDataSufficiency(
        [FromBody] DataSufficiencyRequest request
    )
    {
        try
        {
            var result = _statisticsService.AssessDataSufficiency(
                request.Entries,
                request.Days,
                request.ExpectedReadingsPerDay
            );
            return Ok(result);
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Get clinical targets for a specific diabetes population
    /// </summary>
    /// <param name="population">Population type (Type1Adult, Type2Adult, Elderly, Pregnancy, etc.)</param>
    /// <returns>Clinical targets for the specified population</returns>
    [HttpGet("clinical-targets/{population}")]
    public ActionResult<ClinicalTargets> GetClinicalTargets(DiabetesPopulation population)
    {
        try
        {
            var result = ClinicalTargets.ForPopulation(population);
            return Ok(result);
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Calculate estimated A1C from average glucose
    /// </summary>
    /// <param name="averageGlucose">Average glucose in mg/dL</param>
    /// <returns>Estimated A1C percentage</returns>
    [HttpGet("estimated-a1c/{averageGlucose:double}")]
    public ActionResult<double> CalculateEstimatedA1C(double averageGlucose)
    {
        try
        {
            var result = _statisticsService.CalculateEstimatedA1C(averageGlucose);
            return Ok(result);
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Convert mg/dL to mmol/L
    /// </summary>
    /// <param name="mgdl">Glucose value in mg/dL</param>
    /// <returns>Glucose value in mmol/L</returns>
    [HttpGet("convert/mgdl-to-mmol/{mgdl:double}")]
    public ActionResult<double> MgdlToMMOL(double mgdl)
    {
        try
        {
            var result = _statisticsService.MgdlToMMOL(mgdl);
            return Ok(result);
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Convert mmol/L to mg/dL
    /// </summary>
    /// <param name="mmol">Glucose value in mmol/L</param>
    /// <returns>Glucose value in mg/dL</returns>
    [HttpGet("convert/mmol-to-mgdl/{mmol:double}")]
    public ActionResult<double> MmolToMGDL(double mmol)
    {
        try
        {
            var result = _statisticsService.MmolToMGDL(mmol);
            return Ok(result);
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Format insulin value for display
    /// </summary>
    /// <param name="value">Insulin value</param>
    /// <returns>Formatted insulin string</returns>
    [HttpGet("format/insulin/{value:double}")]
    public ActionResult<string> FormatInsulinDisplay(double value)
    {
        try
        {
            var result = _statisticsService.FormatInsulinDisplay(value);
            return Ok(result);
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Format carb value for display
    /// </summary>
    /// <param name="value">Carb value</param>
    /// <returns>Formatted carb string</returns>
    [HttpGet("format/carb/{value:double}")]
    public ActionResult<string> FormatCarbDisplay(double value)
    {
        try
        {
            var result = _statisticsService.FormatCarbDisplay(value);
            return Ok(result);
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Validate treatment data for completeness and consistency
    /// </summary>
    /// <param name="treatment">Treatment to validate</param>
    /// <returns>True if treatment data is valid</returns>
    [HttpPost("validate/treatment")]
    public ActionResult<bool> ValidateTreatmentData([FromBody] Treatment treatment)
    {
        try
        {
            var result = _statisticsService.ValidateTreatmentData(treatment);
            return Ok(result);
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Clean and filter treatment data
    /// </summary>
    /// <param name="treatments">Array of treatments to clean</param>
    /// <returns>Cleaned collection of treatments</returns>
    [HttpPost("clean/treatments")]
    public ActionResult<IEnumerable<Treatment>> CleanTreatmentData(
        [FromBody] Treatment[] treatments
    )
    {
        try
        {
            var result = _statisticsService.CleanTreatmentData(treatments);
            return Ok(result);
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Gets comprehensive statistics for multiple time periods (1, 3, 7, 30, and 90 days).
    /// Fetches sensor glucose, bolus, carb, and temp-basal data from the database for each period,
    /// computes <see cref="GlucoseAnalytics"/>, <see cref="TreatmentSummary"/>, and
    /// <see cref="InsulinDeliveryStatistics"/>, and caches the result for 5 minutes.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="MultiPeriodStatistics"/> containing a <see cref="PeriodStatistics"/>
    /// entry for each of the five standard periods.</returns>
    /// <remarks>
    /// When no TempBasal or algorithm bolus records are found but a profile is loaded, the method
    /// falls back to computing scheduled basal from the active profile schedule via <see cref="IBasalRateResolver"/>.
    /// GMI reliability is assessed per-period using context-appropriate recommended-day minimums
    /// (e.g., 1-day periods cannot require 14 days of data).
    /// </remarks>
    [HttpGet("periods")]
    [RemoteQuery]
    public async Task<ActionResult<MultiPeriodStatistics>> GetMultiPeriodStatistics(
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            var cacheKey = $"statistics:multi-period:{TenantCacheId}";

            // Try to get from cache first
            var cachedResult = await _cacheService.GetAsync<MultiPeriodStatistics>(
                cacheKey,
                cancellationToken
            );
            if (cachedResult != null)
            {
                return Ok(cachedResult);
            }

            // Check if profile data exists for scheduled basal calculation
            var hasProfileData = await _therapySettingsResolver.HasDataAsync(cancellationToken);

            // Calculate statistics for each period
            var periods = new[] { 1, 3, 7, 30, 90 };
            var now = DateTime.UtcNow;
            var result = new MultiPeriodStatistics { LastUpdated = now };

            // Calculate statistics for each period sequentially to avoid DbContext threading issues
            // DbContext is not thread-safe, so we cannot run multiple queries in parallel
            var periodResults = new List<(int Days, PeriodStatistics Statistics)>();

            foreach (var days in periods)
            {
                var startDate = now.AddDays(-days);
                var endDate = now;

                var startTimestamp = startDate;
                var endTimestamp = endDate;

                // Fetch v4 data for this period (sequential — DbContext is not thread-safe)
                var sensorGlucoseData = await _sensorGlucoseRepository.GetAsync(
                    from: (DateTime?)startTimestamp,
                    to: (DateTime?)endTimestamp,
                    device: null,
                    source: null,
                    limit: 10000,
                    descending: false,
                    ct: cancellationToken
                );
                var filteredEntries = sensorGlucoseData.ToList();

                var bolusData = await _bolusRepository.GetAsync(
                    from: (DateTime?)startTimestamp,
                    to: (DateTime?)endTimestamp,
                    device: null,
                    source: null,
                    limit: 10000,
                    descending: false,
                    kind: BolusKind.Manual,
                    ct: cancellationToken
                );
                var filteredBoluses = bolusData.ToList();

                var carbData = await _carbIntakeRepository.GetAsync(
                    from: (DateTime?)startTimestamp,
                    to: (DateTime?)endTimestamp,
                    device: null,
                    source: null,
                    limit: 10000,
                    descending: false,
                    ct: cancellationToken
                );
                var filteredCarbs = carbData.ToList();

                // Calculate analytics if we have sufficient data
                GlucoseAnalytics? analytics = null;
                TreatmentSummary? treatmentSummary = null;
                InsulinDeliveryStatistics? insulinDelivery = null;
                bool hasSufficientData = filteredEntries.Count >= 10; // Minimum 10 readings

                if (hasSufficientData)
                {
                    analytics = _statisticsService.AnalyzeGlucoseData(
                        filteredEntries,
                        filteredBoluses,
                        filteredCarbs
                    );

                    treatmentSummary = _statisticsService.CalculateTreatmentSummary(
                        filteredBoluses,
                        filteredCarbs
                    );

                    // Fetch TempBasals and algorithm boluses for basal data
                    // Deduplicate by 30s window + rate to eliminate duplicates from multiple connectors
                    var tempBasals = (
                        await _tempBasalRepository.GetAsync(
                            from: startTimestamp,
                            to: endTimestamp,
                            device: null,
                            source: null,
                            limit: 10000,
                            descending: false,
                            ct: cancellationToken
                        )
                    ).ToList();

                    var algorithmBoluses = (
                        await _bolusRepository.GetAsync(
                            from: startTimestamp,
                            to: endTimestamp,
                            device: null,
                            source: null,
                            limit: 10000,
                            descending: false,
                            kind: BolusKind.Algorithm,
                            ct: cancellationToken
                        )
                    ).ToList();

                    insulinDelivery = _statisticsService.CalculateInsulinDeliveryStatistics(
                        filteredBoluses,
                        algorithmBoluses,
                        tempBasals,
                        filteredCarbs,
                        startDate,
                        endDate
                    );

                    // If no TempBasals/algorithm boluses but we have profile data, augment with scheduled basal
                    if (
                        tempBasals.Count == 0
                        && algorithmBoluses.Count == 0
                        && hasProfileData
                    )
                    {
                        var profileBasal = await CalculateScheduledBasalForPeriodAsync(
                            startTimestamp,
                            endTimestamp,
                            cancellationToken
                        );
                        var totalWithProfile = insulinDelivery.TotalBolus + profileBasal;
                        insulinDelivery.TotalBasal = Math.Round(profileBasal * 100) / 100;
                        insulinDelivery.ScheduledBasal = Math.Round(profileBasal * 100) / 100;
                        insulinDelivery.AdditionalBasal = 0;
                        insulinDelivery.TotalInsulin = Math.Round(totalWithProfile * 100) / 100;
                        insulinDelivery.Tdd =
                            Math.Round(
                                totalWithProfile / Math.Max(1, insulinDelivery.DayCount) * 10
                            ) / 10;
                        insulinDelivery.BasalPercent =
                            totalWithProfile > 0
                                ? Math.Round(profileBasal / totalWithProfile * 100 * 10) / 10
                                : 0;
                        insulinDelivery.BolusPercent =
                            totalWithProfile > 0
                                ? Math.Round(
                                    insulinDelivery.TotalBolus / totalWithProfile * 100 * 10
                                ) / 10
                                : 0;
                    }

                    // Keep treatment summary basal consistent
                    treatmentSummary.Totals.Insulin.Basal = insulinDelivery.TotalBasal;
                    treatmentSummary.Totals.Insulin.ScheduledBasal = insulinDelivery.ScheduledBasal;
                    treatmentSummary.Totals.Insulin.AdditionalBasal =
                        insulinDelivery.AdditionalBasal;
                }

                // Compute GMI and reliability for this period
                GlucoseManagementIndicator? periodGmi = null;
                StatisticReliability? periodReliability = null;

                if (hasSufficientData && analytics != null)
                {
                    periodGmi = _statisticsService.CalculateGMI(analytics.BasicStats.Mean);

                    var actualDaysWithData = filteredEntries
                        .Where(e => e.Mills > 0)
                        .Select(e => DateTimeOffset.FromUnixTimeMilliseconds(e.Mills).Date)
                        .Distinct()
                        .Count();

                    // Context-appropriate recommended minimums:
                    // Short periods can't reasonably need 14 days
                    var recommendedDays = days switch
                    {
                        <= 3 => days,
                        <= 7 => 7,
                        _ => 14, // clinical standard for 30/90 day periods
                    };

                    periodReliability = _statisticsService.AssessReliability(
                        actualDaysWithData,
                        filteredEntries.Count,
                        recommendedDays
                    );

                    periodGmi.Reliability = periodReliability;
                }

                periodResults.Add(
                    (
                        days,
                        new PeriodStatistics
                        {
                            PeriodDays = days,
                            StartDate = startDate,
                            EndDate = endDate,
                            Analytics = analytics,
                            TreatmentSummary = treatmentSummary,
                            InsulinDelivery = insulinDelivery,
                            HasSufficientData = hasSufficientData,
                            Gmi = periodGmi,
                            Reliability = periodReliability,
                            EntryCount = filteredEntries.Count,
                            TreatmentCount = filteredBoluses.Count + filteredCarbs.Count,
                        }
                    )
                );
            }

            // Map results to the response object
            foreach (var periodResult in periodResults)
            {
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

            // Cache for 5 minutes — long enough to absorb rapid dashboard refreshes,
            // short enough that newly-imported connector data (basal StateSpans, etc.) appears promptly.
            var expiry = DateTime.UtcNow.AddMinutes(5);
            await _cacheService.SetAsync(cacheKey, result, expiry, cancellationToken);

            return Ok(result);
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Calculate total scheduled basal delivery for a time period based on profile basal schedule,
    /// accounting for any temporary basal treatments that override the scheduled rate.
    /// </summary>
    /// <param name="startTimestamp">Start time as DateTime (UTC)</param>
    /// <param name="endTimestamp">End time as DateTime (UTC)</param>
    /// <returns>Total scheduled basal insulin in units</returns>
    private async Task<double> CalculateScheduledBasalForPeriodAsync(
        DateTime startTimestamp,
        DateTime endTimestamp,
        CancellationToken cancellationToken = default)
    {
        double totalBasal = 0.0;

        // Sample at 5-minute intervals (same as CGM readings)
        // In v4, temp basal adjustments come from StateSpans (handled by the primary code path).
        // This fallback uses only the profile schedule when no StateSpans exist.
        const long intervalMs = 5 * 60 * 1000; // 5 minutes in milliseconds
        var startMills = new DateTimeOffset(startTimestamp, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var endMills = new DateTimeOffset(endTimestamp, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var currentTime = startMills;

        while (currentTime < endMills)
        {
            var scheduledRate = await _basalRateResolver.GetBasalRateAsync(currentTime, ct: cancellationToken);
            var insulinDelivered = scheduledRate * (5.0 / 60.0); // 5 minutes = 5/60 hours
            totalBasal += insulinDelivered;
            currentTime += intervalMs;
        }

        return Math.Round(totalBasal * 100) / 100;
    }

    /// <summary>
    /// Analyze glucose patterns around site changes to identify impact of site age on control
    /// </summary>
    /// <param name="request">Request containing sensor glucose readings, device events, and analysis parameters</param>
    /// <returns>Site change impact analysis with averaged glucose patterns</returns>
    [HttpPost("site-change-impact")]
    [RequestSizeLimit(100 * 1024 * 1024)] // 100 MB
    public ActionResult<SiteChangeImpactAnalysis> CalculateSiteChangeImpact(
        [FromBody] SiteChangeImpactRequest request
    )
    {
        try
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
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Calculate daily basal/bolus ratio statistics for a date range
    /// </summary>
    /// <param name="startDate">Start date of the analysis period</param>
    /// <param name="endDate">End date of the analysis period</param>
    /// <returns>Daily basal/bolus ratio breakdown with averages</returns>
    [HttpGet("daily-basal-bolus-ratios")]
    [RemoteQuery]
    public async Task<ActionResult<DailyBasalBolusRatioResponse>> GetDailyBasalBolusRatios(
        [FromQuery] DateTime startDate,
        [FromQuery] DateTime endDate
    )
    {
        try
        {
            var startDt = DateTime.SpecifyKind(startDate, DateTimeKind.Utc);
            var endDt = DateTime.SpecifyKind(endDate, DateTimeKind.Utc);

            // Fetch manual boluses and basal data sequentially
            // (DbContext is not thread-safe, can't use Task.WhenAll)
            var boluses = await _bolusRepository.GetAsync(
                from: startDt,
                to: endDt,
                device: null,
                source: null,
                limit: 10000,
                descending: false,
                kind: BolusKind.Manual
            );

            var tempBasals = (
                await _tempBasalRepository.GetAsync(
                    from: startDt,
                    to: endDt,
                    device: null,
                    source: null,
                    limit: 10000,
                    descending: false
                )
            ).ToList();

            var algorithmBoluses = await _bolusRepository.GetAsync(
                from: startDt,
                to: endDt,
                device: null,
                source: null,
                limit: 10000,
                descending: false,
                kind: BolusKind.Algorithm
            );

            var tzId = await _therapySettingsResolver.GetTimezoneAsync();
            var tz = !string.IsNullOrEmpty(tzId)
                ? TimeZoneHelper.GetTimeZoneInfoFromId(tzId)
                : TimeZoneInfo.Utc;

            var result = _statisticsService.CalculateDailyBasalBolusRatios(
                boluses,
                algorithmBoluses,
                tempBasals,
                tz
            );
            return Ok(result);
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Calculate comprehensive insulin delivery statistics for a date range
    /// </summary>
    /// <param name="startDate">Start date of the analysis period</param>
    /// <param name="endDate">End date of the analysis period</param>
    /// <returns>Comprehensive insulin delivery statistics</returns>
    [HttpGet("insulin-delivery-stats")]
    [RemoteQuery]
    public async Task<ActionResult<InsulinDeliveryStatistics>> GetInsulinDeliveryStatistics(
        [FromQuery] DateTime startDate,
        [FromQuery] DateTime endDate
    )
    {
        try
        {
            var startDt = DateTime.SpecifyKind(startDate, DateTimeKind.Utc);
            var endDt = DateTime.SpecifyKind(endDate, DateTimeKind.Utc);

            // Fetch manual boluses, TempBasals, algorithm boluses, and carbs
            var boluses = await _bolusRepository.GetAsync(
                from: startDt,
                to: endDt,
                device: null,
                source: null,
                limit: 10000,
                descending: false,
                kind: BolusKind.Manual
            );

            var tempBasals = (
                await _tempBasalRepository.GetAsync(
                    from: startDt,
                    to: endDt,
                    device: null,
                    source: null,
                    limit: 10000,
                    descending: false
                )
            ).ToList();

            var algorithmBoluses = await _bolusRepository.GetAsync(
                from: startDt,
                to: endDt,
                device: null,
                source: null,
                limit: 10000,
                descending: false,
                kind: BolusKind.Algorithm
            );

            var carbs = await _carbIntakeRepository.GetAsync(
                from: startDt,
                to: endDt,
                device: null,
                source: null,
                limit: 10000,
                descending: false
            );

            // Fill in ScheduledRate on temp basals that don't have it
            // (e.g. MyLife/CamAPS algorithm adjustments)
            foreach (var tb in tempBasals)
            {
                if (!tb.ScheduledRate.HasValue && tb.Origin != TempBasalOrigin.Scheduled)
                {
                    tb.ScheduledRate = await _basalRateResolver.GetBasalRateAsync(tb.StartMills);
                }
            }

            var result = _statisticsService.CalculateInsulinDeliveryStatistics(
                boluses,
                algorithmBoluses,
                tempBasals,
                carbs,
                startDate,
                endDate
            );
            return Ok(result);
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Calculate comprehensive basal analysis statistics for a date range
    /// </summary>
    /// <param name="startDate">Start date of the analysis period</param>
    /// <param name="endDate">End date of the analysis period</param>
    /// <returns>Comprehensive basal analysis with stats, temp basal info, and hourly percentiles</returns>
    [HttpGet("basal-analysis")]
    [RemoteQuery]
    public async Task<ActionResult<BasalAnalysisResponse>> GetBasalAnalysis(
        [FromQuery] DateTime? startDate = null,
        [FromQuery] DateTime? endDate = null
    )
    {
        try
        {
            if (startDate is null || endDate is null)
                return BadRequest(new { error = "startDate and endDate are required." });

            // Force UTC kind to avoid DateTimeOffset throwing when the server's local
            // timezone offset would push DateTime.MinValue/MaxValue out of the valid range.
            var startUtc = DateTime.SpecifyKind(startDate.Value, DateTimeKind.Utc);
            var endUtc = DateTime.SpecifyKind(endDate.Value, DateTimeKind.Utc);

            // Fetch TempBasals and algorithm boluses
            // Deduplicate by 30s window + rate to eliminate duplicates from multiple connectors
            var tempBasals = (
                await _tempBasalRepository.GetAsync(
                    from: (DateTime?)startUtc,
                    to: (DateTime?)endUtc,
                    device: null,
                    source: null,
                    limit: 10000,
                    descending: false
                )
            ).ToList();

            var algorithmBoluses = await _bolusRepository.GetAsync(
                from: (DateTime?)startUtc,
                to: (DateTime?)endUtc,
                device: null,
                source: null,
                limit: 10000,
                descending: false,
                kind: BolusKind.Algorithm
            );

            // Fall back to profile-based scheduled rates when no TempBasals exist.
            // This matches ChartDataService.BuildBasalSeriesFromStateSpans which also
            // falls back to BuildBasalSeriesFromProfile, keeping both charts consistent.
            if (tempBasals.Count == 0 && await _therapySettingsResolver.HasDataAsync())
            {
                for (var day = startUtc.Date; day <= endUtc.Date; day = day.AddDays(1))
                {
                    for (int hour = 0; hour < 24; hour++)
                    {
                        var hourStart = day.AddHours(hour);
                        if (hourStart < startUtc || hourStart >= endUtc)
                            continue;

                        var hourMills = new DateTimeOffset(
                            hourStart,
                            TimeSpan.Zero
                        ).ToUnixTimeMilliseconds();
                        var rate = await _basalRateResolver.GetBasalRateAsync(hourMills);
                        tempBasals.Add(
                            new TempBasal
                            {
                                StartTimestamp = hourStart,
                                EndTimestamp = hourStart.AddHours(1),
                                Rate = rate,
                                Origin = TempBasalOrigin.Scheduled,
                            }
                        );
                    }
                }
            }

            var result = _statisticsService.CalculateBasalAnalysis(
                tempBasals,
                algorithmBoluses,
                startUtc,
                endUtc
            );
            return Ok(result);
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Calculates AID (Automated Insulin Delivery) system metrics for a date range.
    /// Uses patient device records to segment the period by algorithm and compute time-weighted metrics
    /// via <see cref="IAidMetricsService"/>.
    /// </summary>
    /// <param name="startDate">Inclusive start of the analysis period (UTC).</param>
    /// <param name="endDate">Inclusive end of the analysis period (UTC).</param>
    /// <returns>An <see cref="AidSystemMetrics"/> object containing loop-on time, site-change counts,
    /// CGM active percent, and per-algorithm segment breakdowns.</returns>
    /// <remarks>
    /// Fetches APS snapshots, temp basals, device events, glucose readings, and target-range schedules
    /// from their respective repositories. CGM metrics are derived from <see cref="IStatisticsService.AnalyzeGlucoseData"/>.
    /// Target range is optional; the method continues without it if the repository throws.
    /// </remarks>
    [HttpGet("aid-system-metrics")]
    [RemoteQuery]
    public async Task<ActionResult<AidSystemMetrics>> GetAidSystemMetrics(
        [FromQuery] DateTime startDate,
        [FromQuery] DateTime endDate
    )
    {
        try
        {
            var startDt = DateTime.SpecifyKind(startDate, DateTimeKind.Utc);
            var endDt = DateTime.SpecifyKind(endDate, DateTimeKind.Utc);

            // Fetch patient devices overlapping the date range
            var devices = await _patientDeviceRepository.GetByDateRangeAsync(startDt, endDt);

            // Map patient devices to segment inputs
            var deviceSegments = devices
                .Where(d =>
                    d.DeviceCategory == DeviceCategory.InsulinPump && d.AidAlgorithm.HasValue
                )
                .Select(d => new DeviceSegmentInput
                {
                    Algorithm = d.AidAlgorithm!.Value,
                    StartDate = d.StartDate.HasValue
                        ? d.StartDate.Value.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)
                        : startDt,
                    EndDate = d.EndDate.HasValue
                        ? d.EndDate.Value.ToDateTime(new TimeOnly(23, 59, 59), DateTimeKind.Utc)
                        : endDt,
                })
                .ToList();

            // Fetch data sequentially (DbContext is not thread-safe)
            var apsSnapshots = (
                await _apsSnapshotRepository.GetAsync(
                    from: startDt,
                    to: endDt,
                    device: null,
                    source: null,
                    limit: 50000,
                    descending: false
                )
            ).ToList();

            var tempBasals = (
                await _tempBasalRepository.GetAsync(
                    from: startDt,
                    to: endDt,
                    device: null,
                    source: null,
                    limit: 50000,
                    descending: false
                )
            ).ToList();

            var deviceEvents = (
                await _deviceEventRepository.GetAsync(
                    from: startDt,
                    to: endDt,
                    device: null,
                    source: null,
                    limit: 10000,
                    descending: false
                )
            ).ToList();

            var glucose = (
                await _sensorGlucoseRepository.GetAsync(
                    from: startDt,
                    to: endDt,
                    device: null,
                    source: null,
                    limit: 50000,
                    descending: false
                )
            ).ToList();

            // Count site changes
            var siteChangeCount = deviceEvents.Count(e =>
                e.EventType == DeviceEventType.SiteChange
            );

            // Calculate CGM metrics using existing statistics service
            double? cgmUsePercent = null;
            double? cgmActivePercent = null;
            if (glucose.Count > 0)
            {
                var analytics = _statisticsService.AnalyzeGlucoseData(
                    glucose,
                    Enumerable.Empty<Bolus>(),
                    Enumerable.Empty<CarbIntake>()
                );
                cgmUsePercent = analytics.DataQuality.CgmActivePercent;
                cgmActivePercent = analytics.DataQuality.DataCompleteness;
            }

            // Get target range from target range schedule repository
            double? targetLow = null;
            double? targetHigh = null;
            try
            {
                var targetSchedules = await _targetRangeScheduleRepository.GetAsync(
                    from: null,
                    to: null,
                    device: null,
                    source: null,
                    limit: 10,
                    descending: true
                );
                var firstSchedule = targetSchedules.FirstOrDefault();
                if (firstSchedule?.Entries.Count > 0)
                {
                    targetLow = firstSchedule.Entries.Min(e => e.Low);
                    targetHigh = firstSchedule.Entries.Max(e => e.High);
                }
            }
            catch
            {
                // Target range is optional — continue without it
            }

            var result = _aidMetricsService.Calculate(
                deviceSegments,
                apsSnapshots,
                tempBasals,
                siteChangeCount,
                cgmUsePercent,
                cgmActivePercent,
                targetLow,
                targetHigh,
                startDt,
                endDt
            );

            return Ok(result);
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
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
