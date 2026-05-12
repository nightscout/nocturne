using Microsoft.AspNetCore.Mvc;
using OpenApi.Remote.Attributes;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;

namespace Nocturne.API.Controllers.V4.Identity;

/// <summary>
/// Controller for querying and managing tenant-scoped roles.
/// </summary>
/// <remarks>
/// Roles determine what permissions a tenant member holds. Role data is maintained by
/// <see cref="ITenantRoleService"/> and scoped to the resolved tenant via <see cref="ITenantAccessor"/>.
/// </remarks>
/// <seealso cref="ITenantRoleService"/>
[ApiController]
[Tags("Identity")]
[Route("api/v4/roles")]
[Produces("application/json")]
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
    /// </summary>
    /// <inheritdoc cref="ITenantRoleService.GetRolesAsync"/>
    [HttpGet]
    [RemoteQuery]
    [ProducesResponseType(typeof(List<TenantRoleDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetRoles(CancellationToken ct)
    {
        if (!HasPermission(TenantPermissions.RolesManage))
            return Forbid();

        var roles = await _roleService.GetRolesAsync(_tenantAccessor.TenantId, ct);
        return Ok(roles);
    }

    /// <summary>
    /// Create a custom role.
    /// </summary>
    /// <inheritdoc cref="ITenantRoleService.CreateRoleAsync"/>
    [HttpPost]
    [RemoteCommand(Invalidates = ["GetRoles"])]
    [ProducesResponseType(typeof(TenantRoleDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> CreateRole([FromBody] CreateRoleRequest request, CancellationToken ct)
    {
        if (!HasPermission(TenantPermissions.RolesManage))
            return Forbid();

        var role = await _roleService.CreateRoleAsync(
            _tenantAccessor.TenantId, request.Name, request.Description, request.Permissions, ct);
        return StatusCode(StatusCodes.Status201Created, role);
    }

    /// <summary>
    /// Update a role.
    /// </summary>
    /// <inheritdoc cref="ITenantRoleService.UpdateRoleAsync"/>
    [HttpPut("{id:guid}")]
    [RemoteCommand(Invalidates = ["GetRoles"])]
    [ProducesResponseType(typeof(TenantRoleDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> UpdateRole(Guid id, [FromBody] UpdateRoleRequest request, CancellationToken ct)
    {
        if (!HasPermission(TenantPermissions.RolesManage))
            return Forbid();

        try
        {
            var role = await _roleService.UpdateRoleAsync(id, request.Name, request.Description, request.Permissions, ct);
            return Ok(role);
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
    [RemoteCommand(Invalidates = ["GetRoles"])]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> DeleteRole(Guid id, CancellationToken ct)
    {
        if (!HasPermission(TenantPermissions.RolesManage))
            return Forbid();

        var result = await _roleService.DeleteRoleAsync(id, ct);
        if (!result.Success)
            return Problem(detail: result.ErrorDescription, statusCode: 400, title: result.ErrorCode);

        return NoContent();
    }

    private bool HasPermission(string permission)
    {
        var grantedScopes = HttpContext.Items["GrantedScopes"] as IReadOnlySet<string>;
        if (grantedScopes == null) return false;
        return TenantPermissions.HasPermission(grantedScopes, permission);
    }
}

public record CreateRoleRequest(string Name, string? Description, List<string> Permissions);
public record UpdateRoleRequest(string Name, string? Description, List<string> Permissions);
