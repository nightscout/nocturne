using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Attributes;
using Nocturne.API.Authorization;
using Nocturne.API.Extensions;
using Nocturne.API.Services.Identity;

namespace Nocturne.API.Controllers.V4.Identity;

/// <summary>
/// Which tenants the caller hears live changes for on the cross-tenant overview.
/// </summary>
/// <remarks>
/// The socket.io bridge's <c>/overview</c> namespace joins a browser to one room per tenant named
/// here, through a handshake ticket the web app signs from this answer. It is the answer
/// <see cref="Hubs.OverviewHub.Authorize"/> gives a session, under the same
/// <see cref="OverviewCredential"/> rule.
/// </remarks>
[ApiController]
[Tags("Identity")]
[Route("api/v4/me/overview-admission")]
[Produces("application/json")]
[Authorize]
public class OverviewAdmissionController : ControllerBase
{
    private readonly ITenantOverviewService _overviewService;

    public OverviewAdmissionController(ITenantOverviewService overviewService)
    {
        _overviewService = overviewService;
    }

    /// <summary>
    /// Get the caller's subject and the tenants it may read glucose for.
    /// </summary>
    [HttpGet]
    [ErrorEnvelope]
    [ProducesResponseType(typeof(OverviewAdmission), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<OverviewAdmission>> GetOverviewAdmission(CancellationToken ct)
    {
        var caller = HttpContext.GetAuthContext();
        if (!OverviewCredential.Admits(caller) || caller?.SubjectId is not { } subjectId)
        {
            return StatusCode(StatusCodes.Status403Forbidden);
        }

        var tenants = await _overviewService.GetGlucoseReadTenantsAsync(
            subjectId, HttpContext.GetGrantedScopes(), caller.AuthType, ct);
        return Ok(new OverviewAdmission(subjectId, [.. tenants.Select(t => t.Tenant.Id)]));
    }
}

/// <summary>A caller's overview admission.</summary>
/// <param name="SubjectId">The subject the overview is read for.</param>
/// <param name="TenantIds">
/// The tenants whose live changes the caller may hear about, as
/// <see cref="ITenantOverviewService.GetGlucoseReadTenantsAsync"/> resolves them.
/// </param>
public record OverviewAdmission(Guid SubjectId, IReadOnlyList<Guid> TenantIds);
