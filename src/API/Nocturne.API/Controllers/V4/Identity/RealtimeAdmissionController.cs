using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Attributes;
using Nocturne.API.Extensions;
using Nocturne.API.Hubs;
using Nocturne.API.Services.Realtime;
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
/// room with <see cref="HubAuthorization.CanJoinTenantRelay"/> and
/// <see cref="RealtimeAdmission.Categories"/> the category rooms with
/// <see cref="HubAuthorization.MayReceiveCategory"/>, the rules <see cref="DataHub"/> applies to its
/// own groups.
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
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public ActionResult<RealtimeAdmission> GetRealtimeAdmission()
    {
        if (HttpContext.GetTenantContext() is not { } tenant)
        {
            return StatusCode(StatusCodes.Status403Forbidden);
        }

        var authorization = HubAuthorizationState.FromRequest(HttpContext);
        return Ok(new RealtimeAdmission(
            tenant.TenantId,
            authorization?.CanJoinTenantRelay ?? false,
            authorization?.OwnSubjectId,
            authorization is null
                ? []
                : [.. RealtimeCategories.All.Where(authorization.MayReceiveCategory)],
            authorization?.HistoryClamped ?? false,
            authorization is { CanJoinTenantRelay: true } && authorization.Satisfies(Scope.AlertsAcknowledge)));
    }
}

/// <summary>A caller's realtime admission.</summary>
/// <param name="TenantId">
/// The tenant the caller was admitted to, signed into the bridge's handshake ticket so the bridge
/// refuses the ticket once the slug names another tenant.
/// </param>
/// <param name="TenantRelay">
/// Whether the caller may join the room carrying the whole tenant's live payloads. False for a
/// guest link and an anonymous share, which hold single categories.
/// </param>
/// <param name="SubjectId">
/// The subject whose per-subject notifications the caller may receive, carried into the bridge's
/// handshake ticket so a browser socket joins only that subject's room. Null for a guest link and a
/// share, which own no subject.
/// </param>
/// <param name="Categories">
/// The <see cref="RealtimeCategories"/> whose storage broadcasts the caller may receive through the
/// bridge, as <c>DataHub.Subscribe</c> would join them. A guest link that may not join the tenant
/// room still receives these. Empty for an anonymous share, which proves no hub credential, and for a
/// history-clamped caller.
/// </param>
/// <param name="HistoryClamped">Whether the caller may read only the last 24 hours.</param>
/// <param name="MayAck">
/// Whether the caller may silence the tenant's alarms through the bridge's <c>/alarm</c> namespace.
/// The bridge forwards an ack on its own AlarmHub connection, whose gate it always meets, so the
/// bridge drops an ack from a subscriber without this.
/// </param>
public record RealtimeAdmission(
    Guid TenantId,
    bool TenantRelay,
    Guid? SubjectId,
    IReadOnlyList<string> Categories,
    bool HistoryClamped,
    bool MayAck);
