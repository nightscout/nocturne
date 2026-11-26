using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Nocturne.API.Attributes;
using Nocturne.API.Extensions;
using Nocturne.API.Services.Telemetry;
using OpenApi.Remote.Attributes;

namespace Nocturne.API.Controllers.V4.Platform;

/// <summary>
/// The web app's page-view beacon. A view is counted against its route id and the UTC day, summed
/// over every tenant and visitor, and only while the operator has turned page counts on
/// (<c>Telemetry:PageCounts:Enabled</c>). Nothing about the caller is kept.
/// </summary>
[ApiController]
[Tags("Platform")]
[Route("api/v4/telemetry")]
[Produces("application/json")]
[AllowAnonymous]
public class PageViewController(PageViewCounter counter) : ControllerBase
{
    /// <summary>
    /// Counts one view of a page. Answers 204 whether or not page counts are on, and 400 for a
    /// route id that is not a page of the app.
    /// </summary>
    [HttpPost("page-view")]
    [RemoteCommand]
    [ErrorEnvelope]
    [EnableRateLimiting(ServiceRegistrationExtensions.PageViewRateLimitPolicy)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public IActionResult RecordPageView([FromBody] PageViewRequest request)
    {
        if (!counter.Enabled)
            return NoContent();

        if (!PageRouteIds.All.Contains(request.RouteId))
            return Problem(detail: "Not a route id of the app", statusCode: 400, title: "Bad Request");

        counter.Record(request.RouteId);
        return NoContent();
    }
}

/// <param name="RouteId">The SvelteKit route id of the page, e.g. <c>/(authenticated)/reports/agp</c>.</param>
public sealed record PageViewRequest(string RouteId);
