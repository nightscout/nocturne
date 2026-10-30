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
/// <c>subject_pin</c> policy narrows them only on a context pinned to a subject. So every method
/// runs as the scope's reader unless its remarks name a pin, and a method that names one
/// preserves the pin its caller held before the read moved here: a platform context for reads
/// made where no tenant is resolved, a subject pin for the hub's revalidation, a system pin for
/// the two setup-state reads a public share host must answer.
/// </remarks>
public interface ISubjectRepository
{
    /// <summary>Stores a subject with the roles <see cref="NewSubject.RoleNames"/> names and returns its id.</summary>
    Task<Guid> CreateAsync(NewSubject subject, CancellationToken ct);

    /// <summary>The subject with <paramref name="subjectId"/>, with its roles; null when there is none.</summary>
    Task<SubjectRecord?> GetAsync(Guid subjectId, CancellationToken ct);

    /// <summary>Those of <paramref name="subjectIds"/> that exist, roles not read.</summary>
    Task<IReadOnlyList<SubjectRecord>> GetManyAsync(IReadOnlyCollection<Guid> subjectIds, CancellationToken ct);

    /// <summary>The display names of those of <paramref name="subjectIds"/> that exist.</summary>
    Task<IReadOnlyDictionary<Guid, string>> GetNamesAsync(IReadOnlyCollection<Guid> subjectIds, CancellationToken ct);

    /// <summary>Whether a subject with <paramref name="subjectId"/> exists.</summary>
    Task<bool> ExistsAsync(Guid subjectId, CancellationToken ct);

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
    /// Pinned to <paramref name="tenantId"/>, as the setup flow's other reads are. <c>subjects</c>
    /// is not tenant-scoped, so the read sees every subject on the instance, the demo visitor
    /// included, which is why the demo exclusion is part of the predicate.
    /// </remarks>
    Task<SubjectRecord?> FindSetupOwnerCandidateAsync(Guid tenantId, CancellationToken ct);

    /// <summary>The pending access requests, newest first, roles not read. A system subject is never one.</summary>
    Task<IReadOnlyList<SubjectRecord>> ListPendingAccessRequestsAsync(CancellationToken ct);

    /// <summary>Whether a pending access request named <paramref name="displayName"/> exists.</summary>
    Task<bool> AnyPendingAccessRequestNamedAsync(string displayName, CancellationToken ct);

    /// <summary>
    /// The subjects part-way through an anonymous enrolment that <paramref name="match"/> names:
    /// not a system subject, holding no passkey and no working OIDC identity. Newest first, by id,
    /// which for UUID v7 ids is creation order. Membership is not consulted here; the caller asks
    /// it per candidate under that subject's own pin.
    /// </summary>
    Task<IReadOnlyList<Guid>> FindCredentiallessSubjectIdsAsync(EnrolmentMatch match, CancellationToken ct);

    /// <summary>Applies <paramref name="edit"/> to the subject; false when there is no such subject.</summary>
    Task<bool> UpdateAsync(Guid subjectId, SubjectEdit edit, CancellationToken ct);

    /// <summary>Replaces the subject's profile fields and returns it as edited, with its roles; null when there is no such subject.</summary>
    Task<SubjectRecord?> UpdateProfileAsync(Guid subjectId, SubjectProfile profile, CancellationToken ct);

    /// <summary>Sets, or with null clears, the message a subject submitted with its access request.</summary>
    Task<bool> SetAccessRequestMessageAsync(Guid subjectId, string? message, CancellationToken ct);

    /// <summary>
    /// Deletes the subject, and in the same transaction clears its personal data from the auth
    /// audit trail, leaving the ids that make it a pseudonymous record. The client address and
    /// user agent are the actor's, so another subject's on a row about this one are kept. Rows
    /// that reference the subject go with it by cascade. False when there is no such subject.
    /// </summary>
    Task<bool> DeleteAsync(Guid subjectId, CancellationToken ct);

    /// <summary>Whether the subject is a demo tenant's visitor; null when there is no such subject.</summary>
    /// <remarks>
    /// On a platform context: the demo refusal runs on the tenantless, subject-scoped endpoints,
    /// where no tenant is resolved, and on the demo tenant's reset.
    /// </remarks>
    Task<bool?> IsDemoSubjectAsync(Guid subjectId, CancellationToken ct);

    /// <summary>Whether the subject holds platform admin; false when there is no such subject.</summary>
    Task<bool> IsPlatformAdminAsync(Guid subjectId, CancellationToken ct);

    /// <summary>Whether any subject holds platform admin.</summary>
    /// <remarks>On a platform context: asked by the startup bootstrap, which has no request scope.</remarks>
    Task<bool> AnyPlatformAdminAsync(CancellationToken ct);

    /// <summary>How many subjects hold platform admin.</summary>
    Task<int> CountPlatformAdminsAsync(CancellationToken ct);

    /// <summary>Grants platform admin to those of <paramref name="subjectIds"/> that exist and returns how many.</summary>
    /// <remarks>On a platform context, as <see cref="AnyPlatformAdminAsync"/> is.</remarks>
    Task<int> GrantPlatformAdminAsync(IReadOnlyCollection<Guid> subjectIds, CancellationToken ct);

    /// <summary>
    /// Whether the subject is active and whether it holds platform admin; null when there is no
    /// such subject.
    /// </summary>
    /// <remarks>
    /// Pinned to <paramref name="subjectId"/>: the hub revalidation reads as the connection's own
    /// subject, so the <c>subject_pin</c> policy admits that row and no other.
    /// </remarks>
    Task<SubjectSignInState?> GetSignInStateAsync(Guid subjectId, CancellationToken ct);

    /// <summary>The names of the subject's global roles.</summary>
    Task<IReadOnlyList<string>> GetRoleNamesAsync(Guid subjectId, CancellationToken ct);

    /// <summary>The subject's global roles.</summary>
    Task<IReadOnlyList<Role>> GetRolesAsync(Guid subjectId, CancellationToken ct);

    /// <summary>Assigns the global role named <paramref name="roleName"/> to the subject at <paramref name="now"/>.</summary>
    /// <param name="assignedBy">The subject who assigned it; null for a system assignment.</param>
    Task<RoleAssignment> AssignRoleAsync(Guid subjectId, string roleName, Guid? assignedBy, DateTime now, CancellationToken ct);

    /// <summary>Removes the global role named <paramref name="roleName"/> from the subject; false when it was not assigned.</summary>
    Task<bool> RemoveRoleAsync(Guid subjectId, string roleName, CancellationToken ct);

    /// <summary>The subject's linked OIDC identities with their providers, oldest link first.</summary>
    Task<IReadOnlyList<SubjectOidcIdentity>> GetOidcIdentitiesAsync(Guid subjectId, CancellationToken ct);

    /// <summary>The OIDC identity the subject most recently signed in with, or linked; null when none is linked.</summary>
    Task<SubjectOidcIdentity?> GetMostRecentlyUsedOidcIdentityAsync(Guid subjectId, CancellationToken ct);

    /// <summary>
    /// The identity <paramref name="oidcSubjectId"/> at <paramref name="issuer"/> and the subject
    /// it links to, with its roles; null when no such identity is linked.
    /// </summary>
    Task<OidcIdentityMatch?> FindByOidcIdentityAsync(string oidcSubjectId, string issuer, CancellationToken ct);

    /// <summary>Links an OIDC identity to a subject and returns its id.</summary>
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
    /// System-pinned: the auth status and setup state are asked on public-share requests, which
    /// read as a share and are hidden every table no category governs.
    /// </remarks>
    Task<bool> TenantHasCredentialedMemberAsync(Guid tenantId, CancellationToken ct);

    /// <summary>
    /// Whether <paramref name="tenantId"/> has an active, non-system, non-demo member with no
    /// passkey and no working OIDC identity: an account that cannot sign in at all.
    /// </summary>
    /// <remarks>System-pinned, as <see cref="TenantHasCredentialedMemberAsync"/> is.</remarks>
    Task<bool> HasOrphanedMemberAsync(Guid tenantId, CancellationToken ct);

    /// <summary>The subject's display preferences; null when there is no such subject.</summary>
    Task<SubjectPreferences?> GetPreferencesAsync(Guid subjectId, CancellationToken ct);
}
