using Nocturne.Core.Models.Authorization;

namespace Nocturne.Core.Models.Identity;

/// <summary>
/// A row of <c>tenant_members</c> with the subject it belongs to and the tenant roles it holds.
/// </summary>
public sealed record TenantMemberRecord(
    Guid Id,
    Guid TenantId,
    Guid SubjectId,
    string? SubjectName,
    string? SubjectUsername,
    bool SubjectIsActive,
    bool IsSystemSubject,
    bool IsDemoSubject,
    bool IsPlatformAdmin,
    IReadOnlyList<TenantMemberRoleRecord> Roles,
    IReadOnlyList<string>? DirectPermissions,
    string? Label,
    string? Username,
    bool LimitTo24Hours,
    DateTime? LastUsedAt,
    Guid? CreatedFromInviteId,
    DateTime SysCreatedAt)
{
    /// <summary>The union of the roles' permissions and the direct permissions, leaving out <paramref name="excludingRoleId"/>.</summary>
    public IEnumerable<string> EffectivePermissions(Guid? excludingRoleId = null) =>
        MemberPermissions.Effective(
            Roles.Where(r => r.RoleId != excludingRoleId).Select(r => r.Permissions),
            DirectPermissions);

    /// <summary>Whether the membership's effective permissions satisfy <paramref name="required"/>.</summary>
    public bool Holds(string required) =>
        Scope.Satisfies(Scope.NormalizeMemberPermissions(EffectivePermissions()), required);

    /// <summary>Whether one of the roles is the tenant's owner role.</summary>
    public bool IsOwner => Roles.Any(r => r.Slug == RoleSeeds.Owner);
}

/// <summary>A tenant role as held by a member.</summary>
public sealed record TenantMemberRoleRecord(Guid RoleId, string Name, string Slug, IReadOnlyList<string> Permissions);

/// <summary>A membership to store, with the roles it is assigned at once.</summary>
public sealed record NewTenantMember(
    Guid TenantId,
    Guid SubjectId,
    IReadOnlyList<Guid> RoleIds,
    IReadOnlyList<string>? DirectPermissions = null,
    string? Label = null,
    string? Username = null,
    bool LimitTo24Hours = false);

/// <summary>A row of <c>tenant_roles</c>, with how many members hold it.</summary>
public sealed record TenantRoleRecord(
    Guid Id,
    Guid TenantId,
    string Name,
    string Slug,
    string? Description,
    IReadOnlyList<string> Permissions,
    bool IsSystem,
    int MemberCount,
    DateTime SysCreatedAt);

public sealed record NewTenantRole(
    Guid TenantId,
    string Name,
    string Slug,
    string? Description,
    IReadOnlyList<string> Permissions,
    bool IsSystem);

public sealed record TenantRoleEdit(string Name, string Slug, string? Description, IReadOnlyList<string> Permissions);

/// <summary>A row of <c>membership_requests</c>.</summary>
public sealed record MembershipRequestRecord(
    Guid Id,
    Guid TenantId,
    Guid SubjectId,
    string? Message,
    string Status,
    Guid? DecidedBySubjectId,
    DateTime? DecidedAt,
    IReadOnlyList<Guid>? RoleIds,
    DateTime CreatedAt)
{
    public const string Pending = "pending";
    public const string Approved = "approved";
    public const string Denied = "denied";
}

public sealed record NewMembershipRequest(Guid TenantId, Guid SubjectId, string? Message, DateTime CreatedAt);

/// <summary>The outcome written onto a pending request.</summary>
public sealed record MembershipRequestDecision(string Status, Guid DecidedBySubjectId, DateTime DecidedAt, IReadOnlyList<Guid>? RoleIds);

/// <summary>One of a subject's memberships with the tenant it is of.</summary>
public sealed record SubjectMembership(TenantRecord Tenant, TenantMemberRecord Membership);

/// <summary>
/// What the setup gate decides from: whether any member holds a credential, and whether any
/// member can sign in with nothing at all. Null is read when the tenant has no member.
/// </summary>
public sealed record SetupGate(bool HasCredentials, bool HasOrphans);

/// <summary>The counts a refused setup gate logs.</summary>
public sealed record SetupCredentialCounts(int Members, int Passkeys, int OidcIdentities);

/// <summary>A member who holds no passkey and no working OIDC identity.</summary>
public sealed record OrphanedMember(Guid SubjectId, string Name, string? Username);

/// <summary>A platform admin's membership, in the tenant it belongs to.</summary>
public sealed record PlatformAdminMembership(Guid SubjectId, Guid TenantId, string Slug, string DisplayName);

/// <summary>
/// Each platform admin once, in the active tenant they joined first, beside how many active
/// platform admins there are, so the caller can tell how many have no tenant to be told in.
/// </summary>
public sealed record PlatformAdminDirectory(IReadOnlyList<PlatformAdminMembership> Memberships, int ActivePlatformAdmins);
