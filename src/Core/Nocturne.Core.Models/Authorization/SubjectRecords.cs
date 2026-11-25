namespace Nocturne.Core.Models.Authorization;

/// <summary>A subject to store.</summary>
/// <param name="ApprovalStatus">An <see cref="Authorization.ApprovalStatus"/> member name.</param>
/// <param name="Id">The id to store under; null lets the store choose one.</param>
/// <param name="RoleNames">
/// Global roles to assign on creation, by name. A name no role carries is skipped; duplicates,
/// in any case, assign once.
/// </param>
/// <param name="OidcIdentity">
/// The provider identity the subject is created from, linked in the same save as the subject, so
/// two first sign-ins racing on one identity cannot leave a subject with no way in.
/// </param>
/// <param name="EmailConfirmedAt">
/// When the provider asserted <paramref name="Email"/> as verified, for a subject created from
/// that assertion. Ignored without an email.
/// </param>
public sealed record NewSubject(
    string Name,
    string? Username = null,
    string? Email = null,
    string? Notes = null,
    bool IsActive = true,
    bool IsSystemSubject = false,
    bool IsDemoSubject = false,
    string ApprovalStatus = "Approved",
    string? AccessRequestMessage = null,
    Guid? Id = null,
    IReadOnlyList<string>? RoleNames = null,
    NewOidcLink? OidcIdentity = null,
    DateTime? EmailConfirmedAt = null);

/// <summary>An OIDC identity to link to the subject it is stored with.</summary>
public sealed record NewOidcLink(
    Guid ProviderId,
    string OidcSubjectId,
    string Issuer,
    string? Email,
    DateTime LinkedAt);

/// <summary>A stored subject.</summary>
/// <param name="Roles">
/// The subject's global roles. Read with the subject by the single-subject reads
/// (<c>GetAsync</c>, <c>FindSystemSubjectAsync</c>, <c>FindMemberByUsernameAsync</c>,
/// <c>FindByOidcIdentityAsync</c>); a list read and a passkey's holder leave it empty.
/// </param>
public sealed record SubjectRecord(
    Guid Id,
    string Name,
    string? Username,
    string? Email,
    DateTime? EmailConfirmedAt,
    string? Notes,
    bool IsActive,
    bool IsSystemSubject,
    bool IsDemoSubject,
    bool IsPlatformAdmin,
    string ApprovalStatus,
    string? AccessRequestMessage,
    string? PreferredLanguage,
    string? Preferences,
    string? AvatarUrl,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateTime? LastLoginAt,
    IReadOnlyList<Role> Roles);

/// <summary>
/// What a subject edit changes; a null member leaves the stored value as it is. A field that can
/// be stored as null (email, notes, the access-request message) is set through
/// <see cref="SubjectProfile"/> or its own method instead.
/// </summary>
/// <param name="EmailConfirmedAt">
/// Confirms <paramref name="Email"/> of this edit at that time, in the same save that stores it, so
/// the address and its confirmation are written as a pair. Without an <paramref name="Email"/> it
/// confirms nothing.
/// </param>
public sealed record SubjectEdit(
    string? Name = null,
    string? Username = null,
    string? Email = null,
    bool? IsActive = null,
    bool? IsPlatformAdmin = null,
    string? ApprovalStatus = null,
    string? PreferredLanguage = null,
    string? Preferences = null,
    DateTime? LastLoginAt = null,
    DateTime? EmailConfirmedAt = null)
{
    /// <summary>Grants a subject that asked for access: it becomes active and can sign in.</summary>
    public static readonly SubjectEdit AccessApproved =
        new(IsActive: true, ApprovalStatus: nameof(Authorization.ApprovalStatus.Approved));

    /// <summary>Refuses a subject that asked for access. It stays inactive.</summary>
    public static readonly SubjectEdit AccessDenied =
        new(ApprovalStatus: nameof(Authorization.ApprovalStatus.Denied));
}

/// <summary>The profile fields an administrator's edit replaces together, null members included.</summary>
public sealed record SubjectProfile(string Name, string? Email, string? Notes);

/// <summary>What confirming a subject's email address found.</summary>
public abstract record EmailConfirmation
{
    private EmailConfirmation() { }

    /// <summary>The address is confirmed, since <paramref name="At"/>.</summary>
    public sealed record Confirmed(DateTime At) : EmailConfirmation;

    /// <summary>There is no such subject.</summary>
    public sealed record NotFound : EmailConfirmation;

    /// <summary>The address named is not the one the subject holds, or the subject holds none.</summary>
    public sealed record AddressMismatch : EmailConfirmation;
}

/// <summary>A tenant whose last owner a subject is, by <c>TenantOwnerFilter.IsLastOwnerAsync</c>.</summary>
public sealed record LastOwnerTenant(Guid TenantId, string Slug);

/// <summary>A team whose last manager a subject is, by <c>TeamManagerFilter.IsLastManagerAsync</c>.</summary>
public sealed record LastManagerTeam(Guid TeamId, string Name);

/// <summary>
/// What a change to a subject that may not leave a tenant without an owner found. It is refused
/// with <see cref="LastOwnerOf"/> while the subject is the last owner of any tenant, and a deletion
/// with <see cref="LastManagerOf"/> while the subject is the last manager of a team others belong to.
/// </summary>
public abstract record SubjectChange
{
    private SubjectChange() { }

    public sealed record Changed : SubjectChange
    {
        /// <summary>
        /// The notices a deletion removed from people other than the deleted subject, whose open apps
        /// still list them. Empty for any other change.
        /// </summary>
        public IReadOnlyList<RemovedNotice> RemovedNotices { get; init; } = [];
    }

    public sealed record NotFound : SubjectChange;

    public sealed record LastOwnerOf(IReadOnlyList<LastOwnerTenant> OwnedTenants) : SubjectChange;

    public sealed record LastManagerOf(IReadOnlyList<LastManagerTeam> ManagedTeams) : SubjectChange;
}

/// <summary>A notice <paramref name="UserId"/> held on <paramref name="TenantId"/>, which a deletion removed.</summary>
public sealed record RemovedNotice(Guid TenantId, string UserId, InAppNotificationDto Notice);

/// <summary>
/// What deleting a subject found, with the tenants a delete that removes even a last owner's
/// memberships left without an owner who can sign in.
/// </summary>
public sealed record SubjectDeletionOutcome(SubjectChange Change, IReadOnlyList<LastOwnerTenant> TenantsLeftWithoutOwner);

/// <summary>What keeps a subject's credentials valid between requests.</summary>
public sealed record SubjectSignInState(bool IsActive, bool IsPlatformAdmin)
{
    /// <summary>Whether the subject may act as a platform admin now: only an active subject may.</summary>
    public bool IsActivePlatformAdmin => IsActive && IsPlatformAdmin;
}

/// <summary>What assigning a global role to a subject found.</summary>
public enum RoleAssignment
{
    RoleNotFound,
    AlreadyAssigned,
    Assigned,
}

/// <summary>
/// What an anonymous enrolment's options step creates: the kind of subject, under the name the
/// caller typed. The complete step checks the subject its challenge was issued for against it.
/// </summary>
public abstract record EnrolmentMatch
{
    private EnrolmentMatch() { }

    /// <summary>An invite acceptance: an active, approved subject under <paramref name="Username"/>.</summary>
    public sealed record Invite(string Username) : EnrolmentMatch;

    /// <summary>An access request: an inactive, pending subject named <paramref name="DisplayName"/>.</summary>
    public sealed record AccessRequest(string DisplayName) : EnrolmentMatch;
}

/// <summary>A primary sign-in factor of a subject: a passkey or a linked provider identity.</summary>
public abstract record SignInFactor
{
    private SignInFactor() { }

    public sealed record Passkey(Guid CredentialId) : SignInFactor;

    public sealed record OidcIdentity(Guid IdentityId) : SignInFactor;
}

/// <summary>An OIDC identity to link to a subject.</summary>
public sealed record NewSubjectOidcIdentity(
    Guid SubjectId,
    Guid ProviderId,
    string OidcSubjectId,
    string Issuer,
    string? Email,
    DateTime LinkedAt);

/// <summary>An OIDC identity with the subject it links to.</summary>
public sealed record OidcIdentityMatch(SubjectOidcIdentity Identity, SubjectRecord Subject);
