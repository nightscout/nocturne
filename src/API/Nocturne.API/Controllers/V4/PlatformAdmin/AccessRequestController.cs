using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OpenApi.Remote.Attributes;
using Nocturne.API.Authorization;
using Nocturne.Core.Contracts.Notifications;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Nocturne.API.Multitenancy;
using Nocturne.Infrastructure.Data;

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
    NocturneDbContext dbContext,
    ISubjectService subjectService,
    ITenantService tenantService,
    ITenantAccessor tenantAccessor,
    IInAppNotificationService notificationService,
    ILogger<AccessRequestController> logger) : ControllerBase
{
    [HttpGet]
    [RemoteQuery]
    [ProducesResponseType(typeof(List<AccessRequestDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<List<AccessRequestDto>>> GetPendingRequests(CancellationToken ct)
    {
        var requests = await dbContext.Subjects
            .Where(s => s.ApprovalStatus == "Pending" && !s.IsSystemSubject)
            .OrderByDescending(s => s.CreatedAt)
            .Select(s => new AccessRequestDto
            {
                SubjectId = s.Id,
                Name = s.Name,
                Message = s.AccessRequestMessage,
                CreatedAt = s.CreatedAt,
            })
            .ToListAsync(ct);

        return Ok(requests);
    }

    /// <inheritdoc cref="ITenantService.AddMemberAsync"/>
    [HttpPost("{subjectId:guid}/approve")]
    [RemoteCommand]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> Approve(
        Guid subjectId,
        [FromBody] ApproveAccessRequestRequest request,
        CancellationToken ct)
    {
        var subject = await dbContext.Subjects
            .FirstOrDefaultAsync(s => s.Id == subjectId && s.ApprovalStatus == "Pending", ct);

        if (subject == null)
            return NotFound();

        if (request.RoleIds.Count == 0 && (request.DirectPermissions == null || request.DirectPermissions.Count == 0))
            return Problem(detail: "At least one role or direct permission is required", statusCode: 400, title: "Bad Request");

        subject.ApprovalStatus = "Approved";
        subject.IsActive = true;
        subject.UpdatedAt = DateTime.UtcNow;
        await dbContext.SaveChangesAsync(ct);

        var tenantContext = tenantAccessor.Context;
        if (tenantContext != null)
        {
            var tenantId = tenantContext.TenantId;

            // Validate roleIds belong to this tenant
            if (request.RoleIds.Count > 0)
            {
                var validCount = await dbContext.TenantRoles
                    .CountAsync(r => r.TenantId == tenantId && request.RoleIds.Contains(r.Id), ct);
                if (validCount != request.RoleIds.Count)
                    return Problem(detail: "One or more role IDs do not belong to this tenant", statusCode: 400, title: "Bad Request");
            }

            await tenantService.AddMemberAsync(tenantId, subjectId, request.RoleIds, request.DirectPermissions, ct: ct);

            // Find owners by looking at members with the owner role slug
            var ownerIds = await dbContext.TenantMembers
                .Where(tm => tm.TenantId == tenantId
                    && tm.MemberRoles.Any(mr => mr.TenantRole.Slug == TenantPermissions.SeedRoles.Owner))
                .Select(tm => tm.SubjectId)
                .ToListAsync(ct);

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
    [RemoteCommand]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> Deny(Guid subjectId, CancellationToken ct)
    {
        var subject = await dbContext.Subjects
            .FirstOrDefaultAsync(s => s.Id == subjectId && s.ApprovalStatus == "Pending", ct);

        if (subject == null)
            return NotFound();

        var denyTenantContext = tenantAccessor.Context;
        if (denyTenantContext != null)
        {
            var ownerIds = await dbContext.TenantMembers
                .Where(tm => tm.TenantId == denyTenantContext.TenantId
                    && tm.MemberRoles.Any(mr => mr.TenantRole.Slug == TenantPermissions.SeedRoles.Owner))
                .Select(tm => tm.SubjectId)
                .ToListAsync(ct);

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
}
