using Microsoft.AspNetCore.Mvc;
using OpenApi.Remote.Attributes;
using Nocturne.API.Extensions;
using Nocturne.API.Services.Auth;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Configuration;

namespace Nocturne.API.Controllers.V4.Identity;

/// <summary>
/// Serves the appearance a public share or a guest link renders the tenant's data with: units,
/// time and date format, colour theme and chart style, taken from the owner's own settings.
/// </summary>
/// <remarks>
/// Gated by the default-deny fallback policy rather than <c>[Authorize]</c>, for the same reason
/// as <see cref="MyPermissionsController"/>: a public share is deliberately unauthenticated, and
/// <c>[Authorize]</c> would 401 every one of them. Any other caller gets the 404 an unrouted path
/// gets, so no member or bearer token can read another member's settings through it.
/// <para>
/// The response carries glucose units, time format, region format, colour theme, prediction
/// display and chart style, and no subject id, name, username, email or display language. See
/// <see cref="UserDisplayPreferences.ToPresentationOnly"/> for what is withheld and why.
/// </para>
/// </remarks>
[ApiController]
[Tags("Identity")]
[Route("api/v4/tenant/appearance")]
[Produces("application/json")]
public class TenantAppearanceController : ControllerBase
{
    private readonly ITenantAppearanceReader _appearance;
    private readonly ITenantAccessor _tenantAccessor;

    public TenantAppearanceController(ITenantAppearanceReader appearance, ITenantAccessor tenantAccessor)
    {
        _appearance = appearance;
        _tenantAccessor = tenantAccessor;
    }

    /// <summary>Get the presentation preferences a share or guest viewer sees the tenant's data with.</summary>
    [HttpGet]
    [RemoteQuery]
    [ProducesResponseType(typeof(UserDisplayPreferences), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<UserDisplayPreferences>> GetTenantAppearance(CancellationToken ct)
    {
        if (!HttpContext.IsShareAccess()
            && HttpContext.GetAuthContext() is not { IsAuthenticated: true, AuthType: AuthType.Guest })
            return NotFound();

        return Ok(await _appearance.GetTenantAppearanceAsync(_tenantAccessor.TenantId, ct));
    }
}
