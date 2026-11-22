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
/// <para>
/// Every write of a membership's roles, direct permissions, 24-hour limit or end date that changes them,
/// including removing a membership and deleting a role members hold, also records one
/// <c>auth_audit_log</c> row per role, permission, limit or end date that moved, saved with the write.
/// </para>
/// <para>
/// A membership whose end date has passed is absent to every method that takes a <c>now</c>: those
/// decide what a subject may do on a tenant, which tenants a subject has, or who receives something
/// for a tenant. The management reads (<see cref="FindMemberAsync"/>, <see cref="GetMemberAsync"/>,
/// <see cref="ListMembersAsync"/>), the setup gate and the owner reads still return it.
/// </para>
/// </remarks>
public interface ITenantMembershipRepository
{
    /// <summary>
    /// Stores a membership with its roles in one save; false when the subject is already a member,
    /// including when another request made it one in the meantime. Any other failed save throws.
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

    /// <summary>
    /// What the membership of <paramref name="tenantId"/> held by <paramref name="subjectId"/> grants;
    /// null when there is none or it has ended at <paramref name="now"/>.
    /// </summary>
    Task<TenantMemberAccess?> GetMemberAccessAsync(Guid tenantId, Guid subjectId, DateTime now, CancellationToken ct);

    /// <summary>Whether <paramref name="subjectId"/> holds a membership of <paramref name="tenantId"/> that has not ended at <paramref name="now"/>.</summary>
    Task<bool> IsMemberAsync(Guid tenantId, Guid subjectId, DateTime now, CancellationToken ct);

    /// <summary>
    /// The names of the tenant roles <paramref name="subjectId"/> holds on <paramref name="tenantId"/>;
    /// none when the membership has ended at <paramref name="now"/>.
    /// </summary>
    Task<IReadOnlyList<string>> GetMemberRoleNamesAsync(Guid tenantId, Guid subjectId, DateTime now, CancellationToken ct);

    /// <summary>Every membership of the tenant, with its subject and roles.</summary>
    Task<IReadOnlyList<TenantMemberRecord>> ListMembersAsync(Guid tenantId, CancellationToken ct);

    /// <summary>
    /// The tenant's memberships held by a person, active and not a system subject, in the order they
    /// joined.
    /// </summary>
    Task<IReadOnlyList<TenantMemberRecord>> ListPeopleAsync(Guid tenantId, CancellationToken ct);

    /// <summary><see cref="ListPeopleAsync"/> without the memberships that have ended at <paramref name="now"/>.</summary>
    Task<IReadOnlyList<TenantMemberRecord>> ListLivePeopleAsync(Guid tenantId, DateTime now, CancellationToken ct);

    /// <summary>The tenant's owning memberships among <see cref="ListPeopleAsync"/>, longest-standing first.</summary>
    Task<IReadOnlyList<TenantMemberRecord>> ListOwnersAsync(Guid tenantId, CancellationToken ct);

    /// <summary>The subjects of <see cref="ListOwnersAsync"/>, in the same order.</summary>
    Task<IReadOnlyList<Guid>> GetOwnerSubjectIdsAsync(Guid tenantId, CancellationToken ct);

    /// <summary>The stored display preferences of the longest-standing owner; null when the tenant has no owner or the owner saved none.</summary>
    Task<string?> GetEarliestOwnerPreferencesAsync(Guid tenantId, CancellationToken ct);

    /// <summary>Whether an owner of <paramref name="tenantId"/> other than <paramref name="memberId"/> can sign in.</summary>
    Task<bool> AnyOtherOwnerAsync(Guid tenantId, Guid memberId, CancellationToken ct);

    /// <summary>
    /// Removes the membership and its roles, refused with <see cref="OwnerGuardedChange.LastOwner"/>
    /// when <c>TenantOwnerFilter.IsLastOwnerAsync</c> says it is the last owner. Its removed row records
    /// <paramref name="how"/>.
    /// </summary>
    /// <remarks>
    /// The check and the removal hold the tenant's owner lock. Over a unit, call it inside the
    /// unit's commit, which then holds the lock until it commits the removal.
    /// </remarks>
    Task<OwnerGuardedChange> RemoveMemberUnlessLastOwnerAsync(
        Guid tenantId, Guid memberId, MembershipEndHow how, CancellationToken ct);

    /// <summary>
    /// Removes every membership <paramref name="subjectId"/> holds, in any tenant, and returns how
    /// many, with the tenants whose last owner it was by <c>TenantOwnerFilter.IsLastOwnerAsync</c>.
    /// Each removed row records <see cref="MembershipEndHow.System"/>.
    /// </summary>
    /// <remarks>
    /// On a platform context: the demo reset retires its visitor from every tenant it joined. It does
    /// not refuse a last owner: anyone can get a session as the demo visitor, so a membership it holds
    /// must go whatever role it carries. It holds the owner lock of every tenant the subject belongs
    /// to, in ascending tenant id, until the removal commits, so the tenants it names are the ones
    /// the commit leaves ownerless. Over a unit, call it inside the unit's commit.
    /// </remarks>
    Task<SubjectMembershipsRemoval> RemoveMembershipsOfSubjectAsync(Guid subjectId, CancellationToken ct);

    /// <summary>Replaces the membership's direct permissions, null clearing them, and stamps it updated at <paramref name="now"/>.</summary>
    Task<bool> SetDirectPermissionsAsync(Guid tenantId, Guid memberId, IReadOnlyList<string>? permissions, DateTime now, CancellationToken ct);

    /// <summary>Sets the membership's 24-hour history limit and stamps it updated at <paramref name="now"/>.</summary>
    Task<bool> SetLimitTo24HoursAsync(Guid tenantId, Guid memberId, bool limitTo24Hours, DateTime now, CancellationToken ct);

    /// <summary>
    /// Sets when the membership ends, null clearing it, and stamps it updated at
    /// <paramref name="now"/> when the date moved. Refused with
    /// <see cref="OwnerGuardedChange.OwnerAccessEnds"/> when it would give an owner an end date.
    /// In the same save, an alert channel the member added stops no later than the end, and a
    /// later end does not move a channel's stop back.
    /// </summary>
    /// <remarks>Holds the tenant's owner lock as <see cref="RemoveMemberUnlessLastOwnerAsync"/> does, so it and a role change cannot race.</remarks>
    Task<OwnerGuardedChange> SetAccessEndUnlessOwnerAsync(
        Guid tenantId, Guid memberId, DateTime? accessEndsAt, DateTime now, CancellationToken ct);

    /// <summary>Sets the membership's per-tenant sign-in name.</summary>
    Task<bool> SetUsernameAsync(Guid tenantId, Guid memberId, string? username, CancellationToken ct);

    /// <summary>
    /// Removes the roles <paramref name="removedRoleIds"/> from the membership, assigns
    /// <paramref name="addedRoleIds"/>, and stamps it updated at <paramref name="now"/>, in one save.
    /// </summary>
    /// <remarks>Does not check for the last owner. For a membership a person holds, use <see cref="ReplaceRolesUnlessLastOwnerAsync"/>.</remarks>
    Task<bool> ReplaceRolesAsync(
        Guid tenantId, Guid memberId, IReadOnlyCollection<Guid> removedRoleIds, IReadOnlyCollection<Guid> addedRoleIds,
        DateTime now, CancellationToken ct);

    /// <summary>
    /// <see cref="ReplaceRolesAsync"/>, refused with <see cref="OwnerGuardedChange.LastOwner"/> when
    /// it removes the owner role from a membership <c>TenantOwnerFilter.IsLastOwnerAsync</c> says is the last owner,
    /// and with <see cref="OwnerGuardedChange.OwnerAccessEnds"/> when it gives the owner role to a
    /// membership that has an end date.
    /// </summary>
    /// <remarks>Holds the tenant's owner lock as <see cref="RemoveMemberUnlessLastOwnerAsync"/> does.</remarks>
    Task<OwnerGuardedChange> ReplaceRolesUnlessLastOwnerAsync(
        Guid tenantId, Guid memberId, IReadOnlyCollection<Guid> removedRoleIds, IReadOnlyCollection<Guid> addedRoleIds,
        DateTime now, CancellationToken ct);

    /// <summary>
    /// Stamps the membership as used at <paramref name="at"/> from <paramref name="ipAddress"/> and
    /// <paramref name="userAgent"/>, in one statement, so the write that outlives a request tracks nothing.
    /// It writes only when the stored stamp is due (<see cref="Nocturne.Core.Models.Authorization.LastUsedStamp"/>).
    /// </summary>
    Task RecordUseAsync(Guid tenantId, Guid memberId, DateTime at, string? ipAddress, string? userAgent, CancellationToken ct);

    /// <summary>
    /// The subject's platform-admin standing and active state and their membership of
    /// <paramref name="tenantId"/> unless it has ended at <paramref name="now"/>, read in one statement
    /// for the authentication of every request; null when there is no such subject. An inactive
    /// subject holds no platform-admin standing.
    /// </summary>
    Task<SubjectMembershipRead?> ReadSubjectMembershipAsync(Guid subjectId, Guid tenantId, DateTime now, CancellationToken ct);

    /// <summary>The tenants <paramref name="subjectId"/> is a member of, ended memberships included.</summary>
    /// <remarks>
    /// Its readers refuse something to a subject who belongs to a tenant, so leaving out an ended
    /// membership would widen what they allow.
    /// Pinned to the subject: the answer spans tenants for one person, which no tenant pin can
    /// express, and the <c>subject_pin</c> policy gives reach over their own rows only.
    /// </remarks>
    Task<IReadOnlyList<Guid>> ListTenantIdsOfSubjectAsync(Guid subjectId, CancellationToken ct);

    /// <summary>
    /// The tenants <paramref name="subjectId"/> holds a membership of that has not ended at
    /// <paramref name="now"/>, or with <paramref name="activeOnly"/> the active ones: those they own
    /// first, then by when they joined, then by slug.
    /// </summary>
    /// <remarks>Pinned to the subject, as <see cref="ListTenantIdsOfSubjectAsync"/> is.</remarks>
    Task<IReadOnlyList<TenantRecord>> ListTenantsOfSubjectAsync(Guid subjectId, bool activeOnly, DateTime now, CancellationToken ct);

    /// <summary>Every membership <paramref name="subjectId"/> holds that has not ended at <paramref name="now"/>, with its tenant and roles.</summary>
    /// <remarks>Pinned to the subject, as <see cref="ListTenantIdsOfSubjectAsync"/> is.</remarks>
    Task<IReadOnlyList<SubjectMembership>> ListMembershipsOfSubjectAsync(Guid subjectId, DateTime now, CancellationToken ct);

    /// <summary>
    /// Whether any member of <paramref name="tenantId"/> holds a passkey or a working OIDC identity,
    /// and whether the site is in recovery mode (<see cref="SetupGate"/>); null when the tenant has
    /// no member, so no member can hold a credential.
    /// </summary>
    /// <remarks>
    /// One statement for a site with no stuck member; a second only when one is stuck.
    /// System-pinned: the setup gate runs on every API request, public-share requests included,
    /// which read as a share and are hidden every table no category governs. The callers are
    /// pinned by <c>SystemPinnedAllowlistTests</c>.
    /// </remarks>
    /// <param name="now">
    /// Judges only who can still administer the site for recovery mode: an administrator whose
    /// membership has ended at <paramref name="now"/> cannot sign in. Whether any member holds a
    /// credential reads every membership, ended ones included.
    /// </param>
    Task<SetupGate?> ReadSetupGateAsync(Guid tenantId, DateTime now, CancellationToken ct);

    /// <summary>How many members, passkeys and working OIDC identities the tenant has, for the log line of a refused setup gate.</summary>
    /// <remarks>System-pinned, as <see cref="ReadSetupGateAsync"/> is.</remarks>
    Task<SetupCredentialCounts> CountSetupCredentialsAsync(Guid tenantId, CancellationToken ct);

    /// <summary>The members of <paramref name="tenantId"/> who cannot sign in (<c>OrphanedSubjectFilter</c>).</summary>
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

    /// <summary>
    /// Writes <paramref name="decision"/> onto the request; false when there is no such request or
    /// it is no longer pending. The write is one statement whose predicate is the pending status, so
    /// the row's lock decides between two decisions made at once. On a unit it joins the transaction
    /// only when called inside the work of <c>DbUnitOfWork.CommitAsync</c>.
    /// </summary>
    Task<bool> DecideRequestAsync(Guid tenantId, Guid requestId, MembershipRequestDecision decision, CancellationToken ct);
}

/// <summary>A membership as the authentication of a request reads it.</summary>
/// <param name="Id">The membership row.</param>
/// <param name="Access">What the membership grants on its tenant.</param>
/// <param name="LastUsedAt">When the membership was last stamped as used.</param>
public sealed record TenantMembershipFacts(Guid Id, TenantMemberAccess Access, DateTime? LastUsedAt);

/// <summary>A subject's platform-admin standing, active state and membership of the resolved tenant, as the authentication of a request reads them.</summary>
/// <param name="IsPlatformAdmin">Whether the subject holds platform admin and is active.</param>
/// <param name="Membership">The subject's membership of the tenant, or null when they hold none.</param>
public sealed record SubjectMembershipRead(bool IsPlatformAdmin, bool IsActive, TenantMembershipFacts? Membership);
