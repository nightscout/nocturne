using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OpenApi.Remote.Attributes;
using Nocturne.API.Extensions;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Nocturne.API.Services.Auth;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.API.Controllers.V4.Identity;

/// <summary>
/// Public-facing member invite endpoints for accepting invites and listing members.
/// Also provides member role/permission management endpoints.
/// </summary>
/// <seealso cref="IMemberInviteService"/>
/// <seealso cref="ITenantService"/>
/// <seealso cref="ITenantRoleService"/>
[ApiController]
[Tags("Identity")]
[Route("api/v4/member-invites")]
[Produces("application/json")]
public class MemberInviteController : ControllerBase
{
    private readonly IMemberInviteService _memberInviteService;
    private readonly ITenantService _tenantService;
    private readonly ITenantRoleService _tenantRoleService;
    private readonly ITenantAccessor _tenantAccessor;
    private readonly NocturneDbContext _dbContext;

    /// <summary>
    /// Initializes a new instance of <see cref="MemberInviteController"/>.
    /// </summary>
    /// <param name="memberInviteService">Service for invite token lifecycle management.</param>
    /// <param name="tenantService">Service for tenant membership operations.</param>
    /// <param name="tenantRoleService">Service for member role assignment.</param>
    /// <param name="tenantAccessor">Accessor for the current request tenant context.</param>
    /// <param name="dbContext">Database context for direct entity access.</param>
    public MemberInviteController(
        IMemberInviteService memberInviteService,
        ITenantService tenantService,
        ITenantRoleService tenantRoleService,
        ITenantAccessor tenantAccessor,
        NocturneDbContext dbContext)
    {
        _memberInviteService = memberInviteService;
        _tenantService = tenantService;
        _tenantRoleService = tenantRoleService;
        _tenantAccessor = tenantAccessor;
        _dbContext = dbContext;
    }

    /// <summary>
    /// Get invite info for the accept page (anonymous).
    /// </summary>
    [HttpGet("{token}/info")]
    [AllowAnonymous]
    [RemoteQuery]
    [ProducesResponseType(typeof(MemberInviteInfo), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetInviteInfo(string token)
    {
        var invite = await _memberInviteService.GetInviteByTokenAsync(token);
        if (invite == null)
            return NotFound();

        return Ok(invite);
    }

    /// <summary>
    /// Accept an invite and join the tenant.
    /// </summary>
    [HttpPost("{token}/accept")]
    [Authorize]
    [RemoteCommand]
    [ProducesResponseType(typeof(AcceptMemberInviteResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> AcceptInvite(string token)
    {
        var subjectId = HttpContext.GetSubjectId();
        if (subjectId == null)
            return Unauthorized();

        var result = await _memberInviteService.AcceptInviteAsync(token, subjectId.Value);

        if (!result.Success)
            return BadRequest(result);

        return Ok(result);
    }

    /// <summary>
    /// List all members of the current tenant.
    /// </summary>
    [HttpGet("members")]
    [RemoteQuery]
    [ProducesResponseType(typeof(List<TenantMemberDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetMembers(CancellationToken ct)
    {
        var tenantId = _tenantAccessor.TenantId;
        var tenant = await _tenantService.GetByIdAsync(tenantId, ct);
        if (tenant == null)
            return NotFound();

        return Ok(tenant.Members);
    }

    /// <summary>
    /// List followers of the current tenant (members with the follower role).
    /// </summary>
    [HttpGet("members/followers")]
    [RemoteQuery]
    [ProducesResponseType(typeof(List<TenantMemberDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetFollowers(CancellationToken ct)
    {
        var tenantId = _tenantAccessor.TenantId;
        var tenant = await _tenantService.GetByIdAsync(tenantId, ct);
        if (tenant == null)
            return NotFound();

        var followers = tenant.Members
            .Where(m => m.Roles.Any(r => r.Slug == TenantPermissions.SeedRoles.Follower))
            .ToList();

        return Ok(followers);
    }

    /// <summary>
    /// Set roles for a member (replaces all role assignments).
    /// </summary>
    [HttpPut("members/{id:guid}/roles")]
    [RemoteCommand(Invalidates = ["GetMembers"])]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SetMemberRoles(
        Guid id,
        [FromBody] SetMemberRolesRequest request,
        [FromServices] PublicAccessCacheService publicAccessCache,
        CancellationToken ct)
    {
        if (!HasPermission(TenantPermissions.MembersManage))
            return Forbid();

        var tenantId = _tenantAccessor.TenantId;
        var member = await _dbContext.TenantMembers
            .Include(m => m.MemberRoles)
            .Include(m => m.Subject)
            .Where(m => m.Id == id && m.TenantId == tenantId && m.RevokedAt == null)
            .FirstOrDefaultAsync(ct);

        if (member == null)
            return NotFound();

        if (request.RoleIds.Count == 0 && (member.DirectPermissions == null || member.DirectPermissions.Count == 0))
            return Problem(detail: "Cannot remove all roles when member has no direct permissions", statusCode: 400, title: "Bad Request");

        // Validate roleIds belong to this tenant
        if (request.RoleIds.Count > 0)
        {
            var validCount = await _dbContext.TenantRoles
                .CountAsync(r => r.TenantId == tenantId && request.RoleIds.Contains(r.Id), ct);
            if (validCount != request.RoleIds.Count)
                return Problem(detail: "One or more role IDs do not belong to this tenant", statusCode: 400, title: "Bad Request");
        }

        // Remove existing role assignments
        _dbContext.TenantMemberRoles.RemoveRange(member.MemberRoles);

        // Add new role assignments
        var now = DateTime.UtcNow;
        foreach (var roleId in request.RoleIds)
        {
            _dbContext.TenantMemberRoles.Add(new TenantMemberRoleEntity
            {
                Id = Guid.CreateVersion7(),
                TenantMemberId = member.Id,
                TenantRoleId = roleId,
                SysCreatedAt = now,
            });
        }

        member.SysUpdatedAt = now;
        await _dbContext.SaveChangesAsync(ct);

        if (member.Subject?.IsSystemSubject == true && member.Subject.Name == "Public")
            publicAccessCache.Evict(tenantId);

        return NoContent();
    }

    /// <summary>
    /// Set direct permissions for a member.
    /// </summary>
    [HttpPut("members/{id:guid}/permissions")]
    [RemoteCommand(Invalidates = ["GetMembers"])]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SetMemberPermissions(
        Guid id,
        [FromBody] SetMemberPermissionsRequest request,
        [FromServices] PublicAccessCacheService publicAccessCache,
        CancellationToken ct)
    {
        if (!HasPermission(TenantPermissions.MembersManage))
            return Forbid();

        var tenantId = _tenantAccessor.TenantId;
        var member = await _dbContext.TenantMembers
            .Include(m => m.MemberRoles)
            .Include(m => m.Subject)
            .Where(m => m.Id == id && m.TenantId == tenantId && m.RevokedAt == null)
            .FirstOrDefaultAsync(ct);

        if (member == null)
            return NotFound();

        if ((request.DirectPermissions == null || request.DirectPermissions.Count == 0) && member.MemberRoles.Count == 0)
            return Problem(detail: "Cannot remove all permissions when member has no roles", statusCode: 400, title: "Bad Request");

        member.DirectPermissions = request.DirectPermissions;
        member.SysUpdatedAt = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync(ct);

        if (member.Subject?.IsSystemSubject == true && member.Subject.Name == "Public")
            publicAccessCache.Evict(tenantId);

        return NoContent();
    }

    /// <summary>
    /// Get effective permissions for a member (union of role permissions + direct permissions).
    /// </summary>
    [HttpGet("members/{id:guid}/effective-permissions")]
    [RemoteQuery]
    [ProducesResponseType(typeof(List<string>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetEffectivePermissions(Guid id, CancellationToken ct)
    {
        if (!HasPermission(TenantPermissions.SharingManage))
            return Forbid();

        var tenantId = _tenantAccessor.TenantId;
        var member = await _dbContext.TenantMembers
            .Where(m => m.Id == id && m.TenantId == tenantId && m.RevokedAt == null)
            .FirstOrDefaultAsync(ct);

        if (member == null)
            return NotFound();

        var permissions = await _tenantRoleService.GetEffectivePermissionsAsync(id, ct);
        return Ok(permissions);
    }

    /// <summary>
    /// Update the 24-hour data limit for a member.
    /// </summary>
    [HttpPut("members/{id:guid}/limit-to-24-hours")]
    [RemoteCommand(Invalidates = ["GetMembers"])]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SetMemberLimitTo24Hours(
        Guid id,
        [FromBody] SetMemberLimitTo24HoursRequest request,
        [FromServices] PublicAccessCacheService publicAccessCache,
        CancellationToken ct)
    {
        if (!HasPermission(TenantPermissions.MembersManage))
            return Forbid();

        var tenantId = _tenantAccessor.TenantId;
        var member = await _dbContext.TenantMembers
            .Include(m => m.Subject)
            .Where(m => m.Id == id && m.TenantId == tenantId && m.RevokedAt == null)
            .FirstOrDefaultAsync(ct);

        if (member == null)
            return NotFound();

        member.LimitTo24Hours = request.LimitTo24Hours;
        member.SysUpdatedAt = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync(ct);

        if (member.Subject?.IsSystemSubject == true && member.Subject.Name == "Public")
            publicAccessCache.Evict(tenantId);

        return NoContent();
    }

    private bool HasPermission(string permission)
    {
        var grantedScopes = HttpContext.Items["GrantedScopes"] as IReadOnlySet<string>;
        if (grantedScopes == null) return false;
        return TenantPermissions.HasPermission(grantedScopes, permission);
    }
}

public record SetMemberRolesRequest(List<Guid> RoleIds);
public record SetMemberPermissionsRequest(List<string>? DirectPermissions);
public record SetMemberLimitTo24HoursRequest(bool LimitTo24Hours);
