using Microsoft.AspNetCore.Mvc;
using OpenApi.Remote.Attributes;
using Nocturne.API.Attributes;
using Nocturne.API.Authorization;
using Nocturne.API.Extensions;
using Nocturne.Core.Contracts.Analytics;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;

namespace Nocturne.API.Controllers.V4.Analytics;

/// <summary>
/// Controller for providing pre-computed chart data for the dashboard.
/// Returns all data needed by the glucose chart in a single call:
/// glucose readings, IOB/COB series, basal delivery, treatment markers,
/// state spans, system events, and tracker markers.
/// </summary>
/// <remarks>
/// Responses are cached for 60 seconds in the caller's own client cache, so a browser that
/// reconnects does not force a recalculation. The cache is deliberately private:
/// <see cref="ChartDataReadScopeGuard"/> makes the body depend on the caller's scopes, and the
/// shared response cache keys only on host, query and <c>Cookie</c>, so a credential that presents
/// neither a cookie nor an <c>Authorization</c> header — the legacy <c>api-secret</c> header —
/// would otherwise be served another credential's unredacted body.
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
    [RequireScope(
        Scope.GlucoseRead,
        Scope.TreatmentsRead,
        Scope.DevicesRead,
        Scope.TherapyRead,
        Scope.HeartRateRead,
        Scope.StepCountRead,
        Scope.SleepRead)]
    [ResponseCache(Duration = 60, Location = ResponseCacheLocation.Client)]
    [ProducesResponseType(typeof(DashboardChartData), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    [ErrorEnvelope]
    public async Task<ActionResult<DashboardChartData>> GetDashboardChartData(
        [FromQuery] long startTime,
        [FromQuery] long endTime,
        [FromQuery] int intervalMinutes = 5,
        CancellationToken cancellationToken = default
    )
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

        return Ok(ChartDataReadScopeGuard.Redact(result, HttpContext.GetGrantedScopes()));
    }

    /// <summary>
    /// Gets the basal delivery series for a time window without running the
    /// full IOB/COB compute pipeline. Fetches only temp basals and profile
    /// data, making it significantly cheaper than the dashboard endpoint.
    /// </summary>
    /// <param name="startTime">Start of the requested window as a Unix timestamp in milliseconds.</param>
    /// <param name="endTime">End of the requested window as a Unix timestamp in milliseconds.
    /// Must be greater than <paramref name="startTime"/>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A list of <see cref="BasalPoint"/> representing basal delivery over time.</returns>
    [HttpGet("basal-series")]
    [RemoteQuery]
    [RequireScope(Scope.TreatmentsRead)]
    [ResponseCache(Duration = 60, VaryByQueryKeys = new[] { "*" })]
    [ProducesResponseType(typeof(List<BasalPoint>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    [ErrorEnvelope]
    public async Task<ActionResult<List<BasalPoint>>> GetBasalSeries(
        [FromQuery] long startTime,
        [FromQuery] long endTime,
        CancellationToken cancellationToken = default
    )
    {
        if (endTime <= startTime)
            return Problem(detail: "endTime must be greater than startTime", statusCode: 400, title: "Bad Request");

        var basalSeries = await _chartDataService.GetBasalSeriesAsync(startTime, endTime, cancellationToken);

        return Ok(basalSeries);
    }
}
