using Nocturne.Core.Models.Identity;

namespace Nocturne.Core.Contracts.Multitenancy;

/// <summary>
/// Stores who belongs to a tenant and what they may do there: memberships with their roles and
/// direct permissions, the tenant's roles, and the requests to join it. The global role catalogue
/// is on <c>IRoleRepository</c>, since a global role belongs to a subject, not to a membership.
/// </summary>
/// <remarks>
/// <c>tenant_members</c>, <c>tenant_roles</c> and <c>tenant_member_roles</c> carry no tenant policy
/// and no category; the one policy on <c>tenant_members</c> is <c>subject_pin</c>, which narrows it
/// to one subject's rows on a context pinned to a subject. <c>membership_requests</c> is tenant-scoped
/// and reads only through a context that carries its tenant. So every method keyed by a tenant runs
/// pinned to that tenant, which is the same rows as the scope's reader when the scope resolved that
/// tenant, and the tenant's own rows when the caller has none: the authentication hot path, SignalR
/// and OIDC callbacks, and the setup, platform-admin, demo and dev provisioning flows. A method
/// whose remarks name another pin takes that one: the subject pin for one person's memberships
/// across tenants, which no tenant pin can express; the system pin for the two reads a public share
/// host must have answered, which <c>SystemPinnedAllowlistTests</c> limits to their callers; and a
/// platform context for the reads over every tenant's members. Over a caller's unit of work every
/// method runs on the unit's context, whose pin the owner set, and stages its write for the
/// owner's commit.
/// </remarks>
public interface ITenantMembershipRepository
{
    /// <summary>
    /// Stores a membership with its roles in one save; false when the subject is already a member,
    /// including when another request made it one in the meantime.
    /// </summary>
    Task<bool> AddMemberAsync(NewTenantMember member, CancellationToken ct);

    /// <summary>The membership of <paramref name="tenantId"/> held by <paramref name="subjectId"/>, with its roles; null when there is none.</summary>
    Task<TenantMemberRecord?> FindMemberAsync(Guid tenantId, Guid subjectId, CancellationToken ct);

    /// <summary>The membership <paramref name="memberId"/> of <paramref name="tenantId"/>, with its roles; null when there is none.</summary>
    Task<TenantMemberRecord?> GetMemberAsync(Guid tenantId, Guid memberId, CancellationToken ct);

    /// <summary>
    /// The membership of <paramref name="tenantId"/> whose per-tenant username is
    /// <paramref name="username"/>, with its roles; null when there is none.
    /// </summary>
    Task<TenantMemberRecord?> FindMemberByUsernameAsync(Guid tenantId, string username, CancellationToken ct);

    /// <summary>Whether a membership of <paramref name="tenantId"/> has per-tenant username <paramref name="username"/>.</summary>
    Task<bool> UsernameExistsAsync(Guid tenantId, string username, CancellationToken ct);

    /// <summary>The tenant's membership held by the Public system subject, with its roles; null when there is none.</summary>
    Task<TenantMemberRecord?> FindPublicMemberAsync(Guid tenantId, CancellationToken ct);

    /// <summary>
    /// What membership <paramref name="memberId"/> grants, keyed on the membership alone, which is
    /// not an authorization decision: the caller has established that the id belongs to its tenant.
    /// Null when the scope's reader cannot reach it. Read as the scope's reader, having no tenant to pin.
    /// </summary>
    Task<TenantMemberAccess?> GetMemberAccessAsync(Guid memberId, CancellationToken ct);

    /// <summary>What the membership of <paramref name="tenantId"/> held by <paramref name="subjectId"/> grants; null when there is none.</summary>
    Task<TenantMemberAccess?> GetMemberAccessAsync(Guid tenantId, Guid subjectId, CancellationToken ct);

    /// <summary>Whether <paramref name="subjectId"/> holds a membership of <paramref name="tenantId"/>.</summary>
    Task<bool> IsMemberAsync(Guid tenantId, Guid subjectId, CancellationToken ct);

    /// <summary>The names of the tenant roles <paramref name="subjectId"/> holds on <paramref name="tenantId"/>.</summary>
    Task<IReadOnlyList<string>> GetMemberRoleNamesAsync(Guid tenantId, Guid subjectId, CancellationToken ct);

    /// <summary>Every membership of the tenant, with its subject and roles.</summary>
    Task<IReadOnlyList<TenantMemberRecord>> ListMembersAsync(Guid tenantId, CancellationToken ct);

    /// <summary>
    /// The tenant's memberships held by a person who can sign in, not a system subject and not
    /// deactivated, in the order they joined.
    /// </summary>
    Task<IReadOnlyList<TenantMemberRecord>> ListPeopleAsync(Guid tenantId, CancellationToken ct);

    /// <summary>The tenant's owning memberships among <see cref="ListPeopleAsync"/>, longest-standing first.</summary>
    Task<IReadOnlyList<TenantMemberRecord>> ListOwnersAsync(Guid tenantId, CancellationToken ct);

    /// <summary>The subjects of <see cref="ListOwnersAsync"/>, in the same order.</summary>
    Task<IReadOnlyList<Guid>> GetOwnerSubjectIdsAsync(Guid tenantId, CancellationToken ct);

    /// <summary>
    /// The subjects of <see cref="ListOwnersAsync"/>, to be told of an anonymous access request.
    /// </summary>
    /// <remarks>
    /// System-pinned: "Request access" is rendered on a share host's login page too, where the
    /// request reads as a share. The callers are pinned by <c>SystemPinnedAllowlistTests</c>.
    /// </remarks>
    Task<IReadOnlyList<Guid>> GetOwnerSubjectIdsForAccessRequestAsync(Guid tenantId, CancellationToken ct);

    /// <summary>The stored display preferences of the longest-standing owner; null when the tenant has no owner or the owner saved none.</summary>
    Task<string?> GetEarliestOwnerPreferencesAsync(Guid tenantId, CancellationToken ct);

    /// <summary>Whether <paramref name="memberId"/> holds the owner role and no other owner who can sign in would remain without it.</summary>
    Task<bool> IsLastOwnerAsync(Guid tenantId, Guid memberId, CancellationToken ct);

    /// <summary>Whether an owner of <paramref name="tenantId"/> other than <paramref name="memberId"/> can sign in.</summary>
    Task<bool> AnyOtherOwnerAsync(Guid tenantId, Guid memberId, CancellationToken ct);

    /// <summary>Removes the membership and its roles; false when there is no such membership.</summary>
    Task<bool> RemoveMemberAsync(Guid tenantId, Guid memberId, CancellationToken ct);

    /// <summary>Removes every membership <paramref name="subjectId"/> holds, in any tenant, and returns how many.</summary>
    /// <remarks>On a platform context: the demo reset retires its visitor from every tenant it joined.</remarks>
    Task<int> RemoveMembershipsOfSubjectAsync(Guid subjectId, CancellationToken ct);

    /// <summary>Replaces the membership's direct permissions, null clearing them, and stamps it updated at <paramref name="now"/>.</summary>
    Task<bool> SetDirectPermissionsAsync(Guid tenantId, Guid memberId, IReadOnlyList<string>? permissions, DateTime now, CancellationToken ct);

    /// <summary>Sets the membership's 24-hour history limit and stamps it updated at <paramref name="now"/>.</summary>
    Task<bool> SetLimitTo24HoursAsync(Guid tenantId, Guid memberId, bool limitTo24Hours, DateTime now, CancellationToken ct);

    /// <summary>Sets the membership's per-tenant sign-in name.</summary>
    Task<bool> SetUsernameAsync(Guid tenantId, Guid memberId, string? username, CancellationToken ct);

    /// <summary>Records the invite the membership was accepted through.</summary>
    Task<bool> SetCreatedFromInviteAsync(Guid tenantId, Guid memberId, Guid inviteId, CancellationToken ct);

    /// <summary>
    /// Removes the roles <paramref name="removedRoleIds"/> from the membership, assigns
    /// <paramref name="addedRoleIds"/>, and stamps it updated at <paramref name="now"/>, in one save.
    /// </summary>
    Task<bool> ReplaceRolesAsync(
        Guid tenantId, Guid memberId, IReadOnlyCollection<Guid> removedRoleIds, IReadOnlyCollection<Guid> addedRoleIds,
        DateTime now, CancellationToken ct);

    /// <summary>
    /// Stamps the membership as used at <paramref name="at"/> from <paramref name="ipAddress"/> and
    /// <paramref name="userAgent"/>, in one statement, so the write that outlives a request tracks nothing.
    /// </summary>
    Task RecordUseAsync(Guid tenantId, Guid memberId, DateTime at, string? ipAddress, string? userAgent, CancellationToken ct);

    /// <summary>
    /// The subject's platform-admin flag and their membership of <paramref name="tenantId"/>, read
    /// in one statement for the authentication of every request; null when there is no such subject.
    /// </summary>
    Task<SubjectMembershipRead?> ReadSubjectMembershipAsync(Guid subjectId, Guid tenantId, CancellationToken ct);

    /// <summary>The tenants <paramref name="subjectId"/> is a member of.</summary>
    /// <remarks>
    /// Pinned to the subject: the answer spans tenants for one person, which no tenant pin can
    /// express, and the <c>subject_pin</c> policy gives reach over their own rows only.
    /// </remarks>
    Task<IReadOnlyList<Guid>> ListTenantIdsOfSubjectAsync(Guid subjectId, CancellationToken ct);

    /// <summary>
    /// The tenants <paramref name="subjectId"/> is a member of, or with <paramref name="activeOnly"/>
    /// the active ones: those they own first, then by when they joined, then by slug.
    /// </summary>
    /// <remarks>Pinned to the subject, as <see cref="ListTenantIdsOfSubjectAsync"/> is.</remarks>
    Task<IReadOnlyList<TenantRecord>> ListTenantsOfSubjectAsync(Guid subjectId, bool activeOnly, CancellationToken ct);

    /// <summary>Every membership <paramref name="subjectId"/> holds, with its tenant and roles.</summary>
    /// <remarks>Pinned to the subject, as <see cref="ListTenantIdsOfSubjectAsync"/> is.</remarks>
    Task<IReadOnlyList<SubjectMembership>> ListMembershipsOfSubjectAsync(Guid subjectId, CancellationToken ct);

    /// <summary>
    /// Whether any member of <paramref name="tenantId"/> holds a passkey or a working OIDC identity,
    /// and whether any member can sign in with nothing, in one statement; null when the tenant has
    /// no member, so no member can hold a credential.
    /// </summary>
    /// <remarks>
    /// System-pinned: the setup gate runs on every API request, public-share requests included,
    /// which read as a share and are hidden every table no category governs. The callers are
    /// pinned by <c>SystemPinnedAllowlistTests</c>.
    /// </remarks>
    Task<SetupGate?> ReadSetupGateAsync(Guid tenantId, CancellationToken ct);

    /// <summary>How many members, passkeys and working OIDC identities the tenant has, for the log line of a refused setup gate.</summary>
    /// <remarks>System-pinned, as <see cref="ReadSetupGateAsync"/> is.</remarks>
    Task<SetupCredentialCounts> CountSetupCredentialsAsync(Guid tenantId, CancellationToken ct);

    /// <summary>The members of <paramref name="tenantId"/> who can sign in with nothing: active, not a system subject, not the demo visitor.</summary>
    /// <remarks>System-pinned, as <see cref="ReadSetupGateAsync"/> is.</remarks>
    Task<IReadOnlyList<OrphanedMember>> ListOrphanedMembersAsync(Guid tenantId, CancellationToken ct);

    /// <summary>Each active platform admin in the active tenant they joined first, with how many there are in all.</summary>
    /// <remarks>On a platform context: the reader is a hosted service telling every admin at once.</remarks>
    Task<PlatformAdminDirectory> ListPlatformAdminsAsync(CancellationToken ct);

    /// <summary>The tenant's roles with how many members hold each.</summary>
    Task<IReadOnlyList<TenantRoleRecord>> ListRolesAsync(Guid tenantId, CancellationToken ct);

    /// <summary>The role <paramref name="roleId"/> of <paramref name="tenantId"/>; null when there is none. A role id alone is not an authorization decision.</summary>
    Task<TenantRoleRecord?> GetRoleAsync(Guid tenantId, Guid roleId, CancellationToken ct);

    /// <summary>The id of the tenant's role with <paramref name="slug"/>; null when there is none.</summary>
    Task<Guid?> FindRoleIdBySlugAsync(Guid tenantId, string slug, CancellationToken ct);

    /// <summary>
    /// The permission list of each of <paramref name="roleIds"/> that belongs to the tenant, one
    /// entry per matching role, so a caller can tell a foreign id from the count.
    /// </summary>
    Task<IReadOnlyList<IReadOnlyList<string>>> ListRolePermissionsAsync(Guid tenantId, IReadOnlyCollection<Guid> roleIds, CancellationToken ct);

    /// <summary>Stores a role and returns it.</summary>
    Task<TenantRoleRecord> CreateRoleAsync(NewTenantRole role, CancellationToken ct);

    /// <summary>Applies <paramref name="edit"/> to the role and stamps it updated at <paramref name="now"/>; null when there is no such role.</summary>
    Task<TenantRoleRecord?> UpdateRoleAsync(Guid tenantId, Guid roleId, TenantRoleEdit edit, DateTime now, CancellationToken ct);

    /// <summary>Deletes the role together with every member's assignment of it, in one save; false when there is no such role.</summary>
    Task<bool> DeleteRoleAsync(Guid tenantId, Guid roleId, CancellationToken ct);

    /// <summary>Stores the tenant's seed roles whose slugs it does not have yet.</summary>
    Task SeedRolesAsync(Guid tenantId, CancellationToken ct);

    /// <summary>Stores a request to join and returns its id.</summary>
    Task<Guid> CreateRequestAsync(NewMembershipRequest request, CancellationToken ct);

    /// <summary>Whether <paramref name="subjectId"/> has a pending request to join <paramref name="tenantId"/>.</summary>
    Task<bool> AnyPendingRequestAsync(Guid tenantId, Guid subjectId, CancellationToken ct);

    /// <summary>The newest request <paramref name="subjectId"/> made to join <paramref name="tenantId"/>; null when there is none.</summary>
    Task<MembershipRequestRecord?> FindLatestRequestAsync(Guid tenantId, Guid subjectId, CancellationToken ct);

    /// <summary>The tenant's pending requests, newest first.</summary>
    Task<IReadOnlyList<MembershipRequestRecord>> ListPendingRequestsAsync(Guid tenantId, CancellationToken ct);

    /// <summary>The request <paramref name="requestId"/> of <paramref name="tenantId"/>; null when there is none.</summary>
    Task<MembershipRequestRecord?> GetRequestAsync(Guid tenantId, Guid requestId, CancellationToken ct);

    /// <summary>Writes <paramref name="decision"/> onto the request; false when there is no such request.</summary>
    Task<bool> DecideRequestAsync(Guid tenantId, Guid requestId, MembershipRequestDecision decision, CancellationToken ct);
}

/// <summary>A membership as the authentication of a request reads it.</summary>
/// <param name="Id">The membership row.</param>
/// <param name="Access">What the membership grants on its tenant.</param>
/// <param name="LastUsedAt">When the membership was last stamped as used.</param>
public sealed record TenantMembershipFacts(Guid Id, TenantMemberAccess Access, DateTime? LastUsedAt);

/// <summary>A subject's platform-admin flag with their membership of the resolved tenant, or null when they hold none.</summary>
public sealed record SubjectMembershipRead(bool IsPlatformAdmin, TenantMembershipFacts? Membership);
