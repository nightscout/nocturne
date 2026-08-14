using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Attributes;
using Nocturne.API.Authorization;
using Nocturne.API.Extensions;
using Nocturne.Core.Contracts.Analytics;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using OpenApi.Remote.Attributes;

namespace Nocturne.API.Controllers.V4.Analytics;

/// <summary>
/// Lean data feed for the actogram report (sleep, steps, and heart-rate variants).
/// Returns glucose, thresholds, heart rates, step counts, and sleep spans for a window.
/// </summary>
/// <remarks>
/// Distinct from <see cref="ChartDataController"/>: the actogram does not render IOB,
/// COB, basal series, or treatment markers, so the dashboard pipeline (which is O(n·m)
/// in interval-ticks × temp basals on wide windows) is bypassed entirely.
/// </remarks>
/// <seealso cref="IActogramReportService"/>
/// <seealso cref="ActogramReportData"/>
/// <seealso cref="ActogramReadScopeGuard"/>
[ApiController]
[Tags("Analytics")]
[Route("api/v4/[controller]")]
[Produces("application/json")]
public class ActogramController : ControllerBase
{
    private readonly IActogramReportService _service;
    private readonly ILogger<ActogramController> _logger;

    public ActogramController(
        IActogramReportService service,
        ILogger<ActogramController> logger
    )
    {
        _service = service;
        _logger = logger;
    }

    /// <summary>
    /// Get actogram report data for a time window.
    /// </summary>
    /// <param name="startTime">Start of the window as Unix milliseconds (inclusive).</param>
    /// <param name="endTime">End of the window as Unix milliseconds (exclusive).
    /// Must be greater than <paramref name="startTime"/>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [HttpGet]
    [RemoteQuery]
    [RequireScope(
        OAuthScopes.GlucoseRead,
        OAuthScopes.HeartRateRead,
        OAuthScopes.StepCountRead,
        OAuthScopes.SleepRead)]
    [ResponseCache(Duration = 60, Location = ResponseCacheLocation.Client)]
    [ProducesResponseType(typeof(ActogramReportData), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<ActogramReportData>> GetActogram(
        [FromQuery] long startTime,
        [FromQuery] long endTime,
        CancellationToken cancellationToken = default
    )
    {
        if (endTime <= startTime)
            return Problem(detail: "endTime must be greater than startTime", statusCode: 400, title: "Bad Request");

        try
        {
            var result = await _service.GetAsync(startTime, endTime, cancellationToken);
            return Ok(ActogramReadScopeGuard.Redact(result, HttpContext.GetGrantedScopes()));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error building actogram report for window {Start}-{End}", startTime, endTime);
            return Problem(detail: "Internal server error", statusCode: 500, title: "Internal Server Error");
        }
    }
}
