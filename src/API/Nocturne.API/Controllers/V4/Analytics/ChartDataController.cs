using Microsoft.AspNetCore.Mvc;
using OpenApi.Remote.Attributes;
using Nocturne.Core.Contracts.Analytics;
using Nocturne.Core.Models;

namespace Nocturne.API.Controllers.V4.Analytics;

/// <summary>
/// Controller for providing pre-computed chart data for the dashboard.
/// Returns all data needed by the glucose chart in a single call:
/// glucose readings, IOB/COB series, basal delivery, treatment markers,
/// state spans, system events, and tracker markers.
/// </summary>
/// <remarks>
/// Responses are cached for 60 seconds, varying by query keys,
/// to avoid redundant recalculation when the browser reconnects.
/// </remarks>
/// <seealso cref="IChartDataService"/>
/// <seealso cref="DashboardChartData"/>
[ApiController]
[Tags("Analytics")]
[Route("api/v4/[controller]")]
[Produces("application/json")]
public class ChartDataController : ControllerBase
{
    private readonly IChartDataService _chartDataService;
    private readonly ILogger<ChartDataController> _logger;

    public ChartDataController(
        IChartDataService chartDataService,
        ILogger<ChartDataController> logger
    )
    {
        _chartDataService = chartDataService;
        _logger = logger;
    }

    /// <summary>
    /// Gets complete dashboard chart data in a single call.
    /// Returns pre-calculated IOB, COB, basal series, categorized treatment markers,
    /// state spans, system events, tracker markers, and glucose readings.
    /// </summary>
    /// <param name="startTime">Start of the requested window as a Unix timestamp in milliseconds.</param>
    /// <param name="endTime">End of the requested window as a Unix timestamp in milliseconds.
    /// Must be greater than <paramref name="startTime"/>.</param>
    /// <param name="intervalMinutes">Granularity of the returned series in minutes (1–60, default 5).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A fully populated <see cref="DashboardChartData"/> object.</returns>
    /// <exception cref="Exception">Returns HTTP 500 if chart data calculation fails.</exception>
    [HttpGet("dashboard")]
    [RemoteQuery]
    [ResponseCache(Duration = 60, VaryByQueryKeys = new[] { "*" })]
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
                return Problem(detail: "endTime must be greater than startTime", statusCode: 400, title: "Bad Request");

            if (intervalMinutes < 1 || intervalMinutes > 60)
                return Problem(detail: "intervalMinutes must be between 1 and 60", statusCode: 400, title: "Bad Request");

            var result = await _chartDataService.GetDashboardChartDataAsync(
                startTime,
                endTime,
                intervalMinutes,
                cancellationToken
            );

            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error calculating dashboard chart data");
            return Problem(detail: "Internal server error", statusCode: 500, title: "Internal Server Error");
        }
    }
}
