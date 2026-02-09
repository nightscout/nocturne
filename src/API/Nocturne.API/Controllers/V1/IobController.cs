using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Attributes;
using Nocturne.Core.Contracts;
using Nocturne.Core.Models;

namespace Nocturne.API.Controllers.V1;

/// <summary>
/// IOB (Insulin on Board) controller providing calculation endpoints
/// Implements IOB calculation endpoints compatible with Nightscout legacy behavior
/// </summary>
[ApiController]
[Route("api/v1/[controller]")]
[Produces("application/json")]
[Tags("V1 IOB")]
[ClientPropertyName("iob")]
public class IobController : ControllerBase
{
    private readonly IIobService _iobService;
    private readonly ITreatmentService _treatmentService;
    private readonly IDeviceStatusService _deviceStatusService;
    private readonly ILogger<IobController> _logger;

    public IobController(
        IIobService iobService,
        ITreatmentService treatmentService,
        IDeviceStatusService deviceStatusService,
        ILogger<IobController> logger
    )
    {
        _iobService = iobService;
        _treatmentService = treatmentService;
        _deviceStatusService = deviceStatusService;
        _logger = logger;
    }

    /// <summary>
    /// Calculate current IOB from treatments and device status
    /// </summary>
    /// <param name="time">Optional timestamp for calculation (default: current time)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Current IOB calculation result</returns>
    /// <response code="200">Returns the current IOB calculation</response>
    /// <response code="500">If there was an internal server error</response>
    [HttpGet]
    [NightscoutEndpoint("/api/v1/iob")]
    [ProducesResponseType(typeof(IobResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<IobResult>> GetCurrentIob(
        [FromQuery] long? time = null,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            var calculationTime = time ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            // Get recent treatments (last 8 hours to cover DIA)
            var treatments = await _treatmentService.GetTreatmentsAsync(
                count: 1000,
                skip: 0,
                cancellationToken: cancellationToken
            );

            // Get recent device status
            var deviceStatus = await _deviceStatusService.GetDeviceStatusAsync(
                count: 10,
                skip: 0,
                cancellationToken: cancellationToken
            );

            // Calculate IOB using the service
            var iobResult = _iobService.CalculateTotal(
                treatments?.ToList() ?? new List<Treatment>(),
                deviceStatus?.ToList() ?? new List<DeviceStatus>(),
                profile: null,
                calculationTime
            );

            return Ok(iobResult);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error calculating current IOB");
            return StatusCode(500, new { error = "Internal server error" });
        }
    }

    /// <summary>
    /// Calculate IOB from treatments only (excluding device status)
    /// </summary>
    /// <param name="time">Optional timestamp for calculation (default: current time)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>IOB calculation result from treatments only</returns>
    /// <response code="200">Returns the IOB calculation from treatments</response>
    /// <response code="500">If there was an internal server error</response>
    [HttpGet("treatments")]
    [NightscoutEndpoint("/api/v1/iob/treatments")]
    [ProducesResponseType(typeof(IobResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<IobResult>> GetIobFromTreatments(
        [FromQuery] long? time = null,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            var calculationTime = time ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            // Get recent treatments (last 8 hours to cover DIA)
            var treatments = await _treatmentService.GetTreatmentsAsync(
                count: 1000,
                skip: 0,
                cancellationToken: cancellationToken
            );

            // Calculate IOB from treatments only
            var iobResult = _iobService.FromTreatments(
                treatments?.ToList() ?? new List<Treatment>(),
                profile: null,
                calculationTime
            );

            return Ok(iobResult);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error calculating IOB from treatments");
            return StatusCode(500, new { error = "Internal server error" });
        }
    }

    /// <summary>
    /// Calculate hourly IOB breakdown for charts and analysis
    /// </summary>
    /// <param name="intervalMinutes">Time interval in minutes for calculations (default: 5)</param>
    /// <param name="hours">Number of hours to calculate (default: 24)</param>
    /// <param name="startTime">Start time for calculation (default: 24 hours ago)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Hourly IOB breakdown data</returns>
    /// <response code="200">Returns the hourly IOB breakdown</response>
    /// <response code="400">If parameters are invalid</response>
    /// <response code="500">If there was an internal server error</response>
    [HttpGet("hourly")]
    [NightscoutEndpoint("/api/v1/iob/hourly")]
    [ProducesResponseType(typeof(HourlyIobResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<HourlyIobResponse>> GetHourlyIob(
        [FromQuery] int intervalMinutes = 5,
        [FromQuery] int hours = 24,
        [FromQuery] long? startTime = null,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            if (intervalMinutes < 1 || intervalMinutes > 60)
            {
                return BadRequest(new { error = "Interval must be between 1 and 60 minutes" });
            }

            if (hours < 1 || hours > 168) // Max 7 days
            {
                return BadRequest(new { error = "Hours must be between 1 and 168" });
            }

            var endTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var calculationStartTime = startTime ?? (endTime - (hours * 60 * 60 * 1000));

            // Get treatments that could affect IOB during this period
            var treatments = await _treatmentService.GetTreatmentsAsync(
                count: 2000,
                skip: 0,
                cancellationToken: cancellationToken
            );

            var hourlyData = new List<HourlyIobData>();
            var totalIntervals = (hours * 60) / intervalMinutes;

            for (int i = 0; i < totalIntervals; i++)
            {
                var timeSlot = calculationStartTime + (i * intervalMinutes * 60 * 1000);
                var timeStamp = DateTimeOffset.FromUnixTimeMilliseconds(timeSlot);

                // Calculate IOB at this time point
                var iobResult = _iobService.FromTreatments(
                    treatments?.ToList() ?? new List<Treatment>(),
                    profile: null,
                    timeSlot
                );

                hourlyData.Add(
                    new HourlyIobData
                    {
                        TimeSlot = timeSlot,
                        Hour = timeStamp.Hour,
                        Minute = timeStamp.Minute,
                        TimeLabel = timeStamp.ToString("HH:mm"),
                        TotalIOB = iobResult.Iob,
                        BolusIOB = iobResult.Iob - (iobResult.BasalIob ?? 0.0),
                        BasalIOB = iobResult.BasalIob ?? 0.0,
                    }
                );
            }

            var response = new HourlyIobResponse
            {
                StartTime = calculationStartTime,
                EndTime = endTime,
                IntervalMinutes = intervalMinutes,
                Hours = hours,
                Data = hourlyData,
            };

            return Ok(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error calculating hourly IOB");
            return StatusCode(500, new { error = "Internal server error" });
        }
    }
}

/// <summary>
/// Response model for hourly IOB breakdown
/// </summary>
public class HourlyIobResponse
{
    public long StartTime { get; set; }
    public long EndTime { get; set; }
    public int IntervalMinutes { get; set; }
    public int Hours { get; set; }
    public List<HourlyIobData> Data { get; set; } = new();
}

/// <summary>
/// Individual hourly IOB data point
/// </summary>
public class HourlyIobData
{
    public long TimeSlot { get; set; }
    public int Hour { get; set; }
    public int Minute { get; set; }
    public string TimeLabel { get; set; } = string.Empty;
    public double TotalIOB { get; set; }
    public double BolusIOB { get; set; }
    public double BasalIOB { get; set; }
}
