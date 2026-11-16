using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Attributes;
using Nocturne.API.Controllers.V4.Base;
using Nocturne.Core.Contracts.Analytics;
using Nocturne.Core.Models.Analytics;
using Nocturne.Core.Models.Authorization;
using OpenApi.Remote.Attributes;

namespace Nocturne.API.Controllers.V4.Analytics;

/// <summary>
/// Readings the sensor itself flagged, grouped into episodes, for Data Quality.
/// </summary>
/// <seealso cref="IErroneousReadingsService"/>
[ApiController]
[Tags("Analytics")]
[Route("api/v4/erroneous-readings")]
[Produces("application/json")]
[RequireScope(Scope.ReportsRead)]
public class ErroneousReadingsController(IErroneousReadingsService erroneousReadings) : ControllerBase
{
    /// <summary>
    /// List the sensor-error episodes that start in a UTC window.
    /// </summary>
    /// <param name="startDate">Inclusive UTC start of the window.</param>
    /// <param name="endDate">Exclusive UTC end of the window.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [HttpGet]
    [RemoteQuery]
    [ResponseCache(Duration = 60, Location = ResponseCacheLocation.Client)]
    public async Task<ActionResult<ErroneousReadingsReport>> GetErroneousReadings(
        [FromQuery] DateTime startDate,
        [FromQuery] DateTime endDate,
        CancellationToken cancellationToken = default)
    {
        if (startDate == default || endDate == default)
            return Problem(detail: "startDate and endDate must be later than 0001-01-01.", statusCode: 400, title: "Bad Request");

        if (endDate <= startDate)
            return Problem(detail: "endDate must be after startDate.", statusCode: 400, title: "Bad Request");

        if (this.RejectDateSpan(startDate, endDate) is { } overlong)
            return overlong;

        return Ok(await erroneousReadings.GetAsync(startDate, endDate, cancellationToken));
    }
}
