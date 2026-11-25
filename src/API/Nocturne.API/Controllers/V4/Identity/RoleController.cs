using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Attributes;
using OpenApi.Remote.Attributes;
using Nocturne.API.Extensions;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.API.Services.Auth;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Controllers.V4.Identity;

/// <summary>
/// The roles that group permissions for the tenant's members. A caller can list roles, create a
/// role, update a role and delete a role.
/// </summary>
/// <remarks>
/// Roles determine what permissions a tenant member holds. Role data is maintained by
/// <see cref="ITenantRoleService"/> and scoped to the resolved tenant via <see cref="ITenantAccessor"/>.
/// </remarks>
/// <seealso cref="ITenantRoleService"/>
[ApiController]
[System.ComponentModel.DisplayName("Roles")]
[Tags("Identity")]
[Route("api/v4/roles")]
[Produces("application/json")]
[Authorize]
public class RoleController : ControllerBase
{
    private readonly ITenantRoleService _roleService;
    private readonly ITenantAccessor _tenantAccessor;

    public RoleController(ITenantRoleService roleService, ITenantAccessor tenantAccessor)
    {
        _roleService = roleService;
        _tenantAccessor = tenantAccessor;
    }

    /// <summary>
    /// List roles for the current tenant.
    /// A <c>roles.manage</c> caller gets every role; a <c>members.invite</c> or <c>members.manage</c> caller gets only the roles it can grant. <c>CanGrant</c> on each role says whether the caller may confer it.
    /// </summary>
    /// <inheritdoc cref="ITenantRoleService.GetRolesAsync"/>
    /// <remarks>
    /// A <c>roles.manage</c> caller gets every role; a <c>members.invite</c> or <c>members.manage</c> caller gets only the roles it can grant.
    /// <c>CanGrant</c> on each role says whether the caller may confer it.
    /// </remarks>
    [HttpGet]
    [RequireScope(Scope.RolesManage, Scope.MembersInvite, Scope.MembersManage)]
    [RemoteQuery]
    [ProducesResponseType(typeof(List<TenantRoleDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetRoles(
        [FromServices] ITenantMembershipRepository members, [FromServices] TimeProvider time, CancellationToken ct)
    {
        var tenantId = _tenantAccessor.TenantId;
        var ceiling = HttpContext.CallerGrantCeiling() ?? GrantCeiling.Nothing;
        var roles = HttpContext.HasScope(Scope.RolesManage)
            ? await _roleService.GetRolesAsync(tenantId, ceiling.Scopes, ct)
            : await _roleService.GetAssignableRolesAsync(tenantId, ceiling.Scopes, ct);

        var holders = await _roleService.GetRoleHoldersAsync(tenantId, ceiling.AccessEndsAt, ct);
        var callerIsOwner = HttpContext.GetSubjectId() is { } subjectId
            && await members.FindMemberAsync(tenantId, subjectId, ct) is { IsOwner: true } member
            && !MembershipLiveness.HasEnded(member.AccessEndsAt, time.GetUtcNow().UtcDateTime);
        return Ok(roles
            .Select(r => r with
            {
                OutlastsCaller = holders.HeldPast.Contains(r.Id),
                InviteOutlastsCaller = holders.InvitedPast.Contains(r.Id),
                HeldByTeam = holders.HeldByTeam.Contains(r.Id),
                OnlyOwnersMayWiden = holders.HeldByTeam.Contains(r.Id) && !callerIsOwner,
                TeamRefusedPermissions = holders.HeldByTeam.Contains(r.Id)
                    ? Scope.AdministrativePermissions.Order(StringComparer.Ordinal).ToList()
                    : [],
                CanGiveToTeam = TeamAccessRules.MayGiveToTeam(r),
                CoveredPermissions = holders.HeldByTeam.Contains(r.Id) && !callerIsOwner
                    ? Scope.PermissionAtoms.Where(a => Scope.Satisfies(r.Permissions, a)).Order(StringComparer.Ordinal).ToList()
                    : null,
            })
            .ToList());
    }

    /// <summary>
    /// Create a custom role.
    /// </summary>
    /// <inheritdoc cref="ITenantRoleService.CreateRoleAsync"/>
    [HttpPost]
    [RequireScope(Scope.RolesManage)]
    [RemoteCommand(Invalidates = ["GetRoles"])]
    [ProducesResponseType(typeof(TenantRoleDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CreateRole([FromBody] CreateRoleRequest request, CancellationToken ct)
    {
        if (HttpContext.CallerGrantCeiling() is not { } ceiling)
            return this.CallerAccessFromTeamProblem();

        var grantedScopes = ceiling.Scopes;
        var violation = Scope.ValidateGrant(request.Permissions, grantedScopes);
        if (violation != null)
            return GrantProblem(violation);

        var result = await _roleService.CreateRoleAsync(
            _tenantAccessor.TenantId, request.Name, request.Description, request.Permissions, grantedScopes, ct);
        return result.Role is null ? SaveProblem(result) : StatusCode(StatusCodes.Status201Created, result.Role);
    }

    /// <summary>
    /// Update a role. A role a team's access gives cannot be given a permission that runs the site
    /// (409, <c>role_held_by_team</c>), and only an owner may add a permission to it
    /// (409, <c>role_held_by_team_owner_only</c>).
    /// </summary>
    /// <inheritdoc cref="ITenantRoleService.UpdateRoleAsync"/>
    [HttpPut("{id:guid}")]
    [RequireScope(Scope.RolesManage)]
    [RemoteCommand(Invalidates = ["GetRoles"])]
    [ProducesResponseType(typeof(TenantRoleDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> UpdateRole(
        Guid id,
        [FromBody] UpdateRoleRequest request,
        [FromServices] PublicAccessCacheService publicAccessCache,
        CancellationToken ct)
    {
        if (await HttpContext.IsCallerInactiveAsync(ct))
            return Challenge();

        if (HttpContext.CallerGrantCeiling() is not { } ceiling)
            return this.CallerAccessFromTeamProblem();

        var holders = await _roleService.GetRoleHoldersAsync(_tenantAccessor.TenantId, ceiling.AccessEndsAt, ct);
        if (RoleHeldPastCallerRefusal(holders, id) is { } heldPast)
            return heldPast;

        var grantedScopes = ceiling.Scopes;
        var violation = Scope.ValidateGrant(request.Permissions, grantedScopes);
        if (violation != null)
            return GrantProblem(violation);

        try
        {
            var result = await _roleService.UpdateRoleAsync(
                _tenantAccessor.TenantId, id, request.Name, request.Description, request.Permissions, grantedScopes,
                HttpContext.GetSubjectId(), ct);
            if (result == null)
                return NotFound();

            if (result.Role is null)
                return SaveProblem(result);

            if (holders.HeldByPublic.Contains(id))
                publicAccessCache.Evict(_tenantAccessor.TenantId);

            return Ok(result.Role);
        }
        catch (InvalidOperationException ex)
        {
            return Problem(detail: ex.Message, statusCode: 400, title: "Bad Request");
        }
    }

    /// <summary>
    /// Delete a role. Refused while a team's access gives it (400, <c>role_held_by_team</c>).
    /// </summary>
    /// <inheritdoc cref="ITenantRoleService.DeleteRoleAsync"/>
    [HttpDelete("{id:guid}")]
    [RequireScope(Scope.RolesManage)]
    [RemoteCommand(Invalidates = ["GetRoles"])]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> DeleteRole(
        Guid id, [FromServices] PublicAccessCacheService publicAccessCache, CancellationToken ct)
    {
        if (HttpContext.CallerGrantCeiling() is not { } ceiling)
            return this.CallerAccessFromTeamProblem();

        var holders = await _roleService.GetRoleHoldersAsync(_tenantAccessor.TenantId, ceiling.AccessEndsAt, ct);
        if (RoleHeldPastCallerRefusal(holders, id) is { } heldPast)
            return heldPast;

        var result = await _roleService.DeleteRoleAsync(_tenantAccessor.TenantId, id, ct);
        if (!result.Success)
            return this.ProblemWithCode(
                result.ErrorCode!, result.ErrorDescription, StatusCodes.Status400BadRequest, title: result.ErrorCode);

        if (holders.HeldByPublic.Contains(id))
            publicAccessCache.Evict(_tenantAccessor.TenantId);

        return NoContent();
    }

    /// <summary>
    /// The 403 for a change to a role a member or a live invite whose access outlasts the caller's
    /// holds (the Public member always does), or null when the caller may go on. A role edit
    /// changes what every holder can do.
    /// </summary>
    private ObjectResult? RoleHeldPastCallerRefusal(RoleHolders holders, Guid roleId) =>
        holders.HeldPast.Contains(roleId)
            ? Problem(detail: HttpContextExtensions.RoleHeldPastCallerDetail, statusCode: StatusCodes.Status403Forbidden)
            : holders.InvitedPast.Contains(roleId)
                ? Problem(detail: HttpContextExtensions.RoleInvitedPastCallerDetail, statusCode: StatusCodes.Status403Forbidden)
                : null;

    private ObjectResult SaveProblem(SaveRoleResult result) =>
        this.ProblemWithCode(
            result.ErrorCode!, result.ErrorDescription, StatusCodes.Status409Conflict, title: result.ErrorCode);

    /// <summary>
    /// An unknown permission is malformed input; exceeding the ceiling is a refusal.
    /// </summary>
    private ObjectResult GrantProblem(GrantCeilingViolation violation) =>
        violation.Code == GrantCeilingViolation.UnknownPermission
            ? Problem(detail: violation.Description, statusCode: 400, title: "Bad Request")
            : Problem(detail: violation.Description, statusCode: 403, title: "Forbidden");

}

public record CreateRoleRequest(string Name, string? Description, List<string> Permissions);
public record UpdateRoleRequest(string Name, string? Description, List<string> Permissions);
