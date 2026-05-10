using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpenApi.Remote.Attributes;
using Nocturne.API.Extensions;

namespace Nocturne.API.Controllers.V4.Identity;

/// <summary>
/// Returns the current user's effective permissions for the resolved tenant.
/// Permissions are populated by MemberScopeMiddleware from the user's roles
/// and direct permissions, intersected with their auth token scopes.
/// </summary>
[ApiController]
[Tags("Identity")]
[Route("api/v4/me/permissions")]
[Produces("application/json")]
[Authorize]
public class MyPermissionsController : ControllerBase
{
    /// <summary>
    /// Get the current user's effective granted scopes for the current tenant.
    /// </summary>
    /// <returns>The list of granted scope strings for the authenticated user on the current tenant.</returns>
    [HttpGet]
    [RemoteQuery]
    [ProducesResponseType(typeof(List<string>), StatusCodes.Status200OK)]
    public ActionResult<List<string>> GetMyPermissions()
    {
        var scopes = HttpContext.GetGrantedScopes();
        return Ok(scopes.ToList());
    }
}
