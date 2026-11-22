using System.Text.Json.Serialization;
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
    bool HasConfirmedEmail,
    IReadOnlyList<TenantMemberRoleRecord> Roles,
    IReadOnlyList<string>? DirectPermissions,
    string? Label,
    string? Username,
    bool LimitTo24Hours,
    DateTime? AccessEndsAt,
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
/// <param name="MadeBy">Who the membership's audit rows name as the actor.</param>
/// <param name="AccessEndsAt">When the membership ends, in UTC; null when it does not.</param>
public sealed record NewTenantMember(
    Guid TenantId,
    Guid SubjectId,
    IReadOnlyList<Guid> RoleIds,
    IReadOnlyList<string>? DirectPermissions = null,
    string? Label = null,
    string? Username = null,
    bool LimitTo24Hours = false,
    Guid? CreatedFromInviteId = null,
    MembershipMadeBy MadeBy = MembershipMadeBy.Caller,
    DateTime? AccessEndsAt = null)
{
    /// <summary>
    /// How the subject came to be a member, as its joined row records it. Required, so a caller cannot
    /// record a join it did not make.
    /// </summary>
    public required MembershipJoinHow How { get; init; }

    /// <summary>The subject who let them in: the invite's maker, or the approver of a request; null otherwise.</summary>
    public required Guid? By { get; init; }
}

/// <summary>How a subject became a member of a tenant.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<MembershipJoinHow>))]
public enum MembershipJoinHow
{
    /// <summary>They accepted an invite.</summary>
    Invite,

    /// <summary>Their membership request or access request was approved.</summary>
    Request,

    /// <summary>A manager or a platform administrator added them, with no invite or request.</summary>
    Direct,

    /// <summary>They are the owner the tenant was created or set up with.</summary>
    Setup,

    /// <summary>The platform added them: the Public and device subjects, the demo member, a dev restore.</summary>
    System,
}

/// <summary>How a subject stopped being a member of a tenant.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<MembershipEndHow>))]
public enum MembershipEndHow
{
    /// <summary>Someone else removed them.</summary>
    Removed,

    /// <summary>They removed themselves.</summary>
    Left,

    /// <summary>Their account was deleted.</summary>
    AccountDeleted,

    /// <summary>The membership had ended and was replaced when they joined again.</summary>
    Replaced,

    /// <summary>
    /// The platform removed them: the demo reset, a dev snapshot import, or setup adoption passing their
    /// membership to another subject.
    /// </summary>
    System,
}

/// <summary>Who made a membership, as its audit rows record it.</summary>
public enum MembershipMadeBy
{
    /// <summary>The scope's audit actor.</summary>
    Caller,

    /// <summary>
    /// The member's own subject, for a membership made before the person has a session: the invitee
    /// accepting an invite, the owner a first-run setup creates, or the owner redeeming a
    /// tenant-creation invite.
    /// </summary>
    Member,

    /// <summary>The platform, for a membership every tenant is given when it is created.</summary>
    System,
}

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

/// <summary>What a role edit or deletion found.</summary>
public enum RoleChangeOutcome
{
    Changed,
    NotFound,

    /// <summary>A team's access gives the role, which may then be neither deleted nor given a permission that runs the site.</summary>
    HeldByTeam,

    /// <summary>A team's access gives the role, and only an owner of the site may widen it.</summary>
    HeldByTeamOwnerOnly,
}

/// <summary>What a role edit found, and the role after it when it changed.</summary>
public sealed record TenantRoleUpdate(RoleChangeOutcome Outcome, TenantRoleRecord? Role);

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
public sealed record MembershipRequestDecision(string Status, Guid? DecidedBySubjectId, DateTime DecidedAt, IReadOnlyList<Guid>? RoleIds);

/// <summary>One of a subject's memberships with the tenant it is of.</summary>
public sealed record SubjectMembership(TenantRecord Tenant, TenantMemberRecord Membership);

/// <summary>
/// What the setup gate decides from. Null is read when the tenant has no member.
/// </summary>
/// <param name="HasCredentials">Whether any member holds a passkey or a working OIDC identity.
/// Until one does the site is in first-run setup.</param>
/// <param name="RecoveryMode">Whether the site is in recovery mode, as
/// <c>ITenantMembershipRepository.ReadSetupGateAsync</c> decides it. A site with a stuck member that
/// is not in recovery mode keeps serving; whether a given administrator may recover a given member
/// is decided when they try.</param>
public sealed record SetupGate(bool HasCredentials, bool RecoveryMode);

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

/// <summary>
/// What removing every membership of a subject did: how many it removed, and the tenants whose
/// last owner it was by <c>TenantOwnerFilter.IsLastOwnerAsync</c>.
/// </summary>
public sealed record SubjectMembershipsRemoval(int Removed, IReadOnlyList<LastOwnerTenant> TenantsLeftWithoutOwner);

/// <summary>What a membership change that may not leave the tenant without an owner found.</summary>
public enum OwnerGuardedChange
{
    Changed,
    NotFound,
    LastOwner,

    /// <summary>
    /// The change would leave an owner's membership with an end date: an end date set on an owner,
    /// or the owner role given to a membership that has one.
    /// </summary>
    OwnerAccessEnds,
}
