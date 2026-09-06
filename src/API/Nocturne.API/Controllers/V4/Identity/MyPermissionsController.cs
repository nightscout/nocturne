using Microsoft.AspNetCore.Mvc;
using OpenApi.Remote.Attributes;
using Nocturne.API.Extensions;

namespace Nocturne.API.Controllers.V4.Identity;

/// <summary>
/// Returns the caller's effective permissions for the resolved tenant.
/// Permissions are populated by MemberScopeMiddleware from the user's roles
/// and direct permissions, intersected with their auth token scopes.
/// </summary>
/// <remarks>
/// Gated by the default-deny fallback policy — a non-empty permission trie — rather than by
/// <c>[Authorize]</c>. A public share link ({token}.share.{baseDomain}) is deliberately
/// unauthenticated, yet AuthenticationMiddleware resolves its Public subject to
/// <see cref="Nocturne.Core.Models.Authorization.Scope.PublicShareScopes"/> and publishes both
/// the scopes and a trie on the request; the share view needs its granted categories to offer
/// only the surfaces it can load, and <c>[Authorize]</c> 401s it. The answer is the caller's own
/// resolved grant, so it discloses nothing a request of theirs would not. A caller with no grant
/// has no trie and is refused by the fallback.
/// </remarks>
[ApiController]
[Tags("Identity")]
[Route("api/v4/me/permissions")]
[Produces("application/json")]
public class MyPermissionsController : ControllerBase
{
    /// <summary>
    /// Get the caller's effective granted scopes for the current tenant.
    /// </summary>
    /// <returns>The list of granted scope strings for the caller on the current tenant.</returns>
    [HttpGet]
    [RemoteQuery]
    [ProducesResponseType(typeof(List<string>), StatusCodes.Status200OK)]
    public ActionResult<List<string>> GetMyPermissions()
    {
        var scopes = HttpContext.GetGrantedScopes();
        return Ok(scopes.ToList());
    }
}
