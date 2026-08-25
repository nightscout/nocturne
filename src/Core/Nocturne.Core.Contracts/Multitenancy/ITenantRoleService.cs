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

    /// <summary>
    /// Returns the combined set of permissions a member has through their roles and direct grants,
    /// or an empty set when the membership does not resolve. Keyed on the membership id alone, so
    /// the caller must have established that the id belongs to its tenant.
    /// </summary>
    Task<List<string>> GetEffectivePermissionsAsync(Guid memberId, CancellationToken ct = default);

    /// <summary>
    /// Checks that every id in <paramref name="roleIds"/> names a role of
    /// <paramref name="tenantId"/>, and that <paramref name="granterScopes"/> already holds every
    /// permission those roles confer.
    /// </summary>
    /// <remarks>
    /// The single entry point for conferring a role, so that no conferring path can be added
    /// without both checks. Assigning a role hands out its permissions, so the grant ceiling that
    /// applies to a direct permission edit applies here too: the Administrator seed role holds
    /// <c>members.manage</c> and <c>roles.manage</c>, which without the ceiling is enough to attach
    /// the Owner role to a chosen subject and reach <c>*</c>.
    /// </remarks>
    /// <param name="tenantId">The tenant whose roles may be conferred.</param>
    /// <param name="roleIds">The roles being conferred. An empty set is valid.</param>
    /// <param name="granterScopes">
    /// The caller's resolved scopes. Passed rather than read so a background or service caller has
    /// to name the authority it is acting on. <see cref="Scope"/> atoms are a subset of
    /// the member-grantable scope vocabulary, so a resolved scope set is a valid granter set.
    /// </param>
    /// <param name="ct">A cancellation token.</param>
    Task<RoleGrantValidation> ValidateRoleGrantAsync(
        Guid tenantId,
        IReadOnlyCollection<Guid> roleIds,
        IReadOnlyCollection<string> granterScopes,
        CancellationToken ct = default);
}

/// <summary>
/// Outcome of <see cref="ITenantRoleService.ValidateRoleGrantAsync"/>. <c>ErrorCode</c> is stable
/// for the frontend to branch and localise on; <c>ErrorDescription</c> is diagnostic only.
/// </summary>
public record RoleGrantValidation(bool Ok, string? ErrorCode, string? ErrorDescription)
{
    /// <summary>A role id does not belong to the tenant.</summary>
    public const string ForeignRole = "role_not_in_tenant";

    /// <summary>The roles confer a permission the caller does not hold.</summary>
    public const string ExceedsGranter = "grant_exceeds_granter";

    public static RoleGrantValidation Valid { get; } = new(true, null, null);
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
