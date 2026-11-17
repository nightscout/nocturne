namespace Nocturne.Core.Models.Authorization;

/// <summary>A subject to store.</summary>
/// <param name="ApprovalStatus">"Approved" or "Pending", as the access-request flow spells them.</param>
/// <param name="Id">The id to store under; null lets the store choose one.</param>
/// <param name="RoleNames">
/// Global roles to assign on creation, by name. A name no role carries is skipped; duplicates,
/// in any case, assign once.
/// </param>
/// <param name="OidcIdentity">
/// The provider identity the subject is created from, linked in the same save as the subject, so
/// two first sign-ins racing on one identity cannot leave a subject with no way in.
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
    NewOidcLink? OidcIdentity = null);

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
public sealed record SubjectEdit(
    string? Name = null,
    string? Username = null,
    string? Email = null,
    bool? IsActive = null,
    bool? IsPlatformAdmin = null,
    string? ApprovalStatus = null,
    string? PreferredLanguage = null,
    string? Preferences = null,
    DateTime? LastLoginAt = null);

/// <summary>The profile fields an administrator's edit replaces together, null members included.</summary>
public sealed record SubjectProfile(string Name, string? Email, string? Notes);

/// <summary>A tenant a change to a subject would leave without an owner who can sign in.</summary>
public sealed record LastOwnerTenant(Guid TenantId, string Slug);

/// <summary>
/// What a change to a subject that may not leave a tenant without an owner found. It is refused
/// with <see cref="LastOwnerOf"/> while the subject is the last owner of any tenant.
/// </summary>
public abstract record SubjectChange
{
    private SubjectChange() { }

    public sealed record Changed : SubjectChange;

    public sealed record NotFound : SubjectChange;

    public sealed record LastOwnerOf(IReadOnlyList<LastOwnerTenant> OwnedTenants) : SubjectChange;
}

/// <summary>What keeps a subject's credentials valid between requests.</summary>
public sealed record SubjectSignInState(bool IsActive, bool IsPlatformAdmin);

/// <summary>What assigning a global role to a subject found.</summary>
public enum RoleAssignment
{
    RoleNotFound,
    AlreadyAssigned,
    Assigned,
}

/// <summary>
/// Names the subject an anonymous enrolment is for. The options and complete steps of a flow
/// share one match, so the subject the options step reuses or creates is the one the complete
/// step re-resolves; the challenge token is not trusted for it, and an invite token is not a key
/// for it because one invite can be accepted by several people.
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
