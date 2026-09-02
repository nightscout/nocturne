using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Nocturne.API.Attributes;
using Nocturne.API.Extensions;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.Analytics;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Profiles;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Basal;
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
/// Several computation endpoints accept large payloads and are decorated with
/// <c>[RequestSizeLimit(ComputeBodyLimitBytes)]</c>.
///
/// <c>GET /periods</c> and <c>GET /basal-analysis</c> fetch data directly from the database
/// using the injected V4 repositories, apply profile-based scheduled-basal fallback when no
/// TempBasal records exist, and cache results for 5 minutes to absorb rapid dashboard refreshes.
///
/// All repositories use <c>ITenantDbContextFactory</c> so each call creates its own independent
/// DbContext; independent repository calls within a single request are parallelised with <c>Task.WhenAll</c>.
/// </remarks>
/// <seealso cref="IStatisticsService"/>
/// <seealso cref="ISensorGlucoseRepository"/>
/// <seealso cref="IBolusRepository"/>
/// <seealso cref="ICarbIntakeRepository"/>
/// <seealso cref="ITempBasalRepository"/>
/// <seealso cref="IAidMetricsService"/>
[ApiController]
[Tags("Analytics")]
[Route("api/v4/[controller]")]
[Produces("application/json")]
public class StatisticsController : ControllerBase
{
    /// <summary>
    /// Body ceiling for the actions that compute over a caller-supplied collection. A report
    /// covering a year posts every reading in the range in one body, so the bound is set by the
    /// longest range a report can ask for rather than by a typical request.
    /// </summary>
    public const int ComputeBodyLimitBytes = 100 * 1024 * 1024;

    private readonly IStatisticsService _statisticsService;
    private readonly ICacheService _cacheService;
    private readonly IProfileProjectionService _profileProjectionService;
    private readonly IBasalRateResolver _basalRateResolver;
    private readonly IBasalSegmentService _basalSegments;
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
    private readonly IBasalInjectionRepository _basalInjectionRepository;
    private readonly IActiveProfileResolver _activeProfileResolver;
    private readonly ICanonicalGlucoseService _canonicalGlucose;

    private string TenantCacheId =>
        _tenantAccessor.Context?.TenantId.ToString()
        ?? throw new InvalidOperationException("Tenant context is not resolved");

    public StatisticsController(
        IStatisticsService statisticsService,
        ICacheService cacheService,
        IProfileProjectionService profileProjectionService,
        IBasalRateResolver basalRateResolver,
        IBasalSegmentService basalSegments,
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
        ITargetRangeScheduleRepository targetRangeScheduleRepository,
        IBasalInjectionRepository basalInjectionRepository,
        IActiveProfileResolver activeProfileResolver,
        ICanonicalGlucoseService canonicalGlucose
    )
    {
        _statisticsService = statisticsService;
        _cacheService = cacheService;
        _profileProjectionService = profileProjectionService;
        _basalRateResolver = basalRateResolver;
        _basalSegments = basalSegments;
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
        _basalInjectionRepository = basalInjectionRepository;
        _activeProfileResolver = activeProfileResolver;
        _canonicalGlucose = canonicalGlucose;
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
    [EnableRateLimiting(ServiceRegistrationExtensions.StatisticsComputeRateLimitPolicy)]
    [RequireScope(Scope.ReportsRead)]
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
    [EnableRateLimiting(ServiceRegistrationExtensions.StatisticsComputeRateLimitPolicy)]
    [RequireScope(Scope.ReportsRead)]
    [RemoteQuery]
    [RequestSizeLimit(ComputeBodyLimitBytes)]
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
    [EnableRateLimiting(ServiceRegistrationExtensions.StatisticsComputeRateLimitPolicy)]
    [RequireScope(Scope.ReportsRead)]
    [RequestSizeLimit(ComputeBodyLimitBytes)]
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
    [EnableRateLimiting(ServiceRegistrationExtensions.StatisticsComputeRateLimitPolicy)]
    [RequireScope(Scope.ReportsRead)]
    [RemoteQuery]
    [RequestSizeLimit(ComputeBodyLimitBytes)]
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
    [EnableRateLimiting(ServiceRegistrationExtensions.StatisticsComputeRateLimitPolicy)]
    [RequireScope(Scope.ReportsRead)]
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
    [EnableRateLimiting(ServiceRegistrationExtensions.StatisticsComputeRateLimitPolicy)]
    [RequireScope(Scope.ReportsRead)]
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
    [EnableRateLimiting(ServiceRegistrationExtensions.StatisticsComputeRateLimitPolicy)]
    [RequireScope(Scope.ReportsRead)]
    [RequestSizeLimit(ComputeBodyLimitBytes)]
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
    [EnableRateLimiting(ServiceRegistrationExtensions.StatisticsComputeRateLimitPolicy)]
    [RequireScope(Scope.ReportsRead)]
    [RequestSizeLimit(ComputeBodyLimitBytes)]
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
    /// Extended glucose analytics for a date range, computed server-side. Fetches glucose,
    /// manual boluses, and carb intakes for the window from the database and runs
    /// <see cref="IStatisticsService.AnalyzeGlucoseDataExtended"/> plus
    /// <see cref="IStatisticsService.CalculateAveragedStats"/>.
    /// </summary>
    /// <param name="startDate">Start of the window (inclusive, UTC).</param>
    /// <param name="endDate">End of the window (exclusive, UTC).</param>
    /// <param name="population">Diabetes population for clinical target assessment. Defaults to Type 1 adult.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The extended analytics and time-of-day averaged stats for the window.</returns>
    [HttpGet("range-analytics")]
    [RequireScope(Scope.ReportsRead)]
    [RemoteQuery]
    [ResponseCache(Duration = 60, VaryByQueryKeys = new[] { "*" })]
    public async Task<ActionResult<ReportAnalysisResult>> GetRangeAnalytics(
        [FromQuery] DateTime startDate,
        [FromQuery] DateTime endDate,
        [FromQuery] DiabetesPopulation population = DiabetesPopulation.Type1Adult,
        [FromQuery] Guid? patientDeviceId = null,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            var startDt = DateTime.SpecifyKind(startDate, DateTimeKind.Utc);
            var endDt = DateTime.SpecifyKind(endDate, DateTimeKind.Utc);

            // int.MaxValue limit mirrors ActogramReportService so dense tenants are never
            // silently truncated (the cause of skewed report stats on high-frequency uploads).
            var glucoseTask = _sensorGlucoseRepository.GetAsync(startDt, endDt, null, null, int.MaxValue, descending: false, patientDeviceId: patientDeviceId, ct: cancellationToken);
            var bolusTask   = _bolusRepository.GetAsync(startDt, endDt, null, null, int.MaxValue, descending: false, kind: BolusKind.Manual, ct: cancellationToken);
            var carbTask    = _carbIntakeRepository.GetAsync(startDt, endDt, null, null, int.MaxValue, descending: false, ct: cancellationToken);
            var devicesTask = _patientDeviceRepository.GetByDateRangeAsync(startDt, endDt, ct: cancellationToken);

            await Task.WhenAll(glucoseTask, bolusTask, carbTask, devicesTask);

            var rawGlucose = (await glucoseTask).ToList();

            // Unfiltered statistics compute over the canonical stream, never blended CGMs.
            // A patientDeviceId filter already restricts the fetch to one device raw, so there
            // is nothing left to canonicalize/blend.
            var entries = patientDeviceId is null
                ? (await _canonicalGlucose.SelectAsync(rawGlucose, cancellationToken)).ToList()
                : rawGlucose;
            var boluses = await bolusTask;
            var carbs   = await carbTask;
            var devices = await devicesTask;

            // Registered devices that contributed readings, for the UI's device picker. Count in a
            // single pass over the raw readings rather than re-scanning per device. The unattributed
            // bucket is tracked separately — a null key can't live in a Dictionary.
            var countByDevice = new Dictionary<Guid, int>();
            var unattributedCount = 0;
            foreach (var r in rawGlucose)
            {
                if (r.PatientDeviceId is { } id)
                    countByDevice[id] = countByDevice.GetValueOrDefault(id) + 1;
                else
                    unattributedCount++;
            }

            var contributingDevices = new List<ContributingDevice>();
            foreach (var d in devices.Where(d => d.DeviceCategory == DeviceCategory.CGM))
            {
                if (!countByDevice.TryGetValue(d.Id, out var readingCount) || readingCount == 0) continue;

                contributingDevices.Add(new ContributingDevice
                {
                    PatientDeviceId = d.Id,
                    Name = d.DisplayName(),
                    ReadingCount = readingCount,
                });
            }

            if (unattributedCount > 0)
            {
                contributingDevices.Add(new ContributingDevice
                {
                    PatientDeviceId = null,
                    Name = "Unattributed",
                    ReadingCount = unattributedCount,
                });
            }

            var result = new ReportAnalysisResult
            {
                Analysis = _statisticsService.AnalyzeGlucoseDataExtended(entries, boluses, carbs, population),
                AveragedStats = _statisticsService.CalculateAveragedStats(entries).ToList(),
                ContributingDevices = contributingDevices,
                PersonalRange = await CalculatePersonalRangeAsync(entries, cancellationToken),
            };
            return Ok(result);
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Returns the target range schedule active for the tenant's active profile right now, or null
    /// when none is configured. Shared selection policy for report statistics and AID metrics, kept
    /// in step with the alert engine and <c>TargetRangeResolver</c> (active profile, newest schedule
    /// at the point in time) so reports evaluate the same range alerts fire against — rather than the
    /// globally-newest record, which could belong to a different, inactive profile.
    /// </summary>
    private async Task<TargetRangeSchedule?> GetActiveTargetRangeScheduleAsync(CancellationToken ct)
    {
        var nowMills = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var profileName = await _activeProfileResolver.GetActiveProfileNameAsync(nowMills, ct) ?? "Default";
        return await _targetRangeScheduleRepository.GetActiveAtAsync(profileName, DateTime.UtcNow, ct);
    }

    /// <summary>
    /// Computes time in the tenant's personal target range for the report window. Failure-isolated:
    /// the personal range is optional garnish on the report, so a missing schedule or a failed
    /// fetch returns null rather than failing the base analytics.
    /// </summary>
    private async Task<PersonalRangeTimeInRange?> CalculatePersonalRangeAsync(
        List<SensorGlucose> entries,
        CancellationToken ct
    )
    {
        try
        {
            var schedule = await GetActiveTargetRangeScheduleAsync(ct);
            if (schedule is null || schedule.Entries.Count == 0)
                return null;

            var tzId = await _therapySettingsResolver.GetTimezoneAsync(ct: ct);
            return _statisticsService.CalculatePersonalRangeTime(
                entries,
                schedule.Entries,
                TimeZoneHelper.GetTimeZoneInfoFromId(tzId)
            );
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
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
    [EnableRateLimiting(ServiceRegistrationExtensions.StatisticsComputeRateLimitPolicy)]
    [RequireScope(Scope.ReportsRead)]
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
    [EnableRateLimiting(ServiceRegistrationExtensions.StatisticsComputeRateLimitPolicy)]
    [RequireScope(Scope.ReportsRead)]
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
    [EnableRateLimiting(ServiceRegistrationExtensions.StatisticsComputeRateLimitPolicy)]
    [RequireScope(Scope.ReportsRead)]
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
    [RequireScope(Scope.ReportsRead)]
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
    [RequireScope(Scope.ReportsRead)]
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
    [RequireScope(Scope.ReportsRead)]
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
    [RequireScope(Scope.ReportsRead)]
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
    /// Validate treatment data for completeness and consistency
    /// </summary>
    /// <param name="treatment">Treatment to validate</param>
    /// <returns>True if treatment data is valid</returns>
    [HttpPost("validate/treatment")]
    [EnableRateLimiting(ServiceRegistrationExtensions.StatisticsComputeRateLimitPolicy)]
    [RequireScope(Scope.ReportsRead)]
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
    [EnableRateLimiting(ServiceRegistrationExtensions.StatisticsComputeRateLimitPolicy)]
    [RequireScope(Scope.ReportsRead)]
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
    /// falls back to integrating scheduled basal across the period via <see cref="IBasalSegmentService"/>.
    /// GMI reliability is assessed per-period using context-appropriate recommended-day minimums
    /// (e.g., 1-day periods cannot require 14 days of data).
    /// </remarks>
    [HttpGet("periods")]
    [RequireScope(Scope.GlucoseRead)]
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

            var periodResults = new List<(int Days, PeriodStatistics Statistics)>();

            foreach (var days in periods)
            {
                var startDate = now.AddDays(-days);
                var endDate = now;

                var startTimestamp = startDate;
                var endTimestamp = endDate;

                var glucoseTask = _sensorGlucoseRepository.GetAsync(from: (DateTime?)startTimestamp, to: (DateTime?)endTimestamp, device: null, source: null, limit: int.MaxValue, descending: false, ct: cancellationToken);
                var bolusTask   = _bolusRepository.GetAsync(from: (DateTime?)startTimestamp, to: (DateTime?)endTimestamp, device: null, source: null, limit: int.MaxValue, descending: false, kind: BolusKind.Manual, ct: cancellationToken);
                var carbTask    = _carbIntakeRepository.GetAsync(from: (DateTime?)startTimestamp, to: (DateTime?)endTimestamp, device: null, source: null, limit: int.MaxValue, descending: false, ct: cancellationToken);

                await Task.WhenAll(glucoseTask, bolusTask, carbTask);

                var filteredEntries = (await _canonicalGlucose.SelectAsync((await glucoseTask).ToList(), cancellationToken)).ToList();
                var filteredBoluses = (await bolusTask).ToList();
                var filteredCarbs   = (await carbTask).ToList();

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
                    // Awaited sequentially: these reads share the request-scoped NocturneDbContext,
                    // which does not support concurrent operations.
                    var tempBasals       = (await _tempBasalRepository.GetAsync(from: startTimestamp, to: endTimestamp, device: null, source: null, limit: int.MaxValue, descending: false, ct: cancellationToken)).ToList();
                    var algorithmBoluses = (await _bolusRepository.GetAsync(from: startTimestamp, to: endTimestamp, device: null, source: null, limit: int.MaxValue, descending: false, kind: BolusKind.Algorithm, ct: cancellationToken)).ToList();
                    var basalInjections  = (await _basalInjectionRepository.GetAsync(startTimestamp, endTimestamp, null, null, int.MaxValue, 0, false, cancellationToken)).ToList();

                    insulinDelivery = _statisticsService.CalculateInsulinDeliveryStatistics(
                        filteredBoluses,
                        algorithmBoluses,
                        tempBasals,
                        filteredCarbs,
                        startDate,
                        endDate,
                        basalInjections
                    );

                    // If no TempBasals/algorithm boluses/basal injections but we have profile data, augment with scheduled basal
                    if (
                        tempBasals.Count == 0
                        && algorithmBoluses.Count == 0
                        && basalInjections.Count == 0
                        && hasProfileData
                    )
                    {
                        var fromMs = new DateTimeOffset(startTimestamp, TimeSpan.Zero).ToUnixTimeMilliseconds();
                        var toMs = new DateTimeOffset(endTimestamp, TimeSpan.Zero).ToUnixTimeMilliseconds();
                        var profileBasal = Math.Round(
                            await _basalSegments.GetSegmentsAsync(fromMs, toMs, cancellationToken).SumUnitsAsync(cancellationToken)
                            * 100) / 100;
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
    /// Analyze glucose patterns around site changes to identify impact of site age on control
    /// </summary>
    /// <param name="request">Request containing sensor glucose readings, device events, and analysis parameters</param>
    /// <returns>Site change impact analysis with averaged glucose patterns</returns>
    [HttpPost("site-change-impact")]
    [EnableRateLimiting(ServiceRegistrationExtensions.StatisticsComputeRateLimitPolicy)]
    [RequireScope(Scope.ReportsRead)]
    [RequestSizeLimit(ComputeBodyLimitBytes)]
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
    [RequireScope(Scope.ReportsRead)]
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

            // Awaited sequentially: these reads share the request-scoped NocturneDbContext,
            // which does not support concurrent operations.
            var boluses          = await _bolusRepository.GetAsync(startDt, endDt, null, null, 10000, descending: false, kind: BolusKind.Manual);
            var tempBasals       = (await _tempBasalRepository.GetAsync(startDt, endDt, null, null, 10000, descending: false)).ToList();
            var algorithmBoluses = await _bolusRepository.GetAsync(startDt, endDt, null, null, 10000, descending: false, kind: BolusKind.Algorithm);
            var basalInjections  = (await _basalInjectionRepository.GetAsync(startDt, endDt, null, null, 10000, 0, false)).ToList();

            var tzId = await _therapySettingsResolver.GetTimezoneAsync();
            var tz = !string.IsNullOrEmpty(tzId)
                ? TimeZoneHelper.GetTimeZoneInfoFromId(tzId)
                : TimeZoneInfo.Utc;

            var result = _statisticsService.CalculateDailyBasalBolusRatios(
                boluses,
                algorithmBoluses,
                tempBasals,
                tz,
                basalInjections
            );
            return Ok(result);
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Pre-aggregated month-by-day statistics for the calendar punch-card view. Fetches glucose,
    /// boluses, carb intakes, and daily basal totals in a single batch, then computes per-day TIR
    /// and treatment summaries inline (no per-day round-trips). Replaces a frontend orchestrator
    /// that was issuing ~62 sequential HTTP calls per 31-day month.
    /// </summary>
    /// <param name="startDate">Inclusive start of the date range.</param>
    /// <param name="endDate">Inclusive end of the date range.</param>
    /// <returns><see cref="PunchCardResponse"/> with months, days, and global maxes for chart scaling.</returns>
    [HttpGet("punch-card")]
    [RequireScope(Scope.GlucoseRead)]
    [RemoteQuery]
    public async Task<ActionResult<PunchCardResponse>> GetPunchCardData(
        [FromQuery] DateTime startDate,
        [FromQuery] DateTime endDate,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            var tzId = await _therapySettingsResolver.GetTimezoneAsync(ct: cancellationToken);
            var tz = !string.IsNullOrEmpty(tzId)
                ? TimeZoneHelper.GetTimeZoneInfoFromId(tzId)
                : TimeZoneInfo.Utc;

            var startLocalDate = DateTime.SpecifyKind(startDate.Date, DateTimeKind.Unspecified);
            var endLocalDate = DateTime.SpecifyKind(endDate.Date, DateTimeKind.Unspecified);
            var startDt = TimeZoneInfo.ConvertTimeToUtc(startLocalDate, tz);
            var endDt = TimeZoneInfo.ConvertTimeToUtc(endLocalDate.AddDays(1).AddTicks(-1), tz);

            // Awaited sequentially: these reads share the request-scoped NocturneDbContext,
            // which does not support concurrent operations.
            var rawGlucose       = (await _sensorGlucoseRepository.GetAsync(startDt, endDt, null, null, 100_000, descending: false, ct: cancellationToken)).ToList();
            var glucoseData      = (await _canonicalGlucose.SelectAsync(rawGlucose, cancellationToken)).ToList();
            var manualBoluses    = (await _bolusRepository.GetAsync(startDt, endDt, null, null, 10_000, descending: false, kind: BolusKind.Manual, ct: cancellationToken)).ToList();
            var carbs            = (await _carbIntakeRepository.GetAsync(startDt, endDt, null, null, 10_000, descending: false, ct: cancellationToken)).ToList();
            var algorithmBoluses = (await _bolusRepository.GetAsync(startDt, endDt, null, null, 10_000, descending: false, kind: BolusKind.Algorithm, ct: cancellationToken)).ToList();
            var tempBasals       = (await _tempBasalRepository.GetAsync(startDt, endDt, null, null, 10_000, descending: false, ct: cancellationToken)).ToList();
            var basalInjections  = (await _basalInjectionRepository.GetAsync(startDt, endDt, null, null, 10_000, 0, false, ct: cancellationToken)).ToList();

            // Daily basal totals come from the existing service path so the calendar's "totalBasal"
            // matches what /daily-basal-bolus-ratios would return for the same window.
            var dailyBasalBolus = _statisticsService.CalculateDailyBasalBolusRatios(
                manualBoluses, algorithmBoluses, tempBasals, tz, basalInjections);
            var dailyBasalMap = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var d in dailyBasalBolus.DailyData)
            {
                if (!string.IsNullOrEmpty(d.Date)) dailyBasalMap[d.Date] = d.Basal;
            }

            // Build the per-month/per-day shape.
            var monthsMap = new Dictionary<string, PunchCardMonth>(StringComparer.Ordinal);
            string[] monthNames =
            [
                "January", "February", "March", "April", "May", "June",
                "July", "August", "September", "October", "November", "December"
            ];

            for (var day = startLocalDate.Date; day <= endLocalDate.Date; day = day.AddDays(1))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var monthKey = $"{day.Year}-{day.Month - 1}";
                if (!monthsMap.TryGetValue(monthKey, out var monthBucket))
                {
                    monthBucket = new PunchCardMonth
                    {
                        Year = day.Year,
                        Month = day.Month - 1,
                        MonthName = monthNames[day.Month - 1],
                    };
                    monthsMap[monthKey] = monthBucket;
                }

                var dayStartLocal = DateTime.SpecifyKind(day, DateTimeKind.Unspecified);
                var dayEndLocal = dayStartLocal.AddDays(1).AddTicks(-1);
                var dayStartUtc = TimeZoneInfo.ConvertTimeToUtc(dayStartLocal, tz);
                var dayEndUtc = TimeZoneInfo.ConvertTimeToUtc(dayEndLocal, tz);
                var dayStartMs = new DateTimeOffset(dayStartUtc, TimeSpan.Zero).ToUnixTimeMilliseconds();
                var dayEndMs = new DateTimeOffset(dayEndUtc, TimeSpan.Zero).ToUnixTimeMilliseconds();

                var dayEntries = glucoseData
                    .Where(e => e.Mills >= dayStartMs && e.Mills <= dayEndMs)
                    .ToList();
                var dayBoluses = manualBoluses
                    .Where(b => b.Mills >= dayStartMs && b.Mills <= dayEndMs)
                    .ToList();
                var dayCarbs = carbs
                    .Where(c => c.Mills >= dayStartMs && c.Mills <= dayEndMs)
                    .ToList();

                TimeInRangeMetrics? tir = dayEntries.Count > 0
                    ? _statisticsService.CalculateTimeInRange(dayEntries)
                    : null;
                TreatmentSummary? treatment = dayBoluses.Count > 0 || dayCarbs.Count > 0
                    ? _statisticsService.CalculateTreatmentSummary(dayBoluses, dayCarbs)
                    : null;

                var pct = tir?.Percentages;
                var inRangePct = pct?.Target ?? 0;
                var lowPct = (pct?.VeryLow ?? 0) + (pct?.Low ?? 0);
                var highPct = (pct?.VeryHigh ?? 0) + (pct?.High ?? 0);

                var dur = tir?.Durations;
                var totalMinutes = (dur?.VeryLow ?? 0) + (dur?.Low ?? 0)
                    + (dur?.Target ?? 0) + (dur?.High ?? 0) + (dur?.VeryHigh ?? 0);
                var totalReadings = (int)Math.Round(totalMinutes / 5.0);
                var inRangeCount = (int)Math.Round(inRangePct / 100.0 * totalReadings);
                var lowCount = (int)Math.Round(lowPct / 100.0 * totalReadings);
                var highCount = (int)Math.Round(highPct / 100.0 * totalReadings);

                var rangeStats = tir?.RangeStats;
                var avgGlucose = rangeStats?.Target?.Mean ?? rangeStats?.Low?.Mean ?? 0;

                var dateStr = day.ToString("yyyy-MM-dd");
                var totals = treatment?.Totals;
                var totalCarbs = totals?.Food?.Carbs ?? 0;
                var totalBolus = totals?.Insulin?.Bolus ?? 0;
                var totalBasal = dailyBasalMap.GetValueOrDefault(dateStr, 0.0);
                var totalInsulin = totalBolus + totalBasal;
                var carbToInsulinRatio = treatment?.CarbToInsulinRatio ?? 0;

                var entries = dayEntries
                    .Where(e => e.Mgdl > 0)
                    .OrderBy(e => e.Mills)
                    .Select(e => new PunchCardEntry { Mills = e.Mills, Mgdl = e.Mgdl })
                    .ToList();

                var dayStats = new PunchCardDay
                {
                    Date = dateStr,
                    Timestamp = dayStartMs,
                    TotalReadings = totalReadings,
                    InRangeCount = inRangeCount,
                    LowCount = lowCount,
                    HighCount = highCount,
                    InRangePercent = inRangePct,
                    LowPercent = lowPct,
                    HighPercent = highPct,
                    AverageGlucose = avgGlucose,
                    TotalCarbs = totalCarbs,
                    TotalInsulin = totalInsulin,
                    TotalBolus = totalBolus,
                    TotalBasal = totalBasal,
                    CarbToInsulinRatio = carbToInsulinRatio,
                    Entries = entries,
                };

                monthBucket.Days.Add(dayStats);
                monthBucket.MaxCarbs = Math.Max(monthBucket.MaxCarbs, dayStats.TotalCarbs);
                monthBucket.MaxInsulin = Math.Max(monthBucket.MaxInsulin, dayStats.TotalInsulin);
                monthBucket.MaxCarbInsulinDiff = Math.Max(
                    monthBucket.MaxCarbInsulinDiff, Math.Abs(dayStats.CarbToInsulinRatio));
                monthBucket.TotalReadings += dayStats.TotalReadings;
            }

            // Per-month summaries from days that actually had data.
            foreach (var month in monthsMap.Values)
            {
                var daysWithData = month.Days.Where(d => d.TotalReadings > 0).ToList();
                if (daysWithData.Count == 0)
                {
                    month.Summary = null;
                    continue;
                }

                var totalIR = daysWithData.Sum(d => d.InRangeCount);
                var totalLow = daysWithData.Sum(d => d.LowCount);
                var totalHigh = daysWithData.Sum(d => d.HighCount);
                var totalReadings = daysWithData.Sum(d => d.TotalReadings);
                var glucoseDays = daysWithData.Where(d => d.AverageGlucose > 0).ToList();

                month.Summary = new PunchCardMonthSummary
                {
                    DayCount = daysWithData.Count,
                    TotalReadings = totalReadings,
                    InRangePercent = totalReadings > 0 ? (double)totalIR / totalReadings * 100 : 0,
                    LowPercent = totalReadings > 0 ? (double)totalLow / totalReadings * 100 : 0,
                    HighPercent = totalReadings > 0 ? (double)totalHigh / totalReadings * 100 : 0,
                    AvgGlucose = glucoseDays.Count > 0
                        ? glucoseDays.Average(d => d.AverageGlucose) : 0,
                };
            }

            var months = monthsMap.Values
                .OrderBy(m => m.Year).ThenBy(m => m.Month)
                .ToList();

            return Ok(new PunchCardResponse
            {
                Months = months,
                DateRange = new PunchCardDateRange
                {
                    From = startDt.ToString("o"),
                    To = endDt.ToString("o"),
                },
                GlobalMaxCarbs = months.Count > 0 ? months.Max(m => m.MaxCarbs) : 0,
                GlobalMaxInsulin = months.Count > 0 ? months.Max(m => m.MaxInsulin) : 0,
                GlobalMaxCarbInsulinDiff = months.Count > 0 ? months.Max(m => m.MaxCarbInsulinDiff) : 0,
            });
        }
        catch (OperationCanceledException)
        {
            throw;
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
    [RequireScope(Scope.ReportsRead)]
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

            var startMs = new DateTimeOffset(startDt, TimeSpan.Zero).ToUnixTimeMilliseconds();
            var endMs   = new DateTimeOffset(endDt,   TimeSpan.Zero).ToUnixTimeMilliseconds();

            // These reads share the request-scoped NocturneDbContext, which is not safe for
            // concurrent operations, so they are awaited sequentially rather than via Task.WhenAll.
            var boluses          = await _bolusRepository.GetAsync(startDt, endDt, null, null, 10000, descending: false, kind: BolusKind.Manual);
            var tempBasals       = (await _tempBasalRepository.GetAsync(startDt, endDt, null, null, 10000, descending: false)).ToList();
            var algorithmBoluses = await _bolusRepository.GetAsync(startDt, endDt, null, null, 10000, descending: false, kind: BolusKind.Algorithm);
            var carbs            = await _carbIntakeRepository.GetAsync(startDt, endDt, null, null, 10000, descending: false);
            var basalInjections  = (await _basalInjectionRepository.GetAsync(startDt, endDt, null, null, 10000, 0, false)).ToList();
            var rateAt           = await _basalRateResolver.BuildResolverAsync(startMs, endMs);

            foreach (var tb in tempBasals)
            {
                if (!tb.ScheduledRate.HasValue && tb.Origin != TempBasalOrigin.Scheduled)
                    tb.ScheduledRate = rateAt(tb.StartMills);
            }

            var result = _statisticsService.CalculateInsulinDeliveryStatistics(
                boluses,
                algorithmBoluses,
                tempBasals,
                carbs,
                startDate,
                endDate,
                basalInjections
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
    [RequireScope(Scope.ReportsRead)]
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
            // No practical fetch cap — see GetHourlyInsulinDelivery: a 10k cap
            // silently truncates ~5-minute AID records past ~35 days.
            var tempBasalTask = _tempBasalRepository.GetAsync((DateTime?)startUtc, (DateTime?)endUtc, null, null, int.MaxValue, descending: false);
            var algoTask      = _bolusRepository.GetAsync((DateTime?)startUtc, (DateTime?)endUtc, null, null, int.MaxValue, descending: false, kind: BolusKind.Algorithm);

            await Task.WhenAll(tempBasalTask, algoTask);

            var tempBasals       = (await tempBasalTask).ToList();
            var algorithmBoluses = await algoTask;

            // Fall back to profile-based scheduled rates when no TempBasals exist.
            // Each segment becomes one synthetic TempBasal; CalculateBasalAnalysis distributes
            // each across the user-local hour-of-day buckets it overlaps, weighted by duration.
            var hasTherapyData = await _therapySettingsResolver.HasDataAsync(HttpContext.RequestAborted);
            if (tempBasals.Count == 0 && hasTherapyData)
            {
                var fromMs = new DateTimeOffset(startUtc, TimeSpan.Zero).ToUnixTimeMilliseconds();
                var toMs = new DateTimeOffset(endUtc, TimeSpan.Zero).ToUnixTimeMilliseconds();
                await foreach (var seg in _basalSegments.GetSegmentsAsync(fromMs, toMs, HttpContext.RequestAborted))
                {
                    tempBasals.Add(new TempBasal
                    {
                        StartTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(seg.StartMills).UtcDateTime,
                        EndTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(seg.EndMills).UtcDateTime,
                        Rate = seg.UnitsPerHour,
                        Origin = TempBasalOrigin.Scheduled,
                    });
                }
            }

            var tzId = await _therapySettingsResolver.GetTimezoneAsync(ct: HttpContext.RequestAborted);
            var userTz = string.IsNullOrEmpty(tzId) ? null : TimeZoneHelper.GetTimeZoneInfoFromId(tzId);

            var result = _statisticsService.CalculateBasalAnalysis(
                tempBasals,
                algorithmBoluses,
                startUtc,
                endUtc,
                userTz
            );
            return Ok(result);
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Calculates the average insulin delivered per hour of day, split by scheduled
    /// basal, temp adjustments, and boluses, from pump-confirmed delivery records.
    /// Basal insulin is duration-weighted across the user-local hours each TempBasal
    /// overlaps, and averages are taken over the days that have delivery data — a
    /// window that extends before the first record does not distort the pattern.
    /// </summary>
    /// <param name="startDate">Start date of the analysis period (UTC)</param>
    /// <param name="endDate">End date of the analysis period (UTC)</param>
    /// <returns>24 hourly averages plus the number of days with data</returns>
    [HttpGet("hourly-insulin-delivery")]
    [RequireScope(Scope.ReportsRead)]
    [RemoteQuery]
    public async Task<ActionResult<HourlyInsulinDeliveryResponse>> GetHourlyInsulinDelivery(
        [FromQuery] DateTime? startDate = null,
        [FromQuery] DateTime? endDate = null
    )
    {
        try
        {
            if (startDate is null || endDate is null)
                return BadRequest(new { error = "startDate and endDate are required." });

            var startUtc = DateTime.SpecifyKind(startDate.Value, DateTimeKind.Utc);
            var endUtc = DateTime.SpecifyKind(endDate.Value, DateTimeKind.Utc);

            // No practical fetch cap: AID pumps write a TempBasal (and often an
            // SMB) every ~5 minutes, so 90 days is ~26k records per type — a
            // 10k cap would silently truncate to the oldest ~35 days and
            // understate every average.
            // Awaited sequentially: these reads share the request-scoped NocturneDbContext,
            // which does not support concurrent operations.
            var tempBasals       = (await _tempBasalRepository.GetAsync((DateTime?)startUtc, (DateTime?)endUtc, null, null, int.MaxValue, descending: false)).ToList();
            var boluses          = await _bolusRepository.GetAsync((DateTime?)startUtc, (DateTime?)endUtc, null, null, int.MaxValue, descending: false, kind: BolusKind.Manual);
            var algorithmBoluses = await _bolusRepository.GetAsync((DateTime?)startUtc, (DateTime?)endUtc, null, null, int.MaxValue, descending: false, kind: BolusKind.Algorithm);
            var basalInjections  = (await _basalInjectionRepository.GetAsync((DateTime?)startUtc, (DateTime?)endUtc, null, null, int.MaxValue, 0, false)).ToList();

            // Fall back to profile-based scheduled rates when no TempBasals exist,
            // mirroring GetBasalAnalysis: each segment becomes one synthetic
            // TempBasal that gets duration-weighted across the hours it overlaps.
            // Skip the profile fallback when basal injections exist: MDI injections are the
            // actual basal source, so synthesizing scheduled rates from the profile on top of
            // them would double-count the day's baseline coverage.
            var hasTherapyData = await _therapySettingsResolver.HasDataAsync(HttpContext.RequestAborted);
            if (tempBasals.Count == 0 && basalInjections.Count == 0 && hasTherapyData)
            {
                var fromMs = new DateTimeOffset(startUtc, TimeSpan.Zero).ToUnixTimeMilliseconds();
                var toMs = new DateTimeOffset(endUtc, TimeSpan.Zero).ToUnixTimeMilliseconds();
                await foreach (var seg in _basalSegments.GetSegmentsAsync(fromMs, toMs, HttpContext.RequestAborted))
                {
                    tempBasals.Add(new TempBasal
                    {
                        StartTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(seg.StartMills).UtcDateTime,
                        EndTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(seg.EndMills).UtcDateTime,
                        Rate = seg.UnitsPerHour,
                        Origin = TempBasalOrigin.Scheduled,
                    });
                }
            }

            var tzId = await _therapySettingsResolver.GetTimezoneAsync(ct: HttpContext.RequestAborted);
            var userTz = string.IsNullOrEmpty(tzId) ? null : TimeZoneHelper.GetTimeZoneInfoFromId(tzId);

            var result = _statisticsService.CalculateHourlyInsulinDelivery(
                tempBasals,
                boluses,
                algorithmBoluses,
                startUtc,
                endUtc,
                userTz,
                basalInjections
            );
            return Ok(result);
        }
        // Input-shaped failures only; anything else propagates to the global
        // handler as a 5xx instead of masquerading as a client error.
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
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
    [RequireScope(Scope.ReportsRead)]
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

            var apsTask     = _apsSnapshotRepository.GetAsync(startDt, endDt, null, null, 50000, descending: false);
            var basalTask   = _tempBasalRepository.GetAsync(startDt, endDt, null, null, 50000, descending: false);
            var eventTask   = _deviceEventRepository.GetAsync(startDt, endDt, null, null, 10000, descending: false);
            var glucoseTask = _sensorGlucoseRepository.GetAsync(startDt, endDt, null, null, 50000, descending: false);

            await Task.WhenAll(apsTask, basalTask, eventTask, glucoseTask);

            var apsSnapshots = (await apsTask).ToList();
            var tempBasals   = (await basalTask).ToList();
            var deviceEvents = (await eventTask).ToList();
            var glucose      = (await glucoseTask).ToList();

            // Count site changes
            var siteChangeCount = deviceEvents.Count(e =>
                e.EventType == DeviceEventType.SiteChange
            );

            // Resolve CGM device names
            var cgmDevices = devices.Where(d => d.DeviceCategory == DeviceCategory.CGM).ToList();
            var cgmDeviceNames = cgmDevices.Count > 0
                ? string.Join(", ", cgmDevices
                    .Select(d => d.CatalogId != null ? DeviceCatalog.GetById(d.CatalogId)?.Name : null)
                    .Where(n => n != null)
                    .DefaultIfEmpty(cgmDevices.First().Model ?? cgmDevices.First().Manufacturer))
                : null;

            // Resolve pump device names
            var pumpDeviceNames = deviceSegments.Count > 0
                ? string.Join(", ", devices
                    .Where(d => d.DeviceCategory == DeviceCategory.InsulinPump)
                    .Select(d => d.CatalogId != null ? DeviceCatalog.GetById(d.CatalogId)?.Name : null)
                    .Where(n => n != null)
                    .Distinct())
                : null;

            // Calculate per-device CGM active time
            double? cgmActivePercent = null;
            if (glucose.Count > 0)
            {
                if (cgmDevices.Count > 0)
                {
                    double totalExpected = 0;
                    double totalActual = 0;

                    foreach (var cgm in cgmDevices)
                    {
                        var catalogEntry = cgm.CatalogId != null ? DeviceCatalog.GetById(cgm.CatalogId) : null;
                        var interval = catalogEntry?.Cgm?.UpdateIntervalMinutes ?? 5;
                        var deviceStart = cgm.StartDate.HasValue
                            ? DateTime.SpecifyKind(cgm.StartDate.Value.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc)
                            : startDt;
                        var deviceEnd = cgm.EndDate.HasValue
                            ? DateTime.SpecifyKind(cgm.EndDate.Value.ToDateTime(new TimeOnly(23, 59, 59)), DateTimeKind.Utc)
                            : endDt;
                        var windowStart = deviceStart > startDt ? deviceStart : startDt;
                        var windowEnd = deviceEnd < endDt ? deviceEnd : endDt;
                        var windowMinutes = (windowEnd - windowStart).TotalMinutes;

                        if (windowMinutes <= 0) continue;

                        totalExpected += windowMinutes / interval;
                        totalActual += glucose.Count(r => r.PatientDeviceId == cgm.Id);
                    }

                    // Count unattributed readings with fallback interval
                    var unattributed = glucose.Count(r => r.PatientDeviceId == null);
                    if (unattributed > 0 && cgmDevices.Count == 0)
                    {
                        var fallbackSource = glucose.FirstOrDefault(r => r.PatientDeviceId == null)?.DataSource;
                        var fallbackInterval = DataSources.GetDefaultUpdateIntervalMinutes(fallbackSource);
                        totalExpected += (endDt - startDt).TotalMinutes / fallbackInterval;
                        totalActual += unattributed;
                    }

                    cgmActivePercent = totalExpected > 0
                        ? Math.Min(Math.Round(totalActual / totalExpected * 100.0, 1), 100.0)
                        : null;
                }
                else
                {
                    // No device registered — use AnalyzeGlucoseData with defaults
                    var analytics = _statisticsService.AnalyzeGlucoseData(
                        glucose, Enumerable.Empty<Bolus>(), Enumerable.Empty<CarbIntake>(),
                        startDate: startDt, endDate: endDt);
                    cgmActivePercent = analytics.DataQuality.CgmActivePercent;
                }
            }

            // Get target range from target range schedule repository
            double? targetLow = null;
            double? targetHigh = null;
            try
            {
                var activeSchedule = await GetActiveTargetRangeScheduleAsync(HttpContext.RequestAborted);
                if (activeSchedule?.Entries.Count > 0)
                {
                    targetLow = activeSchedule.Entries.Min(e => e.Low);
                    targetHigh = activeSchedule.Entries.Max(e => e.High);
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
                cgmDeviceNames,
                pumpDeviceNames,
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
