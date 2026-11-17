using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Attributes;
using Nocturne.API.Services.Analytics;
using Nocturne.Core.Clinical.Treatments;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models;
using OpenApi.Remote.Attributes;

namespace Nocturne.API.Controllers.V1;

/// <summary>
/// IOB (Insulin on Board) controller providing calculation endpoints.
/// Implements IOB calculation endpoints compatible with Nightscout legacy behavior.
/// </summary>
/// <seealso cref="IIobCalculator"/>
[ApiController]
[Tags("V1")]
[Route("api/v1/[controller]")]
[Produces("application/json")]
[ClientPropertyName("iob")]
public class IobController : ControllerBase
{
    private readonly IIobCalculator _iobCalculator;
    private readonly IIobCobReader _iobCobReader;
    private readonly IIobCobSeriesReader _iobCobSeriesReader;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<IobController> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="IobController"/>.
    /// </summary>
    /// <param name="iobCalculator">Calculator for insulin-on-board computations.</param>
    /// <param name="iobCobReader">Reader of the total IOB at an instant.</param>
    /// <param name="iobCobSeriesReader">Reader of the hourly series.</param>
    /// <param name="timeProvider">Clock for the default calculation time and the end of the hourly window.</param>
    /// <param name="logger">Logger instance.</param>
    public IobController(
        IIobCalculator iobCalculator,
        IIobCobReader iobCobReader,
        IIobCobSeriesReader iobCobSeriesReader,
        TimeProvider timeProvider,
        ILogger<IobController> logger
    )
    {
        _iobCalculator = iobCalculator;
        _iobCobReader = iobCobReader;
        _iobCobSeriesReader = iobCobSeriesReader;
        _timeProvider = timeProvider;
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
    [RequireScope(Scope.TreatmentsRead)]
    public async Task<ActionResult<IobResult>> GetCurrentIob(
        [FromQuery] long? time = null,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            var calculationTime = time ?? _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

            var reading = await _iobCobReader.ReadTotalAsync(calculationTime, live: time is null, cancellationToken);

            return Ok(reading.Iob);
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
    [RequireScope(Scope.TreatmentsRead)]
    public async Task<ActionResult<IobResult>> GetIobFromTreatments(
        [FromQuery] long? time = null,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            var calculationTime = time ?? _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

            var (boluses, tempBasals, _) = await _iobCobReader.ReadInputsAsync(calculationTime, cancellationToken);

            var iobResult = await _iobCalculator.CalculateFromTreatmentsAsync(
                boluses, tempBasals, calculationTime, cancellationToken);

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
    [RequireScope(Scope.TreatmentsRead)]
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

            var endTime = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
            var calculationStartTime = startTime ?? (endTime - (hours * 60 * 60 * 1000));

            var totalIntervals = (hours * 60) / intervalMinutes;
            var intervalMs = intervalMinutes * 60 * 1000L;
            var lastSlot = calculationStartTime + (totalIntervals - 1) * intervalMs;

            var series = await _iobCobSeriesReader.ReadSeriesAsync(
                new IobCobSeriesWindow(calculationStartTime, lastSlot, intervalMs), endTime, readCarbs: false, cancellationToken);

            var hourlyData = series.Ticks.Select(tick =>
            {
                var timeStamp = DateTimeOffset.FromUnixTimeMilliseconds(tick.Mills);
                return new HourlyIobData
                {
                    TimeSlot = tick.Mills,
                    Hour = timeStamp.Hour,
                    Minute = timeStamp.Minute,
                    TimeLabel = timeStamp.ToString("HH:mm"),
                    TotalIOB = tick.Iob,
                    BolusIOB = tick.BolusIob ?? 0,
                    BasalIOB = tick.BasalIob ?? 0,
                };
            }).ToList();

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
