using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Attributes;
using Nocturne.API.Services.Telemetry;
using Nocturne.Core.Contracts.Telemetry;

namespace Nocturne.API.Controllers.V4.PlatformAdmin;

/// <summary>
/// How many times each page of the web app was opened on each UTC day, summed over every tenant.
/// Empty unless the operator turned page counts on. Counts reach the store every few minutes, so
/// today's are a little behind.
/// </summary>
[ApiController]
[Tags("PlatformAdmin")]
[Route("api/v4/admin/telemetry")]
[Produces("application/json")]
[Authorize(Roles = "platform_admin")]
public class PageCountsAdminController(IPageViewCountStore store, TimeProvider time) : ControllerBase
{
    internal const int DefaultDays = 30;

    /// <param name="from">First UTC day, inclusive. Defaults to <see cref="DefaultDays"/> days before <paramref name="to"/>, counting both.</param>
    /// <param name="to">Last UTC day, inclusive. Defaults to today.</param>
    /// <param name="ct">Cancels the operation.</param>
    [HttpGet("page-counts")]
    [ErrorEnvelope]
    [ProducesResponseType(typeof(List<PageViewCount>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetPageCounts(
        [FromQuery] DateOnly? from = null,
        [FromQuery] DateOnly? to = null,
        CancellationToken ct = default)
    {
        var last = to ?? DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);
        var first = from ?? last.AddDays(-(DefaultDays - 1));
        if (first > last)
            return Problem(detail: "from must not be after to", statusCode: 400, title: "Bad Request");

        return Ok(await store.ReadAsync(first, last, ct));
    }

    /// <summary>
    /// The route id of every page the app counts, in ordinal order. Counts hold only pages that were
    /// opened, so the ids here that a count lacks are the pages nobody opened.
    /// </summary>
    [HttpGet("page-routes")]
    [ErrorEnvelope]
    [ProducesResponseType(typeof(List<string>), StatusCodes.Status200OK)]
    public IActionResult GetPageRoutes() => Ok(PageRouteIds.All.Order(StringComparer.Ordinal).ToList());
}
