using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Attributes;
using Nocturne.API.Hubs;
using Nocturne.Core.Models.Authorization;

namespace Nocturne.API.Controllers.V4.Identity;

/// <summary>
/// Answers whether the caller's credential may receive the tenant's realtime broadcasts, for the
/// socket.io bridge, which admits browser and legacy sockets without a SignalR connection of their
/// own.
/// </summary>
/// <remarks>
/// Gated like <c>GET /api/v1/entries</c>: the default-deny fallback plus <c>glucose.read</c>, so a
/// caller that could not read glucose over HTTP is refused realtime too,
/// and an anonymous public share is admitted by its read scopes rather than 401'd by
/// <c>[Authorize]</c>. <see cref="RealtimeAdmission.TenantRelay"/> then decides the tenant-wide
/// room with <see cref="HubAuthorization.CanJoinTenantRelay"/>, the rule <see cref="DataHub"/>
/// applies to its own groups.
/// </remarks>
[ApiController]
[Tags("Identity")]
[Route("api/v4/me/realtime-admission")]
[Produces("application/json")]
public class RealtimeAdmissionController : ControllerBase
{
    /// <summary>
    /// Get the caller's realtime admission for the current tenant.
    /// </summary>
    [HttpGet]
    [RequireScope(Scope.GlucoseRead)]
    [ProducesResponseType(typeof(RealtimeAdmission), StatusCodes.Status200OK)]
    public ActionResult<RealtimeAdmission> GetRealtimeAdmission()
    {
        var authorization = HubAuthorizationState.FromRequest(HttpContext);
        return Ok(new RealtimeAdmission(
            authorization?.CanJoinTenantRelay ?? false,
            authorization?.OwnSubjectId));
    }
}

/// <summary>A caller's realtime admission.</summary>
/// <param name="TenantRelay">
/// Whether the caller may join the room carrying the whole tenant's live payloads. False for a
/// guest link and an anonymous share, which hold single categories.
/// </param>
/// <param name="SubjectId">
/// The subject whose per-subject notifications the caller may receive, carried into the bridge's
/// handshake ticket so a browser socket joins only that subject's room. Null for a guest link and a
/// share, which own no subject.
/// </param>
public record RealtimeAdmission(bool TenantRelay, Guid? SubjectId);
