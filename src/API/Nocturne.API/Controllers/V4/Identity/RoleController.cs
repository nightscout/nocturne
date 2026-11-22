using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Attributes;
using OpenApi.Remote.Attributes;
using Nocturne.API.Extensions;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.API.Services.Auth;
using Nocturne.Core.Models.Authorization;

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
    public async Task<IActionResult> GetRoles(CancellationToken ct)
    {
        var tenantId = _tenantAccessor.TenantId;
        var roles = HttpContext.HasScope(Scope.RolesManage)
            ? await _roleService.GetRolesAsync(tenantId, HttpContext.GetGrantedScopes(), ct)
            : await _roleService.GetAssignableRolesAsync(tenantId, HttpContext.GetGrantedScopes(), ct);

        var holders = await _roleService.GetRoleHoldersAsync(tenantId, HttpContext.CallerAccessEndsAt(), ct);
        return Ok(roles
            .Select(r => r with
            {
                OutlastsCaller = holders.HeldPast.Contains(r.Id),
                InviteOutlastsCaller = holders.InvitedPast.Contains(r.Id),
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
        var grantedScopes = HttpContext.GetGrantedScopes();
        var violation = Scope.ValidateGrant(request.Permissions, grantedScopes);
        if (violation != null)
            return GrantProblem(violation);

        var result = await _roleService.CreateRoleAsync(
            _tenantAccessor.TenantId, request.Name, request.Description, request.Permissions, grantedScopes, ct);
        return result.Role is null ? NameProblem(result) : StatusCode(StatusCodes.Status201Created, result.Role);
    }

    /// <summary>
    /// Update a role.
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
        var holders = await _roleService.GetRoleHoldersAsync(
            _tenantAccessor.TenantId, HttpContext.CallerAccessEndsAt(), ct);
        if (RoleHeldPastCallerRefusal(holders, id) is { } heldPast)
            return heldPast;

        var grantedScopes = HttpContext.GetGrantedScopes();
        var violation = Scope.ValidateGrant(request.Permissions, grantedScopes);
        if (violation != null)
            return GrantProblem(violation);

        try
        {
            var result = await _roleService.UpdateRoleAsync(
                _tenantAccessor.TenantId, id, request.Name, request.Description, request.Permissions, grantedScopes, ct);
            if (result == null)
                return NotFound();

            if (result.Role is null)
                return NameProblem(result);

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
    /// Delete a role.
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
        var holders = await _roleService.GetRoleHoldersAsync(
            _tenantAccessor.TenantId, HttpContext.CallerAccessEndsAt(), ct);
        if (RoleHeldPastCallerRefusal(holders, id) is { } heldPast)
            return heldPast;

        var result = await _roleService.DeleteRoleAsync(_tenantAccessor.TenantId, id, ct);
        if (!result.Success)
            return Problem(detail: result.ErrorDescription, statusCode: 400, title: result.ErrorCode);

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

    private ObjectResult NameProblem(SaveRoleResult result) =>
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
