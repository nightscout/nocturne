using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Attributes;
using OpenApi.Remote.Attributes;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;

namespace Nocturne.API.Controllers.V1;

/// <summary>
/// IOB (Insulin on Board) controller providing calculation endpoints.
/// Implements IOB calculation endpoints compatible with Nightscout legacy behavior.
/// </summary>
/// <seealso cref="IIobCalculator"/>
[ApiController]
[Route("api/v1/[controller]")]
[Produces("application/json")]
[ClientPropertyName("iob")]
public class IobController : ControllerBase
{
    private readonly IIobCalculator _iobCalculator;
    private readonly IBolusRepository _bolusRepository;
    private readonly ITempBasalRepository _tempBasalRepository;
    private readonly ILogger<IobController> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="IobController"/>.
    /// </summary>
    /// <param name="iobCalculator">Calculator for insulin-on-board computations.</param>
    /// <param name="bolusRepository">Repository for bolus records.</param>
    /// <param name="tempBasalRepository">Repository for temp basal records.</param>
    /// <param name="logger">Logger instance.</param>
    public IobController(
        IIobCalculator iobCalculator,
        IBolusRepository bolusRepository,
        ITempBasalRepository tempBasalRepository,
        ILogger<IobController> logger
    )
    {
        _iobCalculator = iobCalculator;
        _bolusRepository = bolusRepository;
        _tempBasalRepository = tempBasalRepository;
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

            // Query boluses and temp basals from the last 8 hours to cover any DIA
            var diaHoursAgo = DateTime.UtcNow.AddHours(-8);
            var boluses = (await _bolusRepository.GetAsync(
                from: diaHoursAgo, to: null, device: null, source: null,
                limit: 1000, offset: 0, descending: false, ct: cancellationToken
            )).ToList();
            var tempBasals = (await _tempBasalRepository.GetAsync(
                from: diaHoursAgo, to: null, device: null, source: null,
                limit: 1000, offset: 0, descending: false, ct: cancellationToken
            )).ToList();

            // Calculate IOB using the calculator
            var iobResult = await _iobCalculator.CalculateTotalAsync(
                boluses,
                tempBasals,
                calculationTime,
                ct: cancellationToken
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

            // Query boluses from the last 8 hours to cover any DIA
            var diaHoursAgo = DateTime.UtcNow.AddHours(-8);
            var boluses = (await _bolusRepository.GetAsync(
                from: diaHoursAgo, to: null, device: null, source: null,
                limit: 1000, offset: 0, descending: false, ct: cancellationToken
            )).ToList();

            // Calculate IOB from boluses only
            var iobResult = _iobCalculator.FromBoluses(boluses, calculationTime);

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

            // Query boluses covering the full calculation window plus 8h DIA before it
            var fetchFrom = DateTimeOffset.FromUnixTimeMilliseconds(calculationStartTime).UtcDateTime.AddHours(-8);
            var boluses = (await _bolusRepository.GetAsync(
                from: fetchFrom, to: null, device: null, source: null,
                limit: 2000, offset: 0, descending: false, ct: cancellationToken
            )).ToList();

            var hourlyData = new List<HourlyIobData>();
            var totalIntervals = (hours * 60) / intervalMinutes;

            for (int i = 0; i < totalIntervals; i++)
            {
                var timeSlot = calculationStartTime + (i * intervalMinutes * 60 * 1000);
                var timeStamp = DateTimeOffset.FromUnixTimeMilliseconds(timeSlot);

                // Filter boluses relevant to this time point
                var relevantBoluses = boluses.Where(b => b.Mills <= timeSlot).ToList();

                // Calculate IOB at this time point
                var iobResult = _iobCalculator.FromBoluses(relevantBoluses, timeSlot);

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
