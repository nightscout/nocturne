namespace Nocturne.Core.Contracts.Multitenancy;

/// <summary>
/// Manages tenant-scoped roles: CRUD for custom roles and seeding of system roles
/// (owner, member, follower). Each role carries a set of permission strings.
/// </summary>
/// <seealso cref="ITenantService"/>
/// <seealso cref="IMemberInviteService"/>
public interface ITenantRoleService
{
    /// <summary>Returns all roles defined for the specified tenant.</summary>
    Task<List<TenantRoleDto>> GetRolesAsync(Guid tenantId, CancellationToken ct = default);

    /// <summary>
    /// Returns a role by its ID within <paramref name="tenantId"/>, or null if the tenant has
    /// no such role. A role ID belonging to another tenant resolves to null.
    /// </summary>
    Task<TenantRoleDto?> GetRoleByIdAsync(Guid tenantId, Guid roleId, CancellationToken ct = default);

    /// <summary>Creates a new custom role with the specified permissions.</summary>
    Task<TenantRoleDto> CreateRoleAsync(Guid tenantId, string name, string? description, List<string> permissions, CancellationToken ct = default);

    /// <summary>
    /// Updates a role's name, description, and permissions. Returns null when
    /// <paramref name="tenantId"/> has no role with that ID.
    /// </summary>
    Task<TenantRoleDto?> UpdateRoleAsync(Guid tenantId, Guid roleId, string name, string? description, List<string> permissions, CancellationToken ct = default);

    /// <summary>
    /// Deletes a role if it is not a system role and has no members assigned. A role ID
    /// belonging to another tenant is reported as not found.
    /// </summary>
    Task<DeleteRoleResult> DeleteRoleAsync(Guid tenantId, Guid roleId, CancellationToken ct = default);

    /// <summary>Creates the default system roles (owner, member, follower) for a newly provisioned tenant.</summary>
    Task SeedRolesForTenantAsync(Guid tenantId, CancellationToken ct = default);

    /// <summary>Returns the combined set of permissions a member has through their roles and direct grants.</summary>
    Task<List<string>> GetEffectivePermissionsAsync(Guid memberId, CancellationToken ct = default);
}

/// <summary>
/// Projection of a tenant role including its permission set and member count.
/// </summary>
public record TenantRoleDto(
    Guid Id,
    string Name,
    string Slug,
    string? Description,
    List<string> Permissions,
    bool IsSystem,
    int MemberCount,
    DateTime SysCreatedAt
);

/// <summary>
/// Result of a role deletion attempt. Deletion fails if the role is a system role
/// or has members currently assigned.
/// </summary>
public record DeleteRoleResult(bool Success, string? ErrorCode, string? ErrorDescription);
