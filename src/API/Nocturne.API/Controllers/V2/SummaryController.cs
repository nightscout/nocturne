using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Attributes;
using Nocturne.API.Authorization;
using Nocturne.Core.Contracts.Analytics;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models;
using OpenApi.Remote.Attributes;

namespace Nocturne.API.Controllers.V2;

/// <summary>
/// V2 Summary controller providing aggregated data endpoints.
/// Implements the legacy /api/v2/summary endpoints with 1:1 backwards compatibility.
/// </summary>
/// <remarks>
/// The action requires only <c>glucose.read</c> while the response also carries treatments and the
/// active profile. This is the accepted narrowing described on <see cref="PropertiesController"/>,
/// which names the per-category follow-up.
/// </remarks>
/// <seealso cref="ISummaryService"/>
/// <seealso cref="SummaryResponse"/>
[ApiController]
[Tags("V2")]
[Route("api/v2/summary")]
[Produces("application/json")]
[ClientPropertyName("v2Summary")]
[Authorize(Policy = PolicyNames.HasPermissions)]
public class SummaryController : ControllerBase
{
    private readonly ISummaryService _summaryService;
    private readonly ILogger<SummaryController> _logger;

    private const int DefaultHours = 6;

    /// <summary>
    /// Initializes a new instance of <see cref="SummaryController"/>.
    /// </summary>
    /// <param name="summaryService">Service for assembling summary data for a time window.</param>
    /// <param name="logger">Logger instance.</param>
    public SummaryController(ISummaryService summaryService, ILogger<SummaryController> logger)
    {
        _summaryService = summaryService;
        _logger = logger;
    }

    /// <summary>
    /// Get summary data for the specified time window
    /// Returns processed SGVs, treatments, profile, and state information
    /// </summary>
    /// <param name="hours">Number of hours to include in summary (default 6)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Summary data containing SGVs, treatments, profile, and state</returns>
    /// <response code="200">Returns the summary data</response>
    /// <response code="400">If the hours parameter is invalid</response>
    /// <response code="500">If there was an internal server error</response>
    [HttpGet]
    [ProducesResponseType(typeof(SummaryResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    [RequireScope(Scope.GlucoseRead)]
    public async Task<ActionResult<SummaryResponse>> GetSummary(
        [FromQuery] int? hours = null,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            var hoursToUse = hours ?? DefaultHours;

            if (hoursToUse <= 0)
            {
                return BadRequest(new { error = "Hours parameter must be greater than 0" });
            }

            var result = await _summaryService.GetSummaryAsync(hoursToUse, cancellationToken);

            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting summary for {Hours} hours", hours);
            return StatusCode(500, new { error = "Internal server error" });
        }
    }
}
