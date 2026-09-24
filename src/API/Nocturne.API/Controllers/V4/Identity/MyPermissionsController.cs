using Microsoft.AspNetCore.Mvc;
using OpenApi.Remote.Attributes;
using Nocturne.API.Extensions;
using Nocturne.Core.Contracts.Multitenancy;

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
    private readonly ICategoryReadContext _categoryReadContext;

    /// <summary>
    /// Initializes a new instance of <see cref="MyPermissionsController"/>.
    /// </summary>
    /// <param name="categoryReadContext">Says whether the request is history-clamped.</param>
    public MyPermissionsController(ICategoryReadContext categoryReadContext)
    {
        _categoryReadContext = categoryReadContext;
    }

    /// <summary>
    /// Get the caller's effective granted scopes and history window for the current tenant.
    /// </summary>
    /// <returns>The caller's granted scopes, and whether it may read only the last 24 hours.</returns>
    [HttpGet]
    [RemoteQuery]
    [ProducesResponseType(typeof(MyPermissionsResponse), StatusCodes.Status200OK)]
    public ActionResult<MyPermissionsResponse> GetMyPermissions()
    {
        return Ok(new MyPermissionsResponse
        {
            Scopes = HttpContext.GetGrantedScopes().ToList(),
            LimitTo24Hours = _categoryReadContext.IsHistoryClamped,
        });
    }
}

/// <summary>
/// What the caller may read on the current tenant.
/// </summary>
public class MyPermissionsResponse
{
    /// <summary>The granted scope strings.</summary>
    public List<string> Scopes { get; set; } = [];

    /// <summary>
    /// True when the caller may read only the last 24 hours of time-series data, whether a share
    /// without full history or a clamped member or credential.
    /// </summary>
    public bool LimitTo24Hours { get; set; }
}
