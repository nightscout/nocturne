using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OpenApi.Remote.Attributes;
using Nocturne.API.Extensions;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models;
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
[Authorize]
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

    /// <inheritdoc cref="IMemberInviteService.CreateInviteAsync"/>
    [HttpPost]
    [RemoteCommand(Invalidates = ["ListInvites"])]
    [ProducesResponseType(typeof(MemberInviteResult), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> CreateInvite([FromBody] CreateMemberInviteRequest request)
    {
        if (!HasPermission(TenantPermissions.MembersInvite))
            return Forbid();

        var subjectId = HttpContext.GetSubjectId();
        if (subjectId == null)
            return Unauthorized();

        // The clamp is an access boundary enforced in RLS via app.share_full_history, so a
        // clamped member minting an unclamped invite would widen past their own ceiling by
        // handing the wider access to someone else. Lifting an existing member's clamp already
        // requires members.manage and is refused for self-edits.
        var authContext = HttpContext.Items["AuthContext"] as AuthContext;
        var limitTo24Hours = request.LimitTo24Hours || authContext?.LimitTo24Hours == true;

        try
        {
            var result = await _memberInviteService.CreateInviteAsync(
                _tenantAccessor.TenantId,
                subjectId.Value,
                HttpContext.GetGrantedScopes(),
                request.RoleIds,
                request.DirectPermissions,
                request.Label,
                request.ExpiresInDays,
                request.MaxUses,
                limitTo24Hours,
                $"{Request.Scheme}://{Request.Host}");

            return StatusCode(StatusCodes.Status201Created, result);
        }
        catch (ArgumentException ex)
        {
            return Problem(detail: ex.Message, statusCode: 400, title: "Bad Request");
        }
    }

    /// <inheritdoc cref="IMemberInviteService.GetInvitesForTenantAsync"/>
    [HttpGet]
    [RemoteQuery]
    [ProducesResponseType(typeof(List<MemberInviteInfo>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> ListInvites()
    {
        if (!HasPermission(TenantPermissions.MembersInvite))
            return Forbid();

        var invites = await _memberInviteService.GetInvitesForTenantAsync(_tenantAccessor.TenantId);
        return Ok(invites);
    }

    /// <inheritdoc cref="IMemberInviteService.RevokeInviteAsync"/>
    [HttpDelete("{inviteId:guid}")]
    [RemoteCommand(Invalidates = ["ListInvites"])]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RevokeInvite(Guid inviteId)
    {
        if (!HasPermission(TenantPermissions.MembersInvite))
            return Forbid();

        var revoked = await _memberInviteService.RevokeInviteAsync(inviteId, _tenantAccessor.TenantId);
        return revoked ? NoContent() : NotFound();
    }

    /// <inheritdoc cref="ITenantService.RemoveMemberAsync"/>
    [HttpDelete("members/{subjectId:guid}")]
    [RemoteCommand(Invalidates = ["GetMembers"])]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> RemoveMember(Guid subjectId, CancellationToken ct)
    {
        if (!HasPermission(TenantPermissions.MembersManage))
            return Forbid();

        var tenantId = _tenantAccessor.TenantId;
        var member = await _dbContext.TenantMembers
            .Include(m => m.Subject)
            .Where(m => m.TenantId == tenantId && m.SubjectId == subjectId)
            .FirstOrDefaultAsync(ct);

        if (member == null)
            return NoContent();

        if (member.Subject?.IsSystemSubject == true)
            return Problem(detail: "Cannot remove system subject memberships", statusCode: 400, title: "Bad Request");

        // Prevent removing the last member with the owner role
        var isOwner = await _dbContext.TenantMemberRoles
            .AnyAsync(mr => mr.TenantMemberId == member.Id
                && mr.TenantRole.Slug == TenantPermissions.SeedRoles.Owner, ct);

        if (isOwner)
        {
            var ownerCount = await _dbContext.TenantMemberRoles
                .CountAsync(mr => mr.TenantRole.TenantId == tenantId
                    && mr.TenantRole.Slug == TenantPermissions.SeedRoles.Owner
                    && mr.TenantMember.RevokedAt == null, ct);

            if (ownerCount <= 1)
                return Problem(detail: "Cannot remove the last owner of a tenant", statusCode: 400, title: "Bad Request");
        }

        await _tenantService.RemoveMemberAsync(tenantId, subjectId, ct);
        return NoContent();
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
            .Where(m => m.Roles.Any(r => r.Slug == TenantPermissions.SeedRoles.Viewer))
            .ToList();

        return Ok(followers);
    }

    /// <summary>
    /// Set roles for a member (replaces all role assignments).
    /// </summary>
    [HttpPut("members/{id:guid}/roles")]
    [RemoteCommand(Invalidates = ["GetMembers"])]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
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
            .Where(m => m.Id == id && m.TenantId == tenantId)
            .FirstOrDefaultAsync(ct);

        if (member == null)
            return NotFound();

        if (IsCallersOwnMembership(member))
            return Problem(detail: SelfEditDetail, statusCode: 400, title: "Bad Request");

        if (request.RoleIds.Count == 0 && (member.DirectPermissions == null || member.DirectPermissions.Count == 0))
            return Problem(detail: "Cannot remove all roles when member has no direct permissions", statusCode: 400, title: "Bad Request");

        var roleGrant = await _tenantRoleService.ValidateRoleGrantAsync(
            tenantId, request.RoleIds, HttpContext.GetGrantedScopes(), ct);
        if (!roleGrant.Ok)
            return RoleGrantProblem(roleGrant);

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
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
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
            .Where(m => m.Id == id && m.TenantId == tenantId)
            .FirstOrDefaultAsync(ct);

        if (member == null)
            return NotFound();

        if (IsCallersOwnMembership(member))
            return Problem(detail: SelfEditDetail, statusCode: 400, title: "Bad Request");

        if ((request.DirectPermissions == null || request.DirectPermissions.Count == 0) && member.MemberRoles.Count == 0)
            return Problem(detail: "Cannot remove all permissions when member has no roles", statusCode: 400, title: "Bad Request");

        // The Public system subject serves the anonymous share viewer, so the granter's own
        // permissions are the wrong bound: an owner holding "*" would otherwise be able to give an
        // unauthenticated reader everything. ShareLinkService.SetScopesAsync bounds the same rows to
        // PublicShareScopes; both writers have to agree or the narrower one is decorative.
        var isPublicSubject = member.Subject?.IsSystemSubject == true && member.Subject.Name == "Public";
        if (isPublicSubject)
        {
            var outsideShareVocabulary = (request.DirectPermissions ?? [])
                .Where(p => !TenantPermissions.PublicShareScopes.Contains(p))
                .ToList();
            if (outsideShareVocabulary.Count > 0)
            {
                return Problem(
                    detail: $"Public access cannot be granted: {string.Join(", ", outsideShareVocabulary)}.",
                    statusCode: 400,
                    title: "Bad Request");
            }
        }
        else
        {
            var violation = TenantPermissions.ValidateGrant(
                request.DirectPermissions, HttpContext.GetGrantedScopes());
            if (violation != null)
                return GrantProblem(violation);
        }

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
            .Where(m => m.Id == id && m.TenantId == tenantId)
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
            .Where(m => m.Id == id && m.TenantId == tenantId)
            .FirstOrDefaultAsync(ct);

        if (member == null)
            return NotFound();

        // The clamp is enforced in RLS via app.share_full_history, so lifting your own is a
        // self-widening edit — the same class the role and permission editors refuse.
        if (IsCallersOwnMembership(member))
            return Problem(detail: SelfEditDetail, statusCode: 400, title: "Bad Request");

        member.LimitTo24Hours = request.LimitTo24Hours;
        member.SysUpdatedAt = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync(ct);

        if (member.Subject?.IsSystemSubject == true && member.Subject.Name == "Public")
            publicAccessCache.Evict(tenantId);

        return NoContent();
    }

    private bool HasPermission(string permission)
        => TenantPermissions.HasPermission(HttpContext.GetGrantedScopes(), permission);

    private const string SelfEditDetail =
        "Cannot change your own roles or permissions; ask another member with members.manage.";

    /// <summary>
    /// True when the target membership belongs to the calling subject. Role and permission
    /// edits are routed away from the caller's own membership: a self-edit is the only way a
    /// grant decision can widen the granter's own ceiling within a request, and it is also the
    /// path by which a sole owner can strip their own administrative access.
    /// </summary>
    private bool IsCallersOwnMembership(TenantMemberEntity member)
        => HttpContext.GetSubjectId() is { } subjectId && member.SubjectId == subjectId;

    /// <summary>
    /// An unknown permission is malformed input; exceeding the ceiling is a refusal.
    /// </summary>
    private ObjectResult GrantProblem(GrantCeilingViolation violation) =>
        violation.Code == GrantCeilingViolation.UnknownPermission
            ? Problem(detail: violation.Description, statusCode: 400, title: "Bad Request")
            : Problem(detail: violation.Description, statusCode: 403, title: "Forbidden");

    /// <summary>
    /// A foreign role id is malformed input; a role conferring more than the caller holds is a refusal.
    /// </summary>
    private ObjectResult RoleGrantProblem(RoleGrantValidation validation) =>
        validation.ErrorCode == RoleGrantValidation.ForeignRole
            ? Problem(detail: validation.ErrorDescription, statusCode: 400, title: "Bad Request")
            : Problem(detail: validation.ErrorDescription, statusCode: 403, title: "Forbidden");
}

public class CreateMemberInviteRequest
{
    public List<Guid> RoleIds { get; set; } = [];
    public List<string>? DirectPermissions { get; set; }
    public string? Label { get; set; }
    public int ExpiresInDays { get; set; } = 7;
    public int? MaxUses { get; set; }
    public bool LimitTo24Hours { get; set; }
}

public record SetMemberRolesRequest(List<Guid> RoleIds);
public record SetMemberPermissionsRequest(List<string>? DirectPermissions);
public record SetMemberLimitTo24HoursRequest(bool LimitTo24Hours);
