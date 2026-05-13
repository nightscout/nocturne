using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpenApi.Remote.Attributes;
using Nocturne.API.Extensions;
using Nocturne.Core.Contracts.Identity;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;

namespace Nocturne.API.Controllers.V4.Identity;

/// <summary>
/// Endpoints for membership request lifecycle: users request to join a tenant,
/// and members with <c>members.manage</c> permission approve or deny them.
/// </summary>
[ApiController]
[Tags("Identity")]
[Route("api/v4/membership-requests")]
[Produces("application/json")]
public class MembershipRequestController(
    IMembershipRequestService membershipRequestService,
    ITenantAccessor tenantAccessor) : ControllerBase
{
    /// <summary>
    /// Submit a request to join the current tenant.
    /// </summary>
    [HttpPost]
    [Authorize]
    [RemoteCommand]
    [ProducesResponseType(typeof(CreateMembershipRequestResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CreateRequest(
        [FromBody] CreateMembershipRequestRequest request,
        CancellationToken ct)
    {
        var tenantId = tenantAccessor.TenantId;
        var subjectId = HttpContext.GetSubjectId();
        if (subjectId == null)
            return Unauthorized();

        var result = await membershipRequestService.CreateRequestAsync(
            tenantId, subjectId.Value, request.Message, ct);

        if (!result.Success)
            return BadRequest(result);

        return Ok(result);
    }

    /// <summary>
    /// Get the current user's membership request for this tenant, if any.
    /// </summary>
    [HttpGet("mine")]
    [Authorize]
    [RemoteQuery]
    [ProducesResponseType(typeof(MembershipRequestDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetMyRequest(CancellationToken ct)
    {
        var tenantId = tenantAccessor.TenantId;
        var subjectId = HttpContext.GetSubjectId();
        if (subjectId == null)
            return Unauthorized();

        var result = await membershipRequestService.GetMyRequestAsync(
            tenantId, subjectId.Value, ct);

        return Ok(result);
    }

    /// <summary>
    /// List all pending membership requests for the current tenant.
    /// </summary>
    [HttpGet]
    [Authorize]
    [RemoteQuery]
    [ProducesResponseType(typeof(List<MembershipRequestDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetPendingRequests(CancellationToken ct)
    {
        if (!HasPermission(TenantPermissions.MembersManage))
            return Forbid();

        var tenantId = tenantAccessor.TenantId;
        var result = await membershipRequestService.GetPendingRequestsAsync(tenantId, ct);

        return Ok(result);
    }

    /// <summary>
    /// Approve a membership request and add the user as a tenant member.
    /// </summary>
    [HttpPost("{id:guid}/approve")]
    [Authorize]
    [RemoteCommand]
    [ProducesResponseType(typeof(DecideMembershipRequestResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> ApproveRequest(
        Guid id,
        [FromBody] ApproveMembershipRequestRequest request,
        CancellationToken ct)
    {
        if (!HasPermission(TenantPermissions.MembersManage))
            return Forbid();

        var tenantId = tenantAccessor.TenantId;
        var subjectId = HttpContext.GetSubjectId();
        if (subjectId == null)
            return Unauthorized();

        var result = await membershipRequestService.ApproveRequestAsync(
            id, tenantId, request.RoleIds, subjectId.Value, ct);

        if (!result.Success)
            return BadRequest(result);

        return Ok(result);
    }

    /// <summary>
    /// Deny a membership request.
    /// </summary>
    [HttpPost("{id:guid}/deny")]
    [Authorize]
    [RemoteCommand]
    [ProducesResponseType(typeof(DecideMembershipRequestResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> DenyRequest(Guid id, CancellationToken ct)
    {
        if (!HasPermission(TenantPermissions.MembersManage))
            return Forbid();

        var tenantId = tenantAccessor.TenantId;
        var subjectId = HttpContext.GetSubjectId();
        if (subjectId == null)
            return Unauthorized();

        var result = await membershipRequestService.DenyRequestAsync(
            id, tenantId, subjectId.Value, ct);

        if (!result.Success)
            return BadRequest(result);

        return Ok(result);
    }

    private bool HasPermission(string permission)
    {
        var grantedScopes = HttpContext.Items["GrantedScopes"] as IReadOnlySet<string>;
        if (grantedScopes == null) return false;
        return TenantPermissions.HasPermission(grantedScopes, permission);
    }
}

public record CreateMembershipRequestRequest(string? Message);
public record ApproveMembershipRequestRequest(List<Guid> RoleIds);
