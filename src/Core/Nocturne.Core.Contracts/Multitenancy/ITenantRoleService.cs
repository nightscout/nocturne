using Nocturne.Core.Models.Authorization;

namespace Nocturne.Core.Contracts.Multitenancy;

/// <summary>
/// Manages tenant-scoped roles: CRUD for custom roles and seeding of system roles
/// (owner, member, follower). Each role carries a set of permission strings.
/// </summary>
/// <seealso cref="ITenantService"/>
/// <seealso cref="IMemberInviteService"/>
public interface ITenantRoleService
{
    /// <summary>
    /// Every role of <paramref name="tenantId"/>, each marked with whether
    /// <paramref name="granterScopes"/> could confer it.
    /// </summary>
    Task<List<TenantRoleDto>> GetRolesAsync(
        Guid tenantId, IReadOnlyCollection<string> granterScopes, CancellationToken ct = default);

    /// <summary>
    /// The roles of <paramref name="tenantId"/> that <paramref name="granterScopes"/> could confer:
    /// exactly those for which <see cref="ValidateRoleGrantAsync"/> succeeds.
    /// </summary>
    Task<List<TenantRoleDto>> GetAssignableRolesAsync(
        Guid tenantId, IReadOnlyCollection<string> granterScopes, CancellationToken ct = default);

    /// <summary>
    /// Who holds the roles of <paramref name="tenantId"/>, from one read of its members and, when
    /// <paramref name="callerEnd"/> is not null, its invites. See <see cref="RoleHolders"/>.
    /// </summary>
    Task<RoleHolders> GetRoleHoldersAsync(Guid tenantId, DateTime? callerEnd, CancellationToken ct = default);

    /// <summary>
    /// Returns a role by its ID within <paramref name="tenantId"/>, or null if the tenant has
    /// no such role. A role ID belonging to another tenant resolves to null.
    /// </summary>
    Task<TenantRoleDto?> GetRoleByIdAsync(
        Guid tenantId, Guid roleId, IReadOnlyCollection<string> granterScopes, CancellationToken ct = default);

    /// <summary>
    /// Creates a new custom role with the specified permissions. A name whose slug is reserved or
    /// already used by another role of the tenant is refused with <see cref="SaveRoleResult"/>.
    /// </summary>
    Task<SaveRoleResult> CreateRoleAsync(
        Guid tenantId,
        string name,
        string? description,
        List<string> permissions,
        IReadOnlyCollection<string> granterScopes,
        CancellationToken ct = default);

    /// <summary>
    /// Updates a role's name, description, and permissions. Returns null when
    /// <paramref name="tenantId"/> has no role with that ID. A new name whose slug is reserved or
    /// already used by another role of the tenant is refused with <see cref="SaveRoleResult"/>; a
    /// name that keeps the role's own slug is not. <paramref name="editorSubjectId"/> is the person
    /// making the edit: only an owner may widen a role a team holds.
    /// </summary>
    Task<SaveRoleResult?> UpdateRoleAsync(
        Guid tenantId,
        Guid roleId,
        string name,
        string? description,
        List<string> permissions,
        IReadOnlyCollection<string> granterScopes,
        Guid? editorSubjectId,
        CancellationToken ct = default);

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
    /// The permissions the roles in <paramref name="roleIds"/> confer on <paramref name="tenantId"/>.
    /// Ids that name no role of the tenant contribute nothing.
    /// </summary>
    Task<List<string>> GetRolePermissionsAsync(
        Guid tenantId, IReadOnlyCollection<Guid> roleIds, CancellationToken ct = default);

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
/// The roles held, by group, for a caller whose access ends at a given instant.
/// </summary>
/// <param name="HeldPast">Roles held by a member whose access outlasts the caller's, which includes the Public member since its access never ends. Empty for a caller whose access does not end.</param>
/// <param name="InvitedPast">Roles a live invite gives, where the invite's access outlasts the caller's. Empty for a caller whose access does not end.</param>
/// <param name="HeldByPublic">Roles the Public member holds, for any caller.</param>
/// <param name="HeldByTeam">Roles a team's access gives, for any caller.</param>
public sealed record RoleHolders(
    HashSet<Guid> HeldPast, HashSet<Guid> InvitedPast, HashSet<Guid> HeldByPublic, IReadOnlySet<Guid> HeldByTeam)
{
    /// <summary>Whether the caller may not change <paramref name="roleId"/>.</summary>
    public bool Outlasts(Guid roleId) => HeldPast.Contains(roleId) || InvitedPast.Contains(roleId);
}

/// <summary>
/// Projection of a tenant role including its permission set and member count.
/// </summary>
/// <param name="CanGrant">
/// Whether the caller the role was read for may confer it: what
/// <see cref="ITenantRoleService.ValidateRoleGrantAsync"/> would say for this role alone.
/// </param>
/// <param name="OutlastsCaller">
/// Whether the caller the role was read for has an end date and a member whose access outlasts it
/// holds this role, so the caller may not change or delete it. Always false when read for no caller.
/// </param>
/// <param name="InviteOutlastsCaller">
/// Whether the caller has an end date and a live invite whose access outlasts it gives this role,
/// so the caller may not change or delete it. Always false when read for no caller.
/// </param>
public record TenantRoleDto(
    Guid Id,
    string Name,
    string Slug,
    string? Description,
    List<string> Permissions,
    bool IsSystem,
    int MemberCount,
    DateTime SysCreatedAt,
    bool CanGrant,
    bool OutlastsCaller = false,
    bool InviteOutlastsCaller = false
)
{
    public RoleAccessLevel? AccessLevel => RoleAccessLevels.Of(Permissions);

    /// <summary>
    /// Whether a team's access gives this role, so it may not be deleted or given a permission that
    /// runs the site (<see cref="Scope.IsDataOnly"/>).
    /// </summary>
    public bool HeldByTeam { get; init; }

    /// <summary>
    /// Whether a team's access gives this role and the caller it was read for is not an owner of the
    /// site, so the caller may remove permissions from it but not add any.
    /// </summary>
    public bool OnlyOwnersMayWiden { get; init; }

    /// <summary>
    /// The permissions no one may give this role while a team holds it, which are the
    /// <see cref="Scope.AdministrativePermissions"/>. Empty when no team holds the role.
    /// </summary>
    public IReadOnlyList<string> TeamRefusedPermissions { get; init; } = [];

    /// <summary>
    /// Every permission atom the role's permissions already satisfy (<see cref="Scope.Satisfies(IEnumerable{string}, string)"/>),
    /// which is all the caller may choose when <see cref="OnlyOwnersMayWiden"/> holds. Null otherwise.
    /// </summary>
    public IReadOnlyList<string>? CoveredPermissions { get; init; }

    /// <summary>
    /// Whether the caller the role was read for may give it to a team: they may confer it, and it holds
    /// permissions for data only.
    /// </summary>
    public bool CanGiveToTeam { get; init; }
}

/// <summary>
/// Outcome of creating or renaming a role. <c>Role</c> is set exactly when the save succeeded;
/// otherwise <c>ErrorCode</c> is one of <see cref="NameTaken"/> and <see cref="NameReserved"/>.
/// </summary>
public record SaveRoleResult(TenantRoleDto? Role, string? ErrorCode, string? ErrorDescription)
{
    /// <summary>Another role of the tenant already has this name's slug.</summary>
    public const string NameTaken = "role_name_taken";

    /// <summary>The slug belongs to a seed role, whether or not the tenant still holds it.</summary>
    public const string NameReserved = "role_name_reserved";

    /// <summary>
    /// A team's access gives the role, and the change would give it a permission that runs the site,
    /// or delete it.
    /// </summary>
    public const string RoleHeldByTeam = "role_held_by_team";

    /// <summary>A team's access gives the role, and only an owner of the site may add to it.</summary>
    public const string RoleHeldByTeamOwnerOnly = "role_held_by_team_owner_only";

    public static SaveRoleResult Saved(TenantRoleDto role) => new(role, null, null);

    public static SaveRoleResult Taken { get; } =
        new(null, NameTaken, "A role with this name already exists.");

    public static SaveRoleResult Reserved { get; } =
        new(null, NameReserved, "This name is reserved.");

    public static SaveRoleResult HeldByTeam { get; } =
        new(null, RoleHeldByTeam, "A team has this role, so it can hold only permissions for data.");

    public static SaveRoleResult HeldByTeamOwnerOnly { get; } =
        new(null, RoleHeldByTeamOwnerOnly, "A team has this role, so only an owner can add permissions to it.");
}

/// <summary>
/// Result of a role deletion attempt. Deletion fails if the role is a system role
/// or has members currently assigned.
/// </summary>
public record DeleteRoleResult(bool Success, string? ErrorCode, string? ErrorDescription);
