using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Services;
using Nocturne.Core.Contracts;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Repositories;

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
    private readonly StateSpanRepository _stateSpanRepository;
    private readonly ILogger<ChartDataController> _logger;

    public ChartDataController(
        IIobService iobService,
        ICobService cobService,
        ITreatmentService treatmentService,
        IDeviceStatusService deviceStatusService,
        IProfileService profileService,
        IProfileDataService profileDataService,
        StateSpanRepository stateSpanRepository,
        ILogger<ChartDataController> logger
    )
    {
        _iobService = iobService;
        _cobService = cobService;
        _treatmentService = treatmentService;
        _deviceStatusService = deviceStatusService;
        _profileService = profileService;
        _profileDataService = profileDataService;
        _stateSpanRepository = stateSpanRepository;
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

            // Fetch StateSpans for pump modes, basal delivery, and profiles
            var pumpModeSpans = await _stateSpanRepository.GetByCategory(
                StateSpanCategory.PumpMode, startTime, endTime, cancellationToken);
            var basalDeliverySpans = await _stateSpanRepository.GetByCategory(
                StateSpanCategory.BasalDelivery, startTime, endTime, cancellationToken);
            var profileSpans = await _stateSpanRepository.GetByCategory(
                StateSpanCategory.Profile, startTime, endTime, cancellationToken);

            // Build basal series from BasalDelivery StateSpans (primary) with profile fallback
            var basalSeries = BuildBasalSeriesFromStateSpans(
                basalDeliverySpans.ToList(),
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
                    PumpModeSpans = pumpModeSpans.ToList(),
                    BasalDeliverySpans = basalDeliverySpans.ToList(),
                    ProfileSpans = profileSpans.ToList(),
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
    /// Build basal series from BasalDelivery StateSpans.
    /// StateSpans are the source of truth for pump-confirmed delivery.
    /// Falls back to profile-based rates when there are gaps in StateSpan data.
    /// </summary>
    private List<BasalPoint> BuildBasalSeriesFromStateSpans(
        List<StateSpan> basalDeliverySpans,
        long startTime,
        long endTime,
        double defaultBasalRate
    )
    {
        var series = new List<BasalPoint>();

        // Sort spans by start time
        var sortedSpans = basalDeliverySpans
            .OrderBy(s => s.StartMills)
            .ToList();

        _logger.LogDebug(
            "Building basal series from {SpanCount} BasalDelivery StateSpans",
            sortedSpans.Count
        );

        if (sortedSpans.Count == 0)
        {
            // No StateSpan data - fall back to profile or default
            return BuildBasalSeriesFromProfile(startTime, endTime, defaultBasalRate);
        }

        long currentTime = startTime;

        foreach (var span in sortedSpans)
        {
            var spanStart = span.StartMills;
            var spanEnd = span.EndMills ?? endTime;

            // Skip spans entirely outside our range
            if (spanEnd < startTime || spanStart > endTime)
                continue;

            // Clamp to our range
            spanStart = Math.Max(spanStart, startTime);
            spanEnd = Math.Min(spanEnd, endTime);

            // If there's a gap before this span, fill with profile-based rates
            if (spanStart > currentTime)
            {
                var gapPoints = BuildBasalSeriesFromProfile(currentTime, spanStart, defaultBasalRate);
                series.AddRange(gapPoints);
            }

            // Extract rate and origin from metadata
            double rate = defaultBasalRate;
            if (span.Metadata?.TryGetValue("rate", out var rateObj) == true)
            {
                rate = rateObj switch
                {
                    System.Text.Json.JsonElement jsonElement => jsonElement.GetDouble(),
                    double d => d,
                    _ => Convert.ToDouble(rateObj)
                };
            }

            string? originStr = "Scheduled";
            if (span.Metadata?.TryGetValue("origin", out var originObj) == true)
            {
                originStr = originObj switch
                {
                    System.Text.Json.JsonElement jsonElement => jsonElement.GetString(),
                    string s => s,
                    _ => originObj?.ToString()
                };
            }

            // Parse origin string to enum
            var origin = originStr?.ToLowerInvariant() switch
            {
                "algorithm" => BasalDeliveryOrigin.Algorithm,
                "manual" => BasalDeliveryOrigin.Manual,
                "suspended" => BasalDeliveryOrigin.Suspended,
                _ => BasalDeliveryOrigin.Scheduled
            };

            // Get scheduled rate from profile for comparison
            var scheduledRate = _profileService.HasData()
                ? _profileService.GetBasalRate(spanStart, null)
                : defaultBasalRate;

            // Add point at span start
            series.Add(new BasalPoint
            {
                Timestamp = spanStart,
                Rate = origin == BasalDeliveryOrigin.Suspended ? 0 : rate,
                ScheduledRate = scheduledRate,
                Origin = origin,
            });

            currentTime = spanEnd;
        }

        // If there's remaining time after the last span, fill with profile
        if (currentTime < endTime)
        {
            var tailPoints = BuildBasalSeriesFromProfile(currentTime, endTime, defaultBasalRate);
            series.AddRange(tailPoints);
        }

        // Ensure we have at least one point
        if (series.Count == 0)
        {
            series.Add(new BasalPoint
            {
                Timestamp = startTime,
                Rate = defaultBasalRate,
                ScheduledRate = defaultBasalRate,
                Origin = BasalDeliveryOrigin.Scheduled,
            });
        }

        return series;
    }

    /// <summary>
    /// Build basal series from profile data (fallback when no StateSpan data).
    /// </summary>
    private List<BasalPoint> BuildBasalSeriesFromProfile(
        long startTime,
        long endTime,
        double defaultBasalRate
    )
    {
        var series = new List<BasalPoint>();
        const long intervalMs = 5 * 60 * 1000; // 5-minute intervals

        double? prevRate = null;

        for (long t = startTime; t <= endTime; t += intervalMs)
        {
            var rate = _profileService.HasData()
                ? _profileService.GetBasalRate(t, null)
                : defaultBasalRate;

            // Only add point if rate changed
            if (prevRate == null || Math.Abs(rate - prevRate.Value) > 0.001)
            {
                series.Add(new BasalPoint
                {
                    Timestamp = t,
                    Rate = rate,
                    ScheduledRate = rate,
                    Origin = BasalDeliveryOrigin.Inferred,
                });
                prevRate = rate;
            }
        }

        // Ensure at least one point
        if (series.Count == 0)
        {
            series.Add(new BasalPoint
            {
                Timestamp = startTime,
                Rate = defaultBasalRate,
                ScheduledRate = defaultBasalRate,
                Origin = BasalDeliveryOrigin.Inferred,
            });
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

    /// <summary>
    /// Pump mode state spans for chart background coloring
    /// </summary>
    public List<StateSpan> PumpModeSpans { get; set; } = new();

    /// <summary>
    /// Basal delivery state spans (pump-confirmed delivery data)
    /// </summary>
    public List<StateSpan> BasalDeliverySpans { get; set; } = new();

    /// <summary>
    /// Profile state spans showing active profile changes
    /// </summary>
    public List<StateSpan> ProfileSpans { get; set; } = new();
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
    /// Effective basal rate in U/hr
    /// </summary>
    public double Rate { get; set; }

    /// <summary>
    /// Scheduled basal rate from profile in U/hr
    /// </summary>
    public double ScheduledRate { get; set; }

    /// <summary>
    /// Origin of this basal rate - where it came from
    /// </summary>
    public BasalDeliveryOrigin Origin { get; set; }
}
