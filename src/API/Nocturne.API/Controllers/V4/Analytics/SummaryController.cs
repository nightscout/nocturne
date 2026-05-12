using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Extensions;
using Nocturne.Core.Contracts.Analytics;
using Nocturne.Core.Models.Widget;
using Nocturne.API.Services.Glucose;

namespace Nocturne.API.Controllers.V4.Analytics;

/// <summary>
/// V4 Summary controller providing widget-friendly summary data.
/// Designed for mobile widgets, watch faces, and other constrained displays.
/// </summary>
/// <remarks>
/// Returns a compact <see cref="V4SummaryResponse"/> that aggregates current glucose,
/// insulin-on-board (IOB), carbs-on-board (COB), active tracker states, and alarm status
/// into a single low-latency response.
///
/// The optional <c>hours</c> query parameter controls how many hours of glucose history
/// are included in the response; set it to <c>0</c> (the default) to retrieve only the
/// most recent reading. Setting <c>includePredictions</c> to <c>true</c> appends the
/// configured prediction curve (see <see cref="IPredictionService"/>).
///
/// This endpoint requires authentication. Callers that do not supply a valid bearer token
/// receive <c>401 Unauthorized</c>.
/// </remarks>
/// <seealso cref="IWidgetSummaryService"/>
/// <seealso cref="V4SummaryResponse"/>
[ApiController]
[Tags("Analytics")]
[Route("api/v4/summary")]
[Produces("application/json")]
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
