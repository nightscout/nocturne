using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Attributes;
using Nocturne.Core.Contracts;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Cache.Abstractions;
using Nocturne.Infrastructure.Data.Abstractions;

namespace Nocturne.API.Controllers;

/// <summary>
/// Controller for comprehensive glucose and treatment statistics
/// Provides endpoints for calculating various glucose metrics and analytics
/// </summary>
[ApiController]
[Route("api/v1/[controller]")]
[Produces("application/json")]
public class StatisticsController : ControllerBase
{
    private readonly IStatisticsService _statisticsService;
    private readonly ICacheService _cacheService;
    private readonly IPostgreSqlService _postgreSqlService;
    private readonly IProfileService _profileService;
    private readonly IStateSpanService _stateSpanService;

    public StatisticsController(
        IStatisticsService statisticsService,
        ICacheService cacheService,
        IPostgreSqlService postgreSqlService,
        IProfileService profileService,
        IStateSpanService stateSpanService
    )
    {
        _statisticsService = statisticsService;
        _cacheService = cacheService;
        _postgreSqlService = postgreSqlService;
        _profileService = profileService;
        _stateSpanService = stateSpanService;
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
    /// <param name="entries">Array of glucose entries</param>
    /// <returns>Collection of averaged statistics for each hour</returns>
    [HttpPost("averaged-stats")]
    [RemoteQuery]
    [RequestSizeLimit(100 * 1024 * 1024)] // 100 MB
    public ActionResult<IEnumerable<AveragedStats>> CalculateAveragedStats(
        [FromBody] Entry[] entries
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
    /// Calculate treatment summary for a collection of treatments
    /// </summary>
    /// <param name="treatments">Array of treatments</param>
    /// <returns>Treatment summary with totals and counts</returns>
    [HttpPost("treatment-summary")]
    public ActionResult<TreatmentSummary> CalculateTreatmentSummary(
        [FromBody] Treatment[] treatments
    )
    {
        try
        {
            var result = _statisticsService.CalculateTreatmentSummary(treatments);
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
    /// <param name="request">Request containing entries, treatments, and configuration</param>
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
                request.Treatments ?? Enumerable.Empty<Treatment>(),
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
    /// <param name="request">Request containing entries, treatments, population type, and configuration</param>
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
                request.Treatments ?? Enumerable.Empty<Treatment>(),
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
    /// Get comprehensive statistics for multiple time periods (1, 3, 7, 30, 90 days)
    /// Uses in-memory cache for performance with daily expiration
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Multi-period statistics with comprehensive analytics for each time period</returns>
    [HttpGet("periods")]
    [RemoteQuery]
    public async Task<ActionResult<MultiPeriodStatistics>> GetMultiPeriodStatistics(
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            const string cacheKey = "statistics:multi-period";

            // Try to get from cache first
            var cachedResult = await _cacheService.GetAsync<MultiPeriodStatistics>(
                cacheKey,
                cancellationToken
            );
            if (cachedResult != null)
            {
                return Ok(cachedResult);
            }

            // Load profile data for scheduled basal calculation
            var profiles = await _postgreSqlService.GetProfilesAsync(
                count: 10,
                skip: 0,
                cancellationToken: cancellationToken
            );
            var profileList = profiles.ToList();
            if (profileList.Any())
            {
                _profileService.LoadData(profileList);
            }

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

                // Get entries for this period
                var entries = await _postgreSqlService.GetEntriesWithAdvancedFilterAsync(
                    type: "sgv",
                    count: 10000, // Large number to get all entries in period
                    dateString: startDate.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                    cancellationToken: cancellationToken
                );

                // Filter entries to the specific period
                var filteredEntries = entries
                    .Where(e => e.Date >= startDate && e.Date <= endDate)
                    .ToList();

                // Get treatments for this period
                var treatments = await _postgreSqlService.GetTreatmentsAsync(
                    count: 10000,
                    cancellationToken: cancellationToken
                );

                // Filter treatments to the specific period using Mills (the canonical timestamp)
                var startTimestamp = ((DateTimeOffset)startDate).ToUnixTimeMilliseconds();
                var endTimestamp = ((DateTimeOffset)endDate).ToUnixTimeMilliseconds();
                var filteredTreatments = treatments
                    .Where(t =>
                        t.Mills > 0
                        && t.Mills >= startTimestamp
                        && t.Mills <= endTimestamp
                    )
                    .ToList();

                // Calculate analytics if we have sufficient data
                GlucoseAnalytics? analytics = null;
                TreatmentSummary? treatmentSummary = null;
                InsulinDeliveryStatistics? insulinDelivery = null;
                bool hasSufficientData = filteredEntries.Count >= 10; // Minimum 10 readings

                if (hasSufficientData)
                {
                    analytics = _statisticsService.AnalyzeGlucoseData(
                        filteredEntries,
                        filteredTreatments
                    );

                    // Calculate treatment summary from treatments
                    treatmentSummary = _statisticsService.CalculateTreatmentSummary(
                        filteredTreatments
                    );

                    // Use StateSpan basal data (actual pump delivery) when available,
                    // falling back to profile-based or treatment-based calculation.
                    // StateSpans represent real delivered insulin and match the
                    // daily-basal-bolus-ratios endpoint so TDD values are consistent.
                    var basalSpans = await _stateSpanService.GetStateSpansAsync(
                        category: StateSpanCategory.BasalDelivery,
                        from: startTimestamp,
                        to: endTimestamp,
                        count: 10000
                    );
                    var basalSpansList = basalSpans.ToList();

                    if (basalSpansList.Count > 0)
                    {
                        insulinDelivery = _statisticsService.CalculateInsulinDeliveryStatistics(
                            filteredTreatments,
                            basalSpansList,
                            startDate,
                            endDate
                        );
                    }
                    else if (_profileService.HasData())
                    {
                        insulinDelivery = _statisticsService.CalculateInsulinDeliveryStatistics(
                            filteredTreatments,
                            startDate,
                            endDate
                        );
                        var profileBasal = CalculateScheduledBasalForPeriod(
                            startTimestamp,
                            endTimestamp,
                            filteredTreatments
                        );
                        var totalWithProfile = insulinDelivery.TotalBolus + profileBasal;
                        insulinDelivery.TotalBasal = Math.Round(profileBasal * 100) / 100;
                        insulinDelivery.TotalInsulin = Math.Round(totalWithProfile * 100) / 100;
                        insulinDelivery.Tdd = Math.Round(totalWithProfile / Math.Max(1, insulinDelivery.DayCount) * 10) / 10;
                        insulinDelivery.BasalPercent = totalWithProfile > 0
                            ? Math.Round(profileBasal / totalWithProfile * 100 * 10) / 10
                            : 0;
                        insulinDelivery.BolusPercent = totalWithProfile > 0
                            ? Math.Round(insulinDelivery.TotalBolus / totalWithProfile * 100 * 10) / 10
                            : 0;
                    }
                    else
                    {
                        insulinDelivery = _statisticsService.CalculateInsulinDeliveryStatistics(
                            filteredTreatments,
                            startDate,
                            endDate
                        );
                    }

                    // Keep treatment summary basal consistent
                    treatmentSummary.Totals.Insulin.Basal = insulinDelivery.TotalBasal;
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
                            EntryCount = filteredEntries.Count,
                            TreatmentCount = filteredTreatments.Count,
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

            // Cache the result until the next day at midnight (daily expiration)
            var tomorrow = DateTime.UtcNow.Date.AddDays(1);
            await _cacheService.SetAsync(cacheKey, result, tomorrow, cancellationToken);

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
    /// <param name="startTimestamp">Start time in Unix milliseconds</param>
    /// <param name="endTimestamp">End time in Unix milliseconds</param>
    /// <param name="treatments">Treatments in the period (for temp basal adjustments)</param>
    /// <returns>Total scheduled basal insulin in units</returns>
    private double CalculateScheduledBasalForPeriod(
        long startTimestamp,
        long endTimestamp,
        List<Treatment> treatments
    )
    {
        double totalBasal = 0.0;

        // Get temp basal treatments sorted by time
        var tempBasalTreatments = treatments
            .Where(t =>
                !string.IsNullOrEmpty(t.EventType) &&
                (t.EventType.Equals("Temp Basal", StringComparison.OrdinalIgnoreCase) ||
                 t.EventType.Equals("TempBasal", StringComparison.OrdinalIgnoreCase) ||
                 t.EventType.Equals("Temporary Basal", StringComparison.OrdinalIgnoreCase))
            )
            .OrderBy(t => t.Mills)
            .ToList();

        // Sample at 5-minute intervals (same as CGM readings)
        const long intervalMs = 5 * 60 * 1000; // 5 minutes in milliseconds
        var currentTime = startTimestamp;

        while (currentTime < endTimestamp)
        {
            // Get scheduled basal rate at this time (units per hour)
            var scheduledRate = _profileService.GetBasalRate(currentTime);

            // Check if there's an active temp basal at this time
            var activeTempBasal = tempBasalTreatments.FirstOrDefault(t =>
            {
                var tempStart = t.Mills;
                var durationMs = (t.Duration ?? 0) * 60 * 1000; // Duration is in minutes
                var tempEnd = tempStart + durationMs;
                return currentTime >= tempStart && currentTime < tempEnd;
            });

            double effectiveRate;
            if (activeTempBasal != null)
            {
                // Use temp basal rate instead of scheduled
                if (activeTempBasal.Absolute.HasValue)
                {
                    effectiveRate = activeTempBasal.Absolute.Value;
                }
                else if (activeTempBasal.Rate.HasValue)
                {
                    effectiveRate = activeTempBasal.Rate.Value;
                }
                else if (activeTempBasal.Percent.HasValue)
                {
                    // Percent is relative to scheduled rate
                    effectiveRate = scheduledRate * (100 + activeTempBasal.Percent.Value) / 100.0;
                }
                else
                {
                    effectiveRate = scheduledRate;
                }
            }
            else
            {
                effectiveRate = scheduledRate;
            }

            // Convert rate (units/hour) to insulin delivered in this 5-minute interval
            var insulinDelivered = effectiveRate * (5.0 / 60.0); // 5 minutes = 5/60 hours
            totalBasal += insulinDelivered;

            currentTime += intervalMs;
        }

        return Math.Round(totalBasal * 100) / 100; // Round to 2 decimal places
    }

    /// <summary>
    /// Analyze glucose patterns around site changes to identify impact of site age on control
    /// </summary>
    /// <param name="request">Request containing entries, treatments, and analysis parameters</param>
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
                request.Treatments,
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
            var startMills = new DateTimeOffset(startDate).ToUnixTimeMilliseconds();
            var endMills = new DateTimeOffset(endDate).ToUnixTimeMilliseconds();

            // Fetch treatments (for bolus) and basal StateSpans sequentially
            // (DbContext is not thread-safe, can't use Task.WhenAll)
            var treatments = await _postgreSqlService.GetTreatmentsByTimeRangeAsync(
                startMills,
                endMills,
                count: 10000
            );

            var basalSpans = await _stateSpanService.GetStateSpansAsync(
                category: StateSpanCategory.BasalDelivery,
                from: startMills,
                to: endMills,
                count: 10000
            );

            var result = _statisticsService.CalculateDailyBasalBolusRatios(
                treatments,
                basalSpans
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
            var startMills = new DateTimeOffset(startDate).ToUnixTimeMilliseconds();
            var endMills = new DateTimeOffset(endDate).ToUnixTimeMilliseconds();

            // Fetch treatments and basal StateSpans
            var treatments = await _postgreSqlService.GetTreatmentsWithAdvancedFilterAsync(
                count: 10000,
                skip: 0,
                findQuery: $"{{\"mills\":{{\"$gte\":{startMills},\"$lte\":{endMills}}}}}",
                reverseResults: false
            );

            var basalSpans = await _stateSpanService.GetStateSpansAsync(
                category: StateSpanCategory.BasalDelivery,
                from: startMills,
                to: endMills,
                count: 10000
            );
            var basalSpansList = basalSpans.ToList();

            var result = basalSpansList.Count > 0
                ? _statisticsService.CalculateInsulinDeliveryStatistics(
                    treatments, basalSpansList, startDate, endDate)
                : _statisticsService.CalculateInsulinDeliveryStatistics(
                    treatments, startDate, endDate);
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
        [FromQuery] DateTime startDate,
        [FromQuery] DateTime endDate
    )
    {
        try
        {
            var startMills = new DateTimeOffset(startDate).ToUnixTimeMilliseconds();
            var endMills = new DateTimeOffset(endDate).ToUnixTimeMilliseconds();

            // Fetch basal StateSpans directly
            var basalSpans = await _stateSpanService.GetStateSpansAsync(
                category: StateSpanCategory.BasalDelivery,
                from: startMills,
                to: endMills,
                count: 10000
            );

            var result = _statisticsService.CalculateBasalAnalysis(
                basalSpans,
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
    /// Collection of glucose entries with timestamps
    /// </summary>
    public IEnumerable<Entry> Entries { get; set; } = Enumerable.Empty<Entry>();
}

/// <summary>
/// Request model for time in range calculation
/// </summary>
public class TimeInRangeRequest
{
    /// <summary>
    /// Collection of glucose entries
    /// </summary>
    public IEnumerable<Entry> Entries { get; set; } = Enumerable.Empty<Entry>();

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
    /// Collection of glucose entries
    /// </summary>
    public IEnumerable<Entry> Entries { get; set; } = Enumerable.Empty<Entry>();

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
    /// Collection of glucose entries
    /// </summary>
    public IEnumerable<Entry> Entries { get; set; } = Enumerable.Empty<Entry>();

    /// <summary>
    /// Optional collection of treatments
    /// </summary>
    public IEnumerable<Treatment>? Treatments { get; set; }

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
    /// Collection of glucose entries
    /// </summary>
    public IEnumerable<Entry> Entries { get; set; } = Enumerable.Empty<Entry>();

    /// <summary>
    /// Optional collection of treatments
    /// </summary>
    public IEnumerable<Treatment>? Treatments { get; set; }

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
    /// Collection of glucose entries
    /// </summary>
    public IEnumerable<Entry> Entries { get; set; } = Enumerable.Empty<Entry>();

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
    /// Collection of glucose entries
    /// </summary>
    public IEnumerable<Entry> Entries { get; set; } = Enumerable.Empty<Entry>();

    /// <summary>
    /// Collection of treatments (must include site changes)
    /// </summary>
    public IEnumerable<Treatment> Treatments { get; set; } = Enumerable.Empty<Treatment>();

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

