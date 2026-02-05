using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Extensions;
using Nocturne.Core.Contracts;
using Nocturne.Core.Models.Widget;

namespace Nocturne.API.Controllers.V4;

/// <summary>
/// V4 Summary controller providing widget-friendly summary data.
/// Designed for mobile widgets, watch faces, and other constrained displays.
/// </summary>
[ApiController]
[Route("api/v4/summary")]
[Produces("application/json")]
[Tags("V4 Summary")]
public class SummaryController : ControllerBase
{
    private readonly IWidgetSummaryService _widgetSummaryService;
    private readonly ILogger<SummaryController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SummaryController"/> class.
    /// </summary>
    /// <param name="widgetSummaryService">The widget summary service</param>
    /// <param name="logger">The logger</param>
    public SummaryController(
        IWidgetSummaryService widgetSummaryService,
        ILogger<SummaryController> logger
    )
    {
        _widgetSummaryService = widgetSummaryService;
        _logger = logger;
    }

    /// <summary>
    /// Get widget-friendly summary data including current glucose, IOB, COB, trackers, and alarm state.
    /// </summary>
    /// <param name="hours">Number of hours of glucose history to include (default 0 for current reading only)</param>
    /// <param name="includePredictions">Whether to include predicted glucose values (default false)</param>
    /// <returns>Widget summary response with aggregated diabetes management data</returns>
    [HttpGet]
    [Authorize]
    [ProducesResponseType(typeof(V4SummaryResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<V4SummaryResponse>> GetSummary(
        [FromQuery] int hours = 0,
        [FromQuery] bool includePredictions = false
    )
    {
        var userId = HttpContext.GetSubjectIdString();

        if (string.IsNullOrEmpty(userId))
        {
            return Unauthorized();
        }

        _logger.LogDebug(
            "V4 Summary requested by user {UserId} with hours={Hours}, includePredictions={IncludePredictions}",
            userId,
            hours,
            includePredictions
        );

        try
        {
            var summary = await _widgetSummaryService.GetSummaryAsync(
                userId,
                hours,
                includePredictions,
                HttpContext.RequestAborted
            );

            return Ok(summary);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating V4 summary for user {UserId}", userId);
            throw;
        }
    }
}
