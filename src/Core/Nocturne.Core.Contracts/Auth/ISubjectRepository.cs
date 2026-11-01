using Nocturne.Core.Models.Authorization;

namespace Nocturne.Core.Contracts.Auth;

/// <summary>
/// Stores subjects, the people who sign in, with their global roles, their linked OIDC identities
/// and the questions that span their sign-in factors. Their passkeys, authenticators and recovery
/// codes are on <see cref="IPasskeyCredentialRepository"/>, <see cref="ITotpCredentialRepository"/>
/// and <see cref="IRecoveryCodeRepository"/>.
/// </summary>
/// <remarks>
/// <c>subjects</c> and the tables beside it carry no tenant and no tenant policy; the
/// <c>subject_pin</c> policy narrows them only on a context pinned to a subject. Every method runs
/// as the scope's reader unless its remarks name a pin: a platform context where the caller has no
/// resolved tenant, the subject pin where the caller is the subject's own connection, the setup
/// flow's tenant pin, and the system pin for the two setup-state reads a public share host must
/// answer, which <c>SystemPinnedAllowlistTests</c> limits to their callers.
/// </remarks>
public interface ISubjectRepository
{
    /// <summary>
    /// Stores a subject, with the roles <see cref="NewSubject.RoleNames"/> names and the identity
    /// <see cref="NewSubject.OidcIdentity"/> links, in one save, and returns its id.
    /// </summary>
    Task<Guid> CreateAsync(NewSubject subject, CancellationToken ct);

    /// <summary>The subject with <paramref name="subjectId"/>, with its roles; null when there is none.</summary>
    Task<SubjectRecord?> GetAsync(Guid subjectId, CancellationToken ct);

    /// <summary>Those of <paramref name="subjectIds"/> that exist, roles not read.</summary>
    Task<IReadOnlyList<SubjectRecord>> GetManyAsync(IReadOnlyCollection<Guid> subjectIds, CancellationToken ct);

    /// <summary>The system subject named <paramref name="name"/>, with its roles; null when there is none.</summary>
    Task<SubjectRecord?> FindSystemSubjectAsync(string name, CancellationToken ct);

    /// <summary>
    /// The subject holding a membership of <paramref name="tenantId"/> whose username is
    /// <paramref name="username"/>, with its roles; with <paramref name="activeOnly"/>, only an
    /// active one. Null when no member matches.
    /// </summary>
    Task<SubjectRecord?> FindMemberByUsernameAsync(Guid tenantId, string username, bool activeOnly, CancellationToken ct);

    /// <summary>
    /// The first-run owner candidate: the earliest active subject that is neither a system subject
    /// nor the demo visitor, roles not read. Setup only runs while no member of the tenant holds a
    /// credential, so this is the account the owner options step created or reused, and the
    /// ordering is what lets the options and complete steps resolve the same row.
    /// </summary>
    /// <remarks>
    /// Pinned to <paramref name="tenantId"/>: setup runs on the apex host, where the scope has no
    /// tenant, and the flow pins the one it is setting up. <c>subjects</c> is not tenant-scoped, so
    /// the read sees every subject on the instance, the demo visitor included, which is why the
    /// demo exclusion is part of the predicate.
    /// </remarks>
    Task<SubjectRecord?> FindSetupOwnerCandidateAsync(Guid tenantId, CancellationToken ct);

    /// <summary>
    /// The system subject the scope's tenant issues its API tokens to, or null when it has never
    /// issued one.
    /// </summary>
    /// <remarks>
    /// A device token belongs to the site, not to a person, so the holder is a system subject with a
    /// membership of the tenant that no sign-in reaches. Reading the token list must not write rows
    /// into a tenant that has never had a token; <see cref="EnsureDeviceSubjectAsync"/> is for a
    /// caller about to issue one.
    /// </remarks>
    Task<Guid?> FindDeviceSubjectAsync(CancellationToken ct);

    /// <summary>
    /// The subject <see cref="FindDeviceSubjectAsync"/> names, created with its full-access
    /// membership of the scope's tenant when there is none. Saves on a context of its own, so it
    /// cannot run on a repository over a unit of work.
    /// </summary>
    Task<Guid> EnsureDeviceSubjectAsync(CancellationToken ct);

    /// <summary>The pending access requests, newest first, roles not read. A system subject is never one.</summary>
    Task<IReadOnlyList<SubjectRecord>> ListPendingAccessRequestsAsync(CancellationToken ct);

    /// <summary>
    /// The subjects part-way through an anonymous enrolment that <paramref name="match"/> names:
    /// not a system subject, holding no passkey and no working OIDC identity. Newest first, by id,
    /// which for UUID v7 ids is creation order. Membership is not consulted here; the caller asks
    /// it per candidate under that subject's own pin.
    /// </summary>
    Task<IReadOnlyList<Guid>> FindCredentiallessSubjectIdsAsync(EnrolmentMatch match, CancellationToken ct);

    /// <summary>
    /// Applies <paramref name="edit"/> to the subject and stamps it updated. A null member of the
    /// edit leaves its field as it is.
    /// </summary>
    /// <remarks>
    /// Deactivating an active subject is refused with <see cref="SubjectChange.LastOwnerOf"/> while it
    /// is the last owner who can sign in of any tenant, since a deactivated owner no longer counts.
    /// The check holds the owner lock of each tenant the subject belongs to, in ascending tenant id,
    /// until the change commits. Over a unit, deactivate inside the unit's commit.
    /// </remarks>
    Task<SubjectChange> UpdateAsync(Guid subjectId, SubjectEdit edit, CancellationToken ct);

    /// <summary>
    /// Replaces the subject's profile fields, null members included, and returns it as edited with
    /// its roles; null when there is no such subject. Unlike <see cref="UpdateAsync"/> this can
    /// clear the email and the notes, which an administrator's edit does. It never changes whether
    /// the subject is active.
    /// </summary>
    Task<SubjectRecord?> UpdateProfileAsync(Guid subjectId, SubjectProfile profile, CancellationToken ct);

    /// <summary>Sets, or with null clears, the message a subject submitted with its access request.</summary>
    Task<bool> SetAccessRequestMessageAsync(Guid subjectId, string? message, CancellationToken ct);

    /// <summary>
    /// Deletes the subject, and in the same transaction clears its personal data from the three
    /// audit trails, leaving the ids that make them a pseudonymous record. In <c>auth_audit_log</c>
    /// the details go from every row the subject is party to. In all three, the client address, and
    /// the user agent where the table keeps one, go from every row it acted on, in every tenant.
    /// They are the actor's, so another subject's on a row about this one are kept.
    /// <c>actor_name</c> stays on <c>mutation_audit_log</c> and <c>read_access_log</c>, so a
    /// tenant's owner can still see who changed or read their data. Rows that reference the subject
    /// go with it by cascade.
    /// </summary>
    /// <remarks>
    /// Refused with <see cref="SubjectChange.LastOwnerOf"/> while an active subject is the last owner
    /// who can sign in of any tenant, whether or not the tenant has other members: the cascade would
    /// remove its membership. An inactive subject is never refused, since it counts as no owner. The check holds the owner lock of each tenant the subject belongs to, in
    /// ascending tenant id, until the delete commits.
    /// </remarks>
    Task<SubjectChange> DeleteAsync(Guid subjectId, CancellationToken ct);

    /// <summary>Whether the subject is a demo tenant's visitor; null when there is no such subject.</summary>
    /// <remarks>
    /// On a platform context: the demo refusal guards the tenantless, subject-scoped endpoints,
    /// where no tenant is resolved, and the demo tenant's reset runs with none.
    /// </remarks>
    Task<bool?> IsDemoSubjectAsync(Guid subjectId, CancellationToken ct);

    /// <summary>
    /// Whether the subject holds platform admin; false when there is no such subject. Read as the
    /// scope's reader, unlike <see cref="GetSignInStateAsync"/>.
    /// </summary>
    Task<bool> IsPlatformAdminAsync(Guid subjectId, CancellationToken ct);

    /// <summary>Whether any subject holds platform admin.</summary>
    /// <remarks>
    /// On a platform context: the startup bootstrap that asks has no request scope. The scope's
    /// reader counts with <see cref="CountPlatformAdminsAsync"/>.
    /// </remarks>
    Task<bool> AnyPlatformAdminAsync(CancellationToken ct);

    /// <summary>How many subjects hold platform admin, read as the scope's reader.</summary>
    Task<int> CountPlatformAdminsAsync(CancellationToken ct);

    /// <summary>Grants platform admin to those of <paramref name="subjectIds"/> that exist and returns how many.</summary>
    /// <remarks>On a platform context, as <see cref="AnyPlatformAdminAsync"/> is.</remarks>
    Task<int> GrantPlatformAdminAsync(IReadOnlyCollection<Guid> subjectIds, CancellationToken ct);

    /// <summary>
    /// Whether the subject is active and whether it holds platform admin; null when there is no
    /// such subject.
    /// </summary>
    /// <remarks>
    /// Pinned to <paramref name="subjectId"/>: the hub revalidation proves a connection's
    /// credential as that connection's own subject, so the <c>subject_pin</c> policy admits that
    /// row and no other. <see cref="IsPlatformAdminAsync"/> is the same flag as the scope's reader.
    /// </remarks>
    Task<SubjectSignInState?> GetSignInStateAsync(Guid subjectId, CancellationToken ct);

    /// <summary>The subject's global roles.</summary>
    Task<IReadOnlyList<Role>> GetRolesAsync(Guid subjectId, CancellationToken ct);

    /// <summary>Assigns the global role named <paramref name="roleName"/> to the subject at <paramref name="now"/>.</summary>
    /// <param name="assignedBy">The subject who assigned it; null for a system assignment.</param>
    Task<RoleAssignment> AssignRoleAsync(Guid subjectId, string roleName, Guid? assignedBy, DateTime now, CancellationToken ct);

    /// <summary>Removes the global role named <paramref name="roleName"/> from the subject; false when it was not assigned.</summary>
    Task<bool> RemoveRoleAsync(Guid subjectId, string roleName, CancellationToken ct);

    /// <summary>The subject's linked OIDC identities with their providers, oldest link first.</summary>
    Task<IReadOnlyList<SubjectOidcIdentity>> GetOidcIdentitiesAsync(Guid subjectId, CancellationToken ct);

    /// <summary>
    /// The identity <paramref name="oidcSubjectId"/> at <paramref name="issuer"/> and the subject
    /// it links to, with its roles; null when no such identity is linked.
    /// </summary>
    Task<OidcIdentityMatch?> FindByOidcIdentityAsync(string oidcSubjectId, string issuer, CancellationToken ct);

    /// <summary>Links an OIDC identity to an existing subject and returns its id.</summary>
    Task<Guid> AddOidcIdentityAsync(NewSubjectOidcIdentity identity, CancellationToken ct);

    /// <summary>
    /// Records a sign-in with identity <paramref name="identityId"/> at <paramref name="now"/>, and
    /// when <paramref name="email"/> is given the address the provider now asserts. False when
    /// there is no such identity.
    /// </summary>
    Task<bool> TouchOidcIdentityAsync(Guid identityId, DateTime now, string? email, CancellationToken ct);

    /// <summary>
    /// How many primary sign-in factors the subject holds: passkeys plus OIDC identities whose
    /// provider exists and is enabled.
    /// </summary>
    Task<int> CountPrimaryAuthFactorsAsync(Guid subjectId, CancellationToken ct);

    /// <summary>
    /// Removes <paramref name="factor"/> from the subject unless it is the last one
    /// <see cref="CountPrimaryAuthFactorsAsync"/> would count. The check and the delete run in one
    /// serializable transaction, so two concurrent removals cannot both pass the check.
    /// </summary>
    Task<FactorRemovalResult> TryRemoveFactorAsync(Guid subjectId, SignInFactor factor, CancellationToken ct);

    /// <summary>
    /// How many subjects would be left with no sign-in method if provider
    /// <paramref name="providerId"/> went away: active, non-system, non-demo subjects whose only
    /// working factor is an identity at that provider.
    /// </summary>
    Task<int> CountStrandedByProviderAsync(Guid providerId, CancellationToken ct);

    /// <summary>
    /// Whether any member of <paramref name="tenantId"/> holds a passkey or a working OIDC
    /// identity, i.e. whether the tenant is past first-run setup.
    /// </summary>
    /// <remarks>
    /// System-pinned: the auth status and the setup state are answered on public-share requests,
    /// which read as a share and are hidden every table no category governs. The callers are
    /// pinned by <c>SystemPinnedAllowlistTests</c>.
    /// </remarks>
    Task<bool> TenantHasCredentialedMemberAsync(Guid tenantId, CancellationToken ct);

    /// <summary>
    /// Whether <paramref name="tenantId"/> has an active, non-system, non-demo member with no
    /// passkey and no working OIDC identity: an account that cannot sign in at all.
    /// </summary>
    /// <remarks>System-pinned, as <see cref="TenantHasCredentialedMemberAsync"/> is.</remarks>
    Task<bool> HasOrphanedMemberAsync(Guid tenantId, CancellationToken ct);

    /// <summary>When any subject was last updated, read as the scope's reader; null when there is none.</summary>
    Task<DateTime?> LastModifiedAsync(CancellationToken ct);
}
