using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using OpenApi.Remote.Attributes;
using Nocturne.API.Extensions;
using Nocturne.API.Services.Platform.Releases;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;

namespace Nocturne.API.Controllers.V4.Platform;

/// <summary>
/// The running release, the newest published one and its notes, and when Watchtower will install
/// it. Behind the tenant settings permission: the owners who get the release notices.
/// </summary>
[ApiController]
[Tags("Platform")]
[Authorize]
[Route("api/v4/releases")]
public class ReleasesController(
    ReleaseStatusService releaseStatus,
    IOptions<ReleaseCheckOptions> options) : ControllerBase
{
    [HttpGet("status")]
    [RemoteQuery]
    [ProducesResponseType(typeof(ReleaseStatusDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ReleaseStatusDto>> GetReleaseStatus(CancellationToken ct)
    {
        if (!HttpContext.HasScope(Scope.TenantSettings))
            return Forbid();

        if (!options.Value.Enabled)
            return NotFound();

        return Ok(await releaseStatus.GetStatusAsync(ct));
    }
}
