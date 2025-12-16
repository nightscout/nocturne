using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Services;
using Nocturne.Core.Contracts;
using Nocturne.Core.Models;

namespace Nocturne.API.Controllers.V4;

/// <summary>
/// Controller for providing chart data with server-side calculations
/// Provides pre-computed IOB, COB, and basal time series for dashboard charts
/// </summary>
[ApiController]
[Route("api/v4/[controller]")]
[Produces("application/json")]
[Tags("V4 Chart Data")]
public class ChartDataController : ControllerBase
{
    private readonly IIobService _iobService;
    private readonly ICobService _cobService;
    private readonly ITreatmentService _treatmentService;
    private readonly IDeviceStatusService _deviceStatusService;
    private readonly IProfileService _profileService;
    private readonly IProfileDataService _profileDataService;
    private readonly ILogger<ChartDataController> _logger;

    public ChartDataController(
        IIobService iobService,
        ICobService cobService,
        ITreatmentService treatmentService,
        IDeviceStatusService deviceStatusService,
        IProfileService profileService,
        IProfileDataService profileDataService,
        ILogger<ChartDataController> logger
    )
    {
        _iobService = iobService;
        _cobService = cobService;
        _treatmentService = treatmentService;
        _deviceStatusService = deviceStatusService;
        _profileService = profileService;
        _profileDataService = profileDataService;
        _logger = logger;
    }

    /// <summary>
    /// Get dashboard chart data with pre-calculated IOB, COB, and basal time series
    /// </summary>
    /// <param name="startTime">Start time in Unix milliseconds</param>
    /// <param name="endTime">End time in Unix milliseconds</param>
    /// <param name="intervalMinutes">Interval for IOB/COB calculations (default: 5)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Chart data with all calculated series</returns>
    [HttpGet("dashboard")]
    [ProducesResponseType(typeof(DashboardChartData), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<DashboardChartData>> GetDashboardChartData(
        [FromQuery] long startTime,
        [FromQuery] long endTime,
        [FromQuery] int intervalMinutes = 5,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            if (endTime <= startTime)
            {
                return BadRequest(new { error = "endTime must be greater than startTime" });
            }

            if (intervalMinutes < 1 || intervalMinutes > 60)
            {
                return BadRequest(new { error = "intervalMinutes must be between 1 and 60" });
            }

            // Fetch and load profile data into the profile service
            var profiles = await _profileDataService.GetProfilesAsync(
                count: 100,
                cancellationToken: cancellationToken
            );
            var profileList = profiles?.ToList() ?? new List<Profile>();
            if (profileList.Any())
            {
                _profileService.LoadData(profileList);
                _logger.LogDebug("Loaded {Count} profiles into profile service", profileList.Count);
            }

            // Fetch treatments for the period (with buffer for IOB/COB calculation)
            var bufferMs = 8 * 60 * 60 * 1000; // 8 hours buffer for IOB
            var treatments = await _treatmentService.GetTreatmentsAsync(
                count: 5000,
                skip: 0,
                cancellationToken: cancellationToken
            );

            // Filter treatments to relevant time range (with buffer)
            var relevantTreatments =
                treatments
                    ?.Where(t => t.Mills >= (startTime - bufferMs) && t.Mills <= endTime)
                    .ToList() ?? new List<Treatment>();

            // Fetch device status for the period
            var deviceStatus = await _deviceStatusService.GetDeviceStatusAsync(
                count: 1000,
                skip: 0,
                cancellationToken: cancellationToken
            );

            var deviceStatusList = deviceStatus?.ToList() ?? new List<DeviceStatus>();

            // Get default basal rate from profile at end time
            var defaultBasalRate = _profileService.HasData()
                ? _profileService.GetBasalRate(endTime, null)
                : 1.0;

            // Calculate IOB and COB series at each interval
            var iobSeries = new List<TimeSeriesPoint>();
            var cobSeries = new List<TimeSeriesPoint>();
            var intervalMs = intervalMinutes * 60 * 1000;

            double maxIob = 0;
            double maxCob = 0;

            for (long t = startTime; t <= endTime; t += intervalMs)
            {
                // Calculate IOB at this time
                var iobResult = _iobService.FromTreatments(
                    relevantTreatments,
                    _profileService.HasData() ? _profileService : null,
                    t,
                    null
                );

                var iob = iobResult.Iob;
                iobSeries.Add(new TimeSeriesPoint { Timestamp = t, Value = iob });
                if (iob > maxIob)
                    maxIob = iob;

                // Calculate COB at this time
                var cobResult = _cobService.CobTotal(
                    relevantTreatments,
                    deviceStatusList,
                    _profileService.HasData() ? _profileService : null,
                    t,
                    null
                );

                var cob = cobResult.Cob;
                cobSeries.Add(new TimeSeriesPoint { Timestamp = t, Value = cob });
                if (cob > maxCob)
                    maxCob = cob;
            }

            // Build basal series from temp basal treatments
            var basalSeries = BuildBasalSeries(
                relevantTreatments,
                startTime,
                endTime,
                defaultBasalRate
            );

            var maxBasalRate = Math.Max(
                defaultBasalRate * 2.5,
                basalSeries.Any() ? basalSeries.Max(b => b.Rate) : defaultBasalRate
            );

            return Ok(
                new DashboardChartData
                {
                    IobSeries = iobSeries,
                    CobSeries = cobSeries,
                    BasalSeries = basalSeries,
                    DefaultBasalRate = defaultBasalRate,
                    MaxBasalRate = maxBasalRate,
                    MaxIob = Math.Max(3, maxIob),
                    MaxCob = Math.Max(30, maxCob),
                }
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error calculating dashboard chart data");
            return StatusCode(500, new { error = "Internal server error" });
        }
    }

    /// <summary>
    /// Build basal series using the profile service for accurate scheduled rates and temp basals.
    /// Uses IProfileService.GetTempBasal() which correctly handles:
    /// - Profile basal schedule lookups at each time point
    /// - Active profile switches over time
    /// - Temp basal treatments (absolute and percent-based)
    /// - Combo bolus basal contributions
    /// </summary>
    private List<BasalPoint> BuildBasalSeries(
        List<Treatment> treatments,
        long startTime,
        long endTime,
        double defaultBasalRate
    )
    {
        var series = new List<BasalPoint>();

        // Update profile service with temp basal treatments for accurate lookups
        // Use case-insensitive Contains to match "Temp Basal", "temp basal", etc.
        var tempBasalTreatments = treatments
            .Where(t => !string.IsNullOrEmpty(t.EventType) &&
                        t.EventType.Contains("Temp Basal", StringComparison.OrdinalIgnoreCase))
            .ToList();

        _logger.LogDebug(
            "Found {TempBasalCount} temp basal treatments in time range. Sample: {Sample}",
            tempBasalTreatments.Count,
            tempBasalTreatments.FirstOrDefault() is { } first
                ? $"Mills={first.Mills}, Rate={first.Rate}, Absolute={first.Absolute}, Duration={first.Duration}"
                : "none"
        );

        var comboBolusTreatments = treatments.Where(t => t.EventType == "Combo Bolus").ToList();

        var profileSwitchTreatments = treatments
            .Where(t => t.EventType == "Profile Switch")
            .ToList();

        _profileService.UpdateTreatments(
            profileSwitchTreatments,
            tempBasalTreatments,
            comboBolusTreatments
        );

        // Use 5-minute intervals for the basal series (same as IOB/COB)
        const long intervalMs = 5 * 60 * 1000;

        // Track previous values to only add points when rate or temp status changes
        double? prevRate = null;
        double? prevScheduledRate = null;
        bool? prevIsTemp = null;

        for (long t = startTime; t <= endTime; t += intervalMs)
        {
            double rate;
            double scheduledRate;
            bool isTemp;

            if (_profileService.HasData())
            {
                // Use GetTempBasal which handles:
                // - Looking up the scheduled basal rate from the active profile at this time
                // - Checking for active temp basals and applying absolute or percent adjustments
                // - Including combo bolus basal contributions
                var tempBasalResult = _profileService.GetTempBasal(t, null);

                // TotalBasal includes temp basal + combo bolus contributions
                rate = tempBasalResult.TotalBasal;

                // Basal is the scheduled rate from the profile (without temp basal)
                scheduledRate = tempBasalResult.Basal;

                // IsTemp is true if there's an active temp basal treatment
                isTemp = tempBasalResult.Treatment != null;
            }
            else
            {
                // No profile data - fall back to default rate
                rate = defaultBasalRate;
                scheduledRate = defaultBasalRate;
                isTemp = false;
            }

            // Add point if rate, scheduled rate, or temp status changed (or first point)
            if (
                prevRate == null
                || Math.Abs(rate - prevRate.Value) > 0.001
                || Math.Abs(scheduledRate - (prevScheduledRate ?? 0)) > 0.001
                || isTemp != prevIsTemp
            )
            {
                series.Add(
                    new BasalPoint
                    {
                        Timestamp = t,
                        Rate = rate,
                        ScheduledRate = scheduledRate,
                        IsTemp = isTemp,
                    }
                );

                prevRate = rate;
                prevScheduledRate = scheduledRate;
                prevIsTemp = isTemp;
            }
        }

        // Ensure we have at least one point
        if (series.Count == 0)
        {
            series.Add(
                new BasalPoint
                {
                    Timestamp = startTime,
                    Rate = defaultBasalRate,
                    ScheduledRate = defaultBasalRate,
                    IsTemp = false,
                }
            );
        }

        // Ensure we end at endTime
        var lastPoint = series.Last();
        if (lastPoint.Timestamp < endTime)
        {
            series.Add(
                new BasalPoint
                {
                    Timestamp = endTime,
                    Rate = lastPoint.Rate,
                    ScheduledRate = lastPoint.ScheduledRate,
                    IsTemp = lastPoint.IsTemp,
                }
            );
        }

        return series;
    }
}

/// <summary>
/// Dashboard chart data response with all calculated series
/// </summary>
public class DashboardChartData
{
    /// <summary>
    /// IOB (Insulin on Board) time series
    /// </summary>
    public List<TimeSeriesPoint> IobSeries { get; set; } = new();

    /// <summary>
    /// COB (Carbs on Board) time series
    /// </summary>
    public List<TimeSeriesPoint> CobSeries { get; set; } = new();

    /// <summary>
    /// Basal rate time series with temp basal indicators
    /// </summary>
    public List<BasalPoint> BasalSeries { get; set; } = new();

    /// <summary>
    /// Default basal rate from profile (U/hr)
    /// </summary>
    public double DefaultBasalRate { get; set; }

    /// <summary>
    /// Maximum basal rate in the series (for Y-axis scaling)
    /// </summary>
    public double MaxBasalRate { get; set; }

    /// <summary>
    /// Maximum IOB in the series (for Y-axis scaling)
    /// </summary>
    public double MaxIob { get; set; }

    /// <summary>
    /// Maximum COB in the series (for Y-axis scaling)
    /// </summary>
    public double MaxCob { get; set; }
}

/// <summary>
/// Time series data point with timestamp and value
/// </summary>
public class TimeSeriesPoint
{
    /// <summary>
    /// Timestamp in Unix milliseconds
    /// </summary>
    public long Timestamp { get; set; }

    /// <summary>
    /// Value at this timestamp
    /// </summary>
    public double Value { get; set; }
}

/// <summary>
/// Basal rate data point
/// </summary>
public class BasalPoint
{
    /// <summary>
    /// Timestamp in Unix milliseconds
    /// </summary>
    public long Timestamp { get; set; }

    /// <summary>
    /// Effective basal rate in U/hr (includes temp basals and combo bolus)
    /// </summary>
    public double Rate { get; set; }

    /// <summary>
    /// Scheduled basal rate from profile in U/hr (without temp basal modifications)
    /// </summary>
    public double ScheduledRate { get; set; }

    /// <summary>
    /// Whether this is a temporary basal rate
    /// </summary>
    public bool IsTemp { get; set; }
}
