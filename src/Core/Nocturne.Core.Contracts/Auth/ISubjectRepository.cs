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
/// flow's tenant pin, and the system pin for the reads a public share host must answer, which
/// <c>SystemPinnedAllowlistTests</c> limits to their callers.
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
    /// The subject holding a membership of <paramref name="tenantId"/> in force at
    /// <paramref name="now"/>, recomputed first when a reason of it has lapsed, whose username is <paramref name="username"/>, with its roles; with
    /// <paramref name="activeOnly"/>, only an active one. Null when no member matches.
    /// </summary>
    Task<SubjectRecord?> FindMemberByUsernameAsync(Guid tenantId, string username, bool activeOnly, DateTime now, CancellationToken ct);

    /// <summary>
    /// The active members of <paramref name="tenantId"/> whose membership is in force at
    /// <paramref name="now"/>, recomputed first when a reason of it has lapsed, neither system nor demo subjects, whose confirmed address is
    /// <paramref name="email"/> by <see cref="EmailAddresses.AreSame"/>, roles not read. One address
    /// can sit on several accounts, so there may be more than one.
    /// </summary>
    Task<IReadOnlyList<SubjectRecord>> FindMembersByConfirmedEmailAsync(Guid tenantId, string email, DateTime now, CancellationToken ct);

    /// <summary>
    /// The subject the first-run setup owner step recorded for <paramref name="tenantId"/>
    /// (<see cref="CreateSetupOwnerAsync"/>), roles not read, while it is still safe to hand to
    /// whoever runs setup; otherwise null, and the step creates and records a new one.
    /// </summary>
    /// <remarks>
    /// Pinned to <paramref name="tenantId"/>: setup runs on the apex host, where the scope has no
    /// tenant, and the flow pins the one it is setting up, so the login codes read are that
    /// tenant's.
    /// <para>
    /// Setup resumes the subject it recorded for this tenant and no other. The record is set in
    /// the save that creates the subject and ended by the first credential store, which claims the
    /// setup (<see cref="AddSetupOwnerOidcIdentityAsync"/>,
    /// <see cref="IPasskeyCredentialRepository.CreateForSetupOwnerAsync"/>,
    /// <see cref="AdoptForSetupAsync"/>), or when the subject is deleted.
    /// </para>
    /// <para>
    /// The recorded subject is then checked, because setup is first-come and the subject is about
    /// to pass to whoever runs it. It is refused unless it is active, neither a system subject nor
    /// the demo visitor, and holds none of these, which the step never writes: a passkey, a
    /// provider link (to any provider, enabled or not, since a provider enabled again signs that
    /// identity in again), an authenticator, an unexpired session, an unspent and unexpired
    /// recovery code from an administrator or the operator, an emailed recovery link, a recovery
    /// session or a login code handed to another browser, or a confirmed address while a recovery
    /// link can be delivered (<see cref="IAccountLinkRoute"/>). The platform-admin flag is not
    /// refused: a subject granted it by hand, or one that lost its factors after the startup
    /// bootstrap (<see cref="GrantPlatformAdminAsync"/>) granted it, can hold it.
    /// </para>
    /// </remarks>
    Task<SubjectRecord?> FindSetupOwnerCandidateAsync(Guid tenantId, CancellationToken ct);

    /// <summary>
    /// Stores a subject as <see cref="CreateAsync"/> does and records it as the subject the setup
    /// owner step created for <paramref name="tenantId"/> (<see cref="FindSetupOwnerCandidateAsync"/>),
    /// in one save, so no subject the step creates is left unrecorded.
    /// </summary>
    Task<Guid> CreateSetupOwnerAsync(Guid tenantId, NewSubject subject, CancellationToken ct);

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

    /// <summary>
    /// The pending access requests, newest first, roles not read. A system subject is never one, and
    /// neither is a pending subject with no passkey: its enrolment never registered one, so approving
    /// it would leave an account no one can sign in to.
    /// </summary>
    Task<IReadOnlyList<SubjectRecord>> ListPendingAccessRequestsAsync(CancellationToken ct);

    /// <summary>
    /// Whether a pending access request of the kind <see cref="ListPendingAccessRequestsAsync"/>
    /// lists is under <paramref name="name"/> and has a pending request to join
    /// <paramref name="tenantId"/>. A request filed on another tenant does not count.
    /// </summary>
    /// <remarks>
    /// System-pinned: a public-share host's visitor gets the answer a member's host gives, though a
    /// share is hidden the tenant's membership requests, which are tenant-scoped. The caller is
    /// pinned by <c>SystemPinnedAllowlistTests</c>.
    /// </remarks>
    Task<bool> AnyPendingAccessRequestNamedAsync(Guid tenantId, string name, CancellationToken ct);

    /// <summary>
    /// Whether <paramref name="subjectId"/> is still what an anonymous enrolment's options step left
    /// for <paramref name="match"/>: of the kind and under the name the match names, not a system
    /// subject, and holding no way to sign in: no passkey, no identity-provider link, no
    /// authenticator and no unexpired session. The complete step gives the subject a passkey, and an
    /// invite enrolment signs it in without asking for a code, so a subject holding any of these is
    /// an account, not an enrolment. Any provider link counts, not only one to an enabled provider,
    /// for the reason <see cref="FindSetupOwnerCandidateAsync"/> gives. Membership is not consulted
    /// here; the caller asks it under that subject's own pin.
    /// </summary>
    Task<bool> IsEnrollingSubjectAsync(Guid subjectId, EnrolmentMatch match, CancellationToken ct);

    /// <summary>
    /// Applies <paramref name="edit"/> to the subject and stamps it updated. A null member of the
    /// edit leaves its field as it is.
    /// </summary>
    /// <remarks>
    /// Deactivating an active subject is refused with <see cref="SubjectChange.LastOwnerOf"/> while it
    /// is the last owner of any tenant by <c>TenantOwnerFilter.IsLastOwnerAsync</c>, since a deactivated owner no longer counts.
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

    /// <summary>
    /// Confirms the subject's address at <paramref name="now"/> when <paramref name="email"/> is the
    /// address it holds, compared trimmed and without regard to case. The address the proof was for
    /// is named, so a proof for an address the account has since left confirms nothing, including
    /// when the change commits while this call runs: it is one statement conditional on the stored
    /// address. An address already confirmed keeps its first time. Besides a provider's verified
    /// assertion at creation or sign-in (<see cref="NewSubject.EmailConfirmedAt"/>,
    /// <see cref="SubjectEdit.EmailConfirmedAt"/>), this is the only way an address becomes confirmed.
    /// </summary>
    Task<EmailConfirmation> ConfirmEmailAsync(Guid subjectId, string email, DateTime now, CancellationToken ct);

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
    /// of any tenant by <c>TenantOwnerFilter.IsLastOwnerAsync</c>, whether or not the tenant has other members: the cascade would remove
    /// its membership. An inactive subject is never refused, since it counts as no owner. The check
    /// holds the owner lock of each tenant the subject belongs to, in ascending tenant id, until the
    /// delete commits.
    /// <para>
    /// A team the subject is the only member of is deleted with them. A team with other members is
    /// refused with <see cref="SubjectChange.LastManagerOf"/> while the subject is its last manager by
    /// <c>TeamManagerFilter.IsLastManagerAsync</c>. Both hold the manager lock of each team the subject
    /// belongs to, in ascending team id, until the delete commits.
    /// </para>
    /// <para>
    /// A delete answers <see cref="SubjectChange.Changed.RemovedNotices"/> with the notices it removed
    /// from other people.
    /// </para>
    /// </remarks>
    Task<SubjectChange> DeleteAsync(Guid subjectId, CancellationToken ct);

    /// <summary>Whether the subject is a demo tenant's visitor; null when there is no such subject.</summary>
    /// <remarks>
    /// On a platform context: the demo refusal guards the tenantless, subject-scoped endpoints,
    /// where no tenant is resolved, and the demo tenant's reset runs with none.
    /// </remarks>
    Task<bool?> IsDemoSubjectAsync(Guid subjectId, CancellationToken ct);

    /// <summary>
    /// Whether the subject holds platform admin and is active; false when there is no such subject.
    /// Read as the scope's reader, unlike <see cref="GetSignInStateAsync"/>.
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
    /// Whether platform admins exist and every one of them is stranded, as
    /// <see cref="ReplaceStrandedPlatformAdminsAsync"/> decides it.
    /// </summary>
    /// <remarks>On a platform context, as <see cref="AnyPlatformAdminAsync"/> is.</remarks>
    Task<bool> EveryPlatformAdminIsStrandedAsync(CancellationToken ct);

    /// <summary>
    /// When platform admins exist and every one of them is stranded, clears the flag from them and
    /// grants it to <paramref name="successorId"/>, in one save. Returns the subjects it cleared;
    /// empty when it changed nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// On a platform context, as <see cref="AnyPlatformAdminAsync"/> is. Nothing changes when no
    /// admin exists, when any admin is not stranded, or when the successor cannot sign in, so the
    /// instance is never left without an admin who can. Only stranded admins are read for the
    /// clear, so an admin who can sign in is never cleared, even one granted while this runs.
    /// </para>
    /// <para>
    /// The flag moves on lasting state only. An admin is stranded when it cannot sign in by the
    /// rule every caller shares (<c>OrphanedSubjectFilter</c>) with every provider identity counted,
    /// so a provider that is off, or whose issuer changed, does not strand its admins; and when it
    /// holds no unredeemed, unexpired recovery code or recovery session on any tenant it belongs
    /// to. A deactivated admin is stranded. The successor must be able to sign in now, so only
    /// working identities count for it.
    /// </para>
    /// </remarks>
    Task<IReadOnlyList<Guid>> ReplaceStrandedPlatformAdminsAsync(Guid successorId, CancellationToken ct);

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
    /// Links the first-run setup owner's provider identity as the first credential store, which
    /// claims the setup: only while <paramref name="tenantId"/>'s setup owner record
    /// (<see cref="FindSetupOwnerCandidateAsync"/>) names the identity's subject, ending the record in
    /// the same transaction. Returns the link's id, or null, with nothing linked, when the record no
    /// longer names that subject because another completion claimed it.
    /// </summary>
    Task<Guid?> AddSetupOwnerOidcIdentityAsync(Guid tenantId, NewSubjectOidcIdentity identity, CancellationToken ct);

    /// <summary>
    /// Makes <paramref name="holderId"/> the subject first-run setup completes, in place of the
    /// subject <paramref name="createdId"/> the owner step created: the created subject's membership,
    /// username, display name and global roles pass to the holder and the created subject is deleted.
    /// The membership passes as a removal (system) and a new membership of the holder (setup) with
    /// the same access, so the audit log names the holder as the owner who set up the site.
    /// False, with nothing written, unless both qualify, checked and written in one serializable
    /// transaction.
    /// </summary>
    /// <remarks>
    /// The caller has just proved <paramref name="identityId"/>, so the holder qualifies only when
    /// nothing else could make someone else the owner: it is active, neither a system nor a demo
    /// subject, not a platform admin, a member of no tenant, holds no passkey and no authenticator,
    /// and <paramref name="identityId"/> is the only provider identity it holds. A session and a last
    /// login are allowed, and the holder's refresh tokens are revoked in the same transaction. The
    /// owner step can resume an earlier subject, so the created subject is deleted only when it holds
    /// what that step writes and is otherwise unclaimed, by the same rule as
    /// <see cref="TakeUnclaimedOidcIdentityAsync"/>: its one membership is of the tenant
    /// <paramref name="tenantSlug"/> names, the tenant this setup run is for, and holds the owner role;
    /// it holds no global role but admin; nothing else names it, and it holds no provider identity.
    /// <para>
    /// The adoption is the first credential store, which claims the setup: it goes ahead only while
    /// the tenant's setup owner record (<see cref="FindSetupOwnerCandidateAsync"/>) names
    /// <paramref name="createdId"/>, and ends the record in its transaction, so it is refused once a
    /// passkey or another provider identity has completed setup, and refuses those after it.
    /// </para>
    /// </remarks>
    Task<bool> AdoptForSetupAsync(Guid createdId, Guid holderId, Guid identityId, string tenantSlug, CancellationToken ct);

    /// <summary>
    /// Links identity <paramref name="identityId"/> to <paramref name="subjectId"/>, with the address
    /// <paramref name="email"/> the provider now asserts, when the subject
    /// holding it is unclaimed, deleting that subject, and returns the new link's id. Null, with
    /// nothing written, when it is not, checked and written in one serializable transaction. The
    /// rows removed are the holder's <c>subjects</c> row and, by cascade, its
    /// <c>subject_oidc_identities</c> and <c>subject_roles</c>; audit rows naming it are kept.
    /// </summary>
    /// <remarks>
    /// Unclaimed: it is active, neither a system nor a demo subject, not a platform admin, has never
    /// signed in (no last login and no refresh token row), holds no other provider identity, and no
    /// row of any other table with a foreign key to <c>subjects</c> names it, so the cascade removes
    /// nothing else. Of the columns that hold a subject id with no foreign key, membership requests,
    /// client devices, device codes, alert invites and alert receipts are checked too; the rest are
    /// written only by a signed-in subject, so the never-signed-in clause is what keeps them empty.
    /// The tenant-scoped tables are read tenant by tenant. A reader whose history is clamped could
    /// not see every row, so for one the answer is always null.
    /// </remarks>
    Task<Guid?> TakeUnclaimedOidcIdentityAsync(Guid identityId, Guid subjectId, string? email, CancellationToken ct);

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
    /// How many subjects holding a working identity at <paramref name="providerId"/>, on any tenant,
    /// would be unable to sign in if that provider went away (<c>OrphanedSubjectFilter</c>).
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

    /// <summary>When any subject was last updated, read as the scope's reader; null when there is none.</summary>
    Task<DateTime?> LastModifiedAsync(CancellationToken ct);
}
