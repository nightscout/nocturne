using Microsoft.AspNetCore.Mvc;
using OpenApi.Remote.Attributes;
using Nocturne.API.Extensions;
using Nocturne.API.Services.Auth;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Configuration;

namespace Nocturne.API.Controllers.V4.Identity;

/// <summary>
/// Serves the appearance an anonymous share viewer renders the tenant's data with: units, time
/// and date format, colour theme and chart style, taken from the link owner's own settings.
/// </summary>
/// <remarks>
/// Gated by the default-deny fallback policy rather than <c>[Authorize]</c>, for the same reason
/// as <see cref="MyPermissionsController"/>: a public share is deliberately unauthenticated, and
/// <c>[Authorize]</c> would 401 every one of them. The share host is additionally the only place
/// this answers — a request that did not arrive over a share token gets the 404 an unrouted path
/// gets, so no member or bearer token can read another member's settings through it.
/// <para>
/// What a viewer can read is presentation only, and is nobody's identity: glucose units, time
/// format, region format, colour theme, prediction display and chart style. The projection is an
/// allow-list (<see cref="UserDisplayPreferences.ToPresentationOnly"/>), so a preference added
/// later stays private until someone decides otherwise, and the response carries no subject id,
/// name, username, email or display language.
/// </para>
/// </remarks>
[ApiController]
[Tags("Identity")]
[Route("api/v4/share/appearance")]
[Produces("application/json")]
public class ShareAppearanceController : ControllerBase
{
    private readonly IShareLinkService _shareLinkService;
    private readonly ITenantAccessor _tenantAccessor;

    public ShareAppearanceController(IShareLinkService shareLinkService, ITenantAccessor tenantAccessor)
    {
        _shareLinkService = shareLinkService;
        _tenantAccessor = tenantAccessor;
    }

    /// <summary>Get the presentation preferences the current share link is displayed with.</summary>
    [HttpGet]
    [RemoteQuery]
    [ProducesResponseType(typeof(UserDisplayPreferences), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<UserDisplayPreferences>> GetShareAppearance(CancellationToken ct)
    {
        if (!HttpContext.IsShareAccess())
            return NotFound();

        return Ok(await _shareLinkService.GetSharedAppearanceAsync(_tenantAccessor.TenantId, ct));
    }
}
