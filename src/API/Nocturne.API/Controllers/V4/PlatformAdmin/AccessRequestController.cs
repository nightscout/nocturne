using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpenApi.Remote.Attributes;
using Nocturne.API.Authorization;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Notifications;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Nocturne.API.Multitenancy;
using Nocturne.API.Extensions;

namespace Nocturne.API.Controllers.V4.PlatformAdmin;

/// <summary>
/// Platform-admin controller for reviewing and actioning user access requests.
/// </summary>
/// <remarks>
/// Access requests are created when an unauthenticated user completes the
/// <c>POST /api/auth/passkey/access-request/complete</c> ceremony. Admins with the
/// <c>platform_admin</c> role can list pending requests, approve them (activating the subject
/// and adding them as a tenant member), or reject them (deactivating the subject record).
/// </remarks>
/// <seealso cref="ISubjectService"/>
/// <seealso cref="ITenantService"/>
[ApiController]
[Tags("PlatformAdmin")]
[Route("api/v4/admin/access-requests")]
[Authorize(Roles = "platform_admin")]
[AllowDuringSetup]
public class AccessRequestController(
    ITenantMembershipRepository members,
    ISubjectRepository subjects,
    ISubjectService subjectService,
    ITenantService tenantService,
    ITenantRoleService tenantRoleService,
    ITenantAccessor tenantAccessor,
    IInAppNotificationService notificationService,
    ILogger<AccessRequestController> logger) : ControllerBase
{
    [HttpGet]
    [RemoteQuery]
    [ProducesResponseType(typeof(List<AccessRequestDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<List<AccessRequestDto>>> GetPendingRequests(CancellationToken ct)
    {
        var pending = await subjects.ListPendingAccessRequestsAsync(ct);

        return Ok(pending.Select(s => new AccessRequestDto
        {
            SubjectId = s.Id,
            Name = s.Name,
            Message = s.AccessRequestMessage,
            CreatedAt = s.CreatedAt,
        }).ToList());
    }

    /// <inheritdoc cref="ITenantService.AddMemberAsync"/>
    [HttpPost("{subjectId:guid}/approve")]
    [RemoteCommand(Invalidates = ["GetPendingRequests"])]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> Approve(
        Guid subjectId,
        [FromBody] ApproveAccessRequestRequest request,
        CancellationToken ct)
    {
        var subject = await subjects.GetAsync(subjectId, ct);

        if (subject is not { ApprovalStatus: "Pending" })
            return NotFound();

        if (request.RoleIds.Count == 0 && (request.DirectPermissions == null || request.DirectPermissions.Count == 0))
            return Problem(detail: "At least one role or direct permission is required", statusCode: 400, title: "Bad Request");

        await subjects.UpdateAsync(subjectId, new SubjectEdit(IsActive: true, ApprovalStatus: "Approved"), ct);

        var tenantContext = tenantAccessor.Context;
        if (tenantContext != null)
        {
            var tenantId = tenantContext.TenantId;

            var roleGrant = await tenantRoleService.ValidateRoleGrantAsync(
                tenantId, request.RoleIds, HttpContext.GetGrantedScopes(), ct);
            if (!roleGrant.Ok)
            {
                return Problem(
                    detail: roleGrant.ErrorDescription,
                    statusCode: roleGrant.ErrorCode == RoleGrantValidation.ForeignRole ? 400 : 403,
                    title: roleGrant.ErrorCode == RoleGrantValidation.ForeignRole ? "Bad Request" : "Forbidden");
            }

            await tenantService.AddMemberAsync(
                tenantId, subjectId, request.RoleIds, request.DirectPermissions,
                limitTo24Hours: request.LimitTo24Hours, ct: ct);

            var ownerIds = await members.GetOwnerSubjectIdsAsync(tenantId, ct);

            foreach (var ownerId in ownerIds)
            {
                await notificationService.ArchiveBySourceAsync(
                    ownerId.ToString(),
                    "passkey.anonymous_login_request",
                    subjectId.ToString(),
                    NotificationArchiveReason.Completed,
                    ct);
            }
        }

        logger.LogInformation(
            "Access request approved: subject {SubjectId} ({Name}) assigned {RoleCount} roles",
            subjectId, subject.Name, request.RoleIds.Count);

        return Ok();
    }

    /// <inheritdoc cref="ISubjectService.DeleteSubjectAsync"/>
    [HttpPost("{subjectId:guid}/deny")]
    [RemoteCommand(Invalidates = ["GetPendingRequests"])]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> Deny(Guid subjectId, CancellationToken ct)
    {
        var subject = await subjects.GetAsync(subjectId, ct);

        if (subject is not { ApprovalStatus: "Pending" })
            return NotFound();

        var denyTenantContext = tenantAccessor.Context;
        if (denyTenantContext != null)
        {
            var ownerIds = await members.GetOwnerSubjectIdsAsync(denyTenantContext.TenantId, ct);

            foreach (var ownerId in ownerIds)
            {
                await notificationService.ArchiveBySourceAsync(
                    ownerId.ToString(),
                    "passkey.anonymous_login_request",
                    subjectId.ToString(),
                    NotificationArchiveReason.Dismissed,
                    ct);
            }
        }

        await subjectService.DeleteSubjectAsync(subjectId);

        logger.LogInformation(
            "Access request denied: subject {SubjectId} ({Name}) deleted",
            subjectId, subject.Name);

        return Ok();
    }
}

public class AccessRequestDto
{
    public Guid SubjectId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Message { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class ApproveAccessRequestRequest
{
    public List<Guid> RoleIds { get; set; } = [];
    public List<string>? DirectPermissions { get; set; }

    /// <summary>
    /// Restricts the new member's reads to the last 24 hours of data, as on a share or invite.
    /// </summary>
    public bool LimitTo24Hours { get; set; }
}
