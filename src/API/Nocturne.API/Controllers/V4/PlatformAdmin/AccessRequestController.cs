using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpenApi.Remote.Attributes;
using Nocturne.API.Authorization;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Identity;
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
/// <seealso cref="IAccessRequestService"/>
[ApiController]
[Tags("PlatformAdmin")]
[Route("api/v4/admin/access-requests")]
[Authorize(Roles = "platform_admin")]
[AllowDuringSetup]
public class AccessRequestController(
    ITenantMembershipRepository members,
    ISubjectRepository subjects,
    ISubjectService subjectService,
    IAccessRequestService accessRequests,
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

    /// <inheritdoc cref="IAccessRequestService.ApproveAsync"/>
    [HttpPost("{subjectId:guid}/approve")]
    [RemoteCommand(Invalidates = ["GetPendingRequests"])]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult> Approve(
        Guid subjectId,
        [FromBody] ApproveAccessRequestRequest request,
        CancellationToken ct)
    {
        var result = await accessRequests.ApproveAsync(
            subjectId,
            tenantAccessor.Context?.TenantId,
            request.RoleIds,
            request.DirectPermissions,
            request.LimitTo24Hours,
            HttpContext.GetGrantedScopes(),
            ct);

        switch (result.Outcome)
        {
            case ApproveAccessRequestOutcome.Approved:
                return Ok();
            case ApproveAccessRequestOutcome.NothingGranted:
                return Problem(detail: "At least one role or direct permission is required", statusCode: 400, title: "Bad Request");
            case ApproveAccessRequestOutcome.RoleGrantRefused:
                var foreignRole = result.RoleGrant!.ErrorCode == RoleGrantValidation.ForeignRole;
                return Problem(
                    detail: result.RoleGrant.ErrorDescription,
                    statusCode: foreignRole ? 400 : 403,
                    title: foreignRole ? "Bad Request" : "Forbidden");
            case ApproveAccessRequestOutcome.AlreadyMember:
                return Problem(detail: "The subject is already a member.", statusCode: 409, title: "Conflict");
            case ApproveAccessRequestOutcome.NotFound:
                return NotFound();
            default:
                throw new ArgumentOutOfRangeException(nameof(result));
        }
    }

    /// <inheritdoc cref="ISubjectService.DeleteSubjectAsync"/>
    [HttpPost("{subjectId:guid}/deny")]
    [RemoteCommand(Invalidates = ["GetPendingRequests"])]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult> Deny(Guid subjectId, CancellationToken ct)
    {
        var subject = await subjects.GetAsync(subjectId, ct);

        if (subject is not { ApprovalStatus: "Pending" })
            return NotFound();

        if (await subjectService.DeleteSubjectAsync(subjectId) is SubjectChange.LastOwnerOf lastOwnerOf)
        {
            return Problem(
                detail: $"The subject is the last owner of: {string.Join(", ", lastOwnerOf.OwnedTenants.Select(t => t.Slug))}.",
                statusCode: StatusCodes.Status409Conflict,
                title: "Conflict");
        }

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
