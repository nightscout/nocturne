using Nocturne.API.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Attributes;
using OpenApi.Remote.Attributes;
using Nocturne.API.Extensions;
using Nocturne.Core.Contracts.Identity;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;

namespace Nocturne.API.Controllers.V4.Identity;

/// <summary>
/// Requests to join the tenant, and their approval. A caller can ask to join and read its own
/// request. Members with the <c>members.manage</c> permission can list, approve and deny requests,
/// and read or change the request settings.
/// </summary>
[ApiController]
[System.ComponentModel.DisplayName("Membership Requests")]
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
    [DenyDemoSubject]
    [RemoteCommand(Invalidates = ["GetMyRequest"])]
    [ProducesResponseType(typeof(CreateMembershipRequestResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
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

        if (result.Outcome == CreateMembershipRequestOutcome.AlreadyMember)
            return Problem(detail: result.Error, statusCode: StatusCodes.Status409Conflict, title: "Conflict");

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
    [RequireScope(Scope.MembersManage)]
    [RemoteQuery]
    [ProducesResponseType(typeof(List<MembershipRequestDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetPendingRequests(CancellationToken ct)
    {
        var tenantId = tenantAccessor.TenantId;
        var result = await membershipRequestService.GetPendingRequestsAsync(tenantId, ct);

        return Ok(result);
    }

    /// <summary>
    /// Approve a membership request and add the user as a tenant member.
    /// </summary>
    [HttpPost("{id:guid}/approve")]
    [Authorize]
    [RequireScope(Scope.MembersManage)]
    [RemoteCommand(Invalidates = ["GetPendingRequests", "MemberInvite_GetMembers"])]
    [ProducesResponseType(typeof(DecideMembershipRequestResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> ApproveRequest(
        Guid id,
        [FromBody] ApproveMembershipRequestRequest request,
        [FromServices] TimeProvider time,
        CancellationToken ct)
    {
        if (await HttpContext.IsCallerInactiveAsync(ct))
            return Challenge();

        var tenantId = tenantAccessor.TenantId;
        var subjectId = HttpContext.GetSubjectId();
        if (subjectId == null)
            return Unauthorized();

        if (HttpContext.CallerGrantCeiling() is not { } ceiling)
            return this.CallerAccessFromTeamProblem();

        var accessEndsAt = HttpContext.InheritAccessEnd(AccessEndRequest.Instant(request.AccessEndsAt));
        if (AccessEndRequest.Refusal(HttpContext, accessEndsAt, time.GetUtcNow().UtcDateTime) is (var status, var detail))
            return Problem(detail: detail, statusCode: status);

        var result = await membershipRequestService.ApproveRequestAsync(
            id, tenantId, request.RoleIds, subjectId.Value, ceiling.Scopes,
            accessEndsAt, ceiling.HistoryClamped, ct);

        if (result.Code is { } code)
            return this.ProblemWithCode(code, result.Error, StatusCodes.Status403Forbidden);

        if (!result.Success)
            return BadRequest(result);

        return Ok(result);
    }

    /// <summary>
    /// Deny a membership request.
    /// </summary>
    [HttpPost("{id:guid}/deny")]
    [Authorize]
    [RequireScope(Scope.MembersManage)]
    [RemoteCommand(Invalidates = ["GetPendingRequests"])]
    [ProducesResponseType(typeof(DecideMembershipRequestResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> DenyRequest(Guid id, CancellationToken ct)
    {
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

    /// <summary>
    /// Get whether this tenant currently lets people request to become a member.
    /// </summary>
    [HttpGet("settings")]
    [Authorize]
    [RequireScope(Scope.MembersManage)]
    [RemoteQuery]
    [ProducesResponseType(typeof(MembershipRequestSettingsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetMembershipRequestSettings(CancellationToken ct)
    {
        var allow = await membershipRequestService.GetAllowRequestsAsync(tenantAccessor.TenantId, ct);
        return Ok(new MembershipRequestSettingsDto(allow));
    }

    /// <summary>
    /// Enable or disable whether people can request to become a member. Independent of public access.
    /// </summary>
    [HttpPut("settings")]
    [Authorize]
    [RequireScope(Scope.MembersManage)]
    [RemoteCommand(Invalidates = ["GetMembershipRequestSettings"])]
    [ProducesResponseType(typeof(MembershipRequestSettingsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> SetMembershipRequestSettings(
        [FromBody] UpdateMembershipRequestSettingsRequest request,
        CancellationToken ct)
    {
        var allow = await membershipRequestService.SetAllowRequestsAsync(
            tenantAccessor.TenantId, request.AllowRequests, ct);
        return Ok(new MembershipRequestSettingsDto(allow));
    }

}

public record CreateMembershipRequestRequest(string? Message);
/// <param name="RoleIds">The roles the new membership holds.</param>
/// <param name="AccessEndsAt">When the new membership ends; none when omitted, unless the approver's own access ends.</param>
public record ApproveMembershipRequestRequest(List<Guid> RoleIds, DateTimeOffset? AccessEndsAt = null);
public record MembershipRequestSettingsDto(bool AllowRequests);
public record UpdateMembershipRequestSettingsRequest(bool AllowRequests);
