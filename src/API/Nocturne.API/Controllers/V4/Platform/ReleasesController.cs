using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Attributes;
using Microsoft.Extensions.Options;
using OpenApi.Remote.Attributes;
using Nocturne.API.Extensions;
using Nocturne.API.Services.Platform.Releases;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;

namespace Nocturne.API.Controllers.V4.Platform;

/// <summary>
/// The running release and the newest published release, with its notes, and when an automatic
/// update is due. Callers need the tenant settings permission.
/// </summary>
/// <remarks>
/// The tenant settings permission is held by the owners who get the release notices.
/// </remarks>
[ApiController]
[Tags("Platform")]
[Authorize]
[Route("api/v4/releases")]
public class ReleasesController(
    ReleaseStatusService releaseStatus,
    IOptions<ReleaseCheckOptions> options) : ControllerBase
{
    [HttpGet("status")]
    [RequireScope(Scope.TenantSettings)]
    [RemoteQuery]
    [ProducesResponseType(typeof(ReleaseStatusDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ReleaseStatusDto>> GetReleaseStatus(CancellationToken ct)
    {
        if (!options.Value.Enabled)
            return NotFound();

        return Ok(await releaseStatus.GetStatusAsync(ct));
    }
}
