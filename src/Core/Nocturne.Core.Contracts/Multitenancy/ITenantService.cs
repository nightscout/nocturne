using System.Text.Json.Serialization;
using Nocturne.Core.Models.Identity;
using Nocturne.Core.Models.V4;

namespace Nocturne.Core.Contracts.Multitenancy;

/// <summary>
/// Core service for tenant lifecycle management: provisioning, configuration,
/// and membership administration. Tenants are the top-level isolation
/// boundary enforced via PostgreSQL Row Level Security.
/// </summary>
/// <seealso cref="ITenantAccessor"/>
/// <seealso cref="TenantContext"/>
/// <seealso cref="ITenantMemberService"/>
/// <seealso cref="ITenantRoleService"/>
public interface ITenantService
{
    /// <summary>Creates a new tenant with the specified subject as its owner.</summary>
    Task<TenantCreatedDto> CreateAsync(string slug, string displayName, Guid creatorSubjectId, CancellationToken ct = default);

    /// <summary>
    /// Creates a tenant with <paramref name="ownerSubjectId"/> as its owner, under
    /// <paramref name="ownerUsername"/> when one is given, in one transaction: the tenant is created
    /// with its owner or not at all.
    /// </summary>
    Task<TenantCreatedDto> CreateWithOwnerAsync(
        string slug, string displayName, Guid ownerSubjectId, string? ownerUsername = null, CancellationToken ct = default);

    /// <summary>Creates a new tenant without assigning an owner.</summary>
    Task<TenantCreatedDto> CreateWithoutOwnerAsync(string slug, string displayName, CancellationToken ct = default);

    /// <summary>Re-seeds roles and public membership for an existing tenant after a data purge.</summary>
    Task SeedAfterResetAsync(Guid tenantId, CancellationToken ct = default);

    Task<List<TenantDto>> GetAllAsync(CancellationToken ct = default);

    /// <summary>Returns the tenant with its member list, or null if not found.</summary>
    Task<TenantDetailDto?> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>Updates a tenant's display name, active state, and access-request policy.</summary>
    Task<TenantDto> UpdateAsync(Guid id, string displayName, bool isActive, bool? allowAccessRequests = null, CancellationToken ct = default);

    Task<TenantSettingsDto> GetSettingsAsync(Guid id, CancellationToken ct = default);

    Task<TenantSettingsDto> SetAllowPublicDocsAsync(Guid id, bool allowPublicDocs, CancellationToken ct = default);

    /// <summary>Renames the tenant, leaving its slug and active state as they are.</summary>
    Task<TenantSettingsDto> SetDisplayNameAsync(Guid id, string displayName, CancellationToken ct = default);

    /// <summary>
    /// How long a tenant keeps its subdomain after a change before it may change again. Every
    /// change breaks the uploaders and followers on the old address, so a tenant cannot move
    /// repeatedly while its owner is still updating them.
    /// </summary>
    static readonly TimeSpan SlugChangeInterval = TimeSpan.FromHours(1);

    /// <summary>
    /// Moves the tenant to the subdomain <paramref name="newSlug"/>, after the checks
    /// <see cref="ValidateSlugAsync(string, CancellationToken)"/> makes, at most once per
    /// <see cref="SlugChangeInterval"/>. The old subdomain stops resolving at once: nothing
    /// redirects from it.
    /// </summary>
    /// <returns>The refusal, or a valid result once the tenant has moved.</returns>
    Task<SlugValidationResult> ChangeSlugAsync(Guid id, string newSlug, CancellationToken ct = default);

    Task<PatientRelationship?> GetPatientRelationshipAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Records who the tenant is for, replacing any earlier answer. A caller other than the helper
    /// replacing <see cref="PatientRelationship.Helper"/> is the recipient answering, which marks
    /// the tenant handed over.
    /// </summary>
    Task SetPatientRelationshipAsync(
        Guid id, PatientRelationship relationship, Guid? callerId, CancellationToken ct = default);

    /// <summary>The glucose units the owner chose for the tenant, or null when none was chosen.</summary>
    Task<string?> GetDefaultGlucoseUnitsAsync(Guid id, CancellationToken ct = default);

    /// <summary>Records the owner's choice of tenant glucose units, "mg/dl" or "mmol".</summary>
    Task SetDefaultGlucoseUnitsAsync(Guid id, string units, CancellationToken ct = default);

    /// <summary>Permanently deletes a tenant and all associated data.</summary>
    Task DeleteAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Adds <paramref name="subjectId"/> to the tenant with its roles in one save. False when the
    /// subject is already a member, including when another request made it one in the meantime;
    /// any other failure throws.
    /// </summary>
    Task<bool> AddMemberAsync(Guid tenantId, Guid subjectId, List<Guid> roleIds, List<string>? directPermissions = null, string? label = null, bool limitTo24Hours = false, CancellationToken ct = default);

    /// <summary>
    /// Removes a subject's membership from the specified tenant. Refuses to remove a system
    /// subject's membership or the tenant's last owner; a membership that is already absent
    /// succeeds.
    /// </summary>
    Task<MemberRemovalResult> RemoveMemberAsync(Guid tenantId, Guid subjectId, CancellationToken ct = default);

    /// <summary>
    /// Sets when a member's access ends, or clears it with null, as
    /// <c>TenantMembershipRepository.SetAccessEndUnlessOwnerAsync</c> does. When a date is set,
    /// every live guest link and device credential the member made in the tenant that would outlive
    /// the membership's end, the sum of its reasons, has its expiry lowered to it, and so does the
    /// stop of every alert channel they added, in the same transaction; when the sum has no end, none
    /// is. An expiry only ever shrinks: a later date lengthens none, and clearing the date changes none.
    /// </summary>
    /// <param name="now">The instant the change is made at, which decides which grants are live.</param>
    Task<OwnerGuardedChange> SetMemberAccessEndAsync(
        Guid tenantId, Guid memberId, DateTime? accessEndsAt, DateTime now, CancellationToken ct = default);

    /// <summary>
    /// Replaces the roles of a member's direct reason, as
    /// <c>TenantMembershipRepository.ReplaceRolesUnlessLastOwnerAsync</c> does, and in the same
    /// transaction brings the member's team reasons on the tenant into line, since an owner holds none:
    /// one who loses the owner role gains what the team's rows give, and one who gains it loses them.
    /// </summary>
    Task<OwnerGuardedChange> ReplaceMemberRolesAsync(
        Guid tenantId, Guid memberId, IReadOnlyCollection<Guid> removedRoleIds, IReadOnlyCollection<Guid> addedRoleIds,
        CancellationToken ct);

    /// <summary>
    /// Returns all tenants that the specified subject is a member of: tenants the subject owns
    /// first, then oldest membership, ties on slug. Callers read the first entry as a tenant
    /// that person owns.
    /// </summary>
    Task<List<TenantDto>> GetTenantsForSubjectAsync(Guid subjectId, CancellationToken ct = default);

    /// <summary>
    /// Returns the tenants the specified subject can go to: those of
    /// <see cref="GetTenantsForSubjectAsync"/> that are active, in the same order. An inactive
    /// tenant's host answers 403 on every path, so it is never somewhere to send anyone.
    /// </summary>
    Task<List<TenantDto>> GetReachableTenantsForSubjectAsync(Guid subjectId, CancellationToken ct = default);

    /// <summary>Checks whether a slug is valid and available for use.</summary>
    Task<SlugValidationResult> ValidateSlugAsync(string slug, CancellationToken ct = default);

    /// <summary>
    /// <see cref="ValidateSlugAsync(string, CancellationToken)"/>, except that a reserved name passes
    /// when <paramref name="allowReserved"/> is set. Reserved names keep the platform's own subdomains
    /// out of tenants' hands. Only the dev-only seed endpoint sets it (enabled in Development, or by
    /// <c>NOCTURNE_ENABLE_DEV_ONLY_ENDPOINTS</c>), which already hands out owner sessions to anyone.
    /// </summary>
    Task<SlugValidationResult> ValidateSlugAsync(string slug, bool allowReserved, CancellationToken ct = default);

    /// <summary>Creates a new tenant and its owner subject in a single operation, using either a passkey credential or an OIDC identity.</summary>
    /// <exception cref="ProvisionConflictException">
    /// The credential names an owner subject id that already exists. Only an already-linked OIDC
    /// identity resolves the owner to an existing subject.
    /// </exception>
    Task<ProvisionResult> ProvisionWithOwnerAsync(
        string slug, string displayName, string ownerUsername, string ownerEmail,
        ProvisionCredentialData? credential, ProvisionOidcIdentityData? oidcIdentity,
        CancellationToken ct = default);
}

public record TenantDto(Guid Id, string Slug, string DisplayName, bool IsActive, DateTime SysCreatedAt);

/// <param name="NextSlugChangeAt">
/// When the subdomain may next change, while the last change is less than
/// <see cref="ITenantService.SlugChangeInterval"/> old; otherwise null.
/// </param>
public record TenantSettingsDto(
    bool AllowPublicDocs, string Slug, string DisplayName, DateTime SysCreatedAt, DateTime? NextSlugChangeAt = null);

public record TenantCreatedDto(Guid Id, string Slug, string DisplayName, bool IsActive, DateTime SysCreatedAt);

public record TenantDetailDto(Guid Id, string Slug, string DisplayName, bool IsActive, DateTime SysCreatedAt, List<TenantMemberDto> Members);

/// <summary>
/// Projection of a tenant member, including their assigned roles and direct permissions.
/// </summary>
/// <param name="AccessEndsAt">When the membership ends, in UTC; null when it does not.</param>
/// <param name="HasEnded">Whether the membership has ended, so it grants nothing until the date is moved later.</param>
/// <param name="OutlastsCaller">
/// Whether the caller the member was read for has an end date and this member's access outlasts it,
/// so the caller may not change or remove the member. Always false when read for no caller.
/// </param>
/// <param name="HasConfirmedRecoveryEmail">
/// Whether the member holds a recovery email they confirmed. Null for a system or demo subject,
/// which has no one to recover, and for every member where this site cannot send the confirming
/// email (<c>IAccountLinkRoute.IsConfigured</c>), so a reader shows nothing for null. Only the
/// boolean is carried, never the address.
/// </param>
/// <param name="HasDirect">
/// Whether the member was let in on the tenant itself. A member who holds access only through a team
/// cannot have their roles, permissions, limit or end changed, nor be removed, from the member list:
/// stopping the team's access is what changes it.
/// </param>
/// <param name="Reasons">
/// Each reason the member holds, the direct one first; the fields above are their sum. A team reason
/// is listed only when its team's access is to this tenant.
/// </param>
/// <param name="DirectLiftsTeamLimit">
/// Whether the member's direct reason gives full history while a team reason limits them to the last
/// 24 hours, so the member reads full history: the sum keeps a limit only when every reason has it.
/// </param>
public record TenantMemberDto(
    Guid Id,
    Guid SubjectId,
    string? Name,
    bool IsSystemSubject,
    List<TenantMemberRoleDto> Roles,
    List<string>? DirectPermissions,
    string? Label,
    bool LimitTo24Hours,
    DateTime? LastUsedAt,
    DateTime SysCreatedAt,
    bool IsPlatformAdmin,
    DateTime? AccessEndsAt,
    bool HasEnded,
    bool? HasConfirmedRecoveryEmail,
    bool HasDirect,
    List<TenantMemberReasonDto> Reasons,
    bool DirectLiftsTeamLimit,
    bool OutlastsCaller = false);

/// <summary>Why a member holds their access.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<MemberReasonKind>))]
public enum MemberReasonKind
{
    /// <summary>They were let in on the tenant itself: an invite, a request, a manager, setup or the system.</summary>
    Direct,

    /// <summary>They are staff of a team the tenant gave access to.</summary>
    Team,
}

/// <summary>One reason a <see cref="TenantMemberDto"/> holds, with what it gives.</summary>
/// <param name="TeamId">The team, for a team reason.</param>
/// <param name="TeamName">The team's current name, for a team reason.</param>
/// <param name="AccessEndsAt">When the reason ends, in UTC; null when it does not.</param>
/// <param name="HasEnded">Whether the reason has ended, so it gives nothing.</param>
public record TenantMemberReasonDto(
    MemberReasonKind Kind,
    Guid? TeamId,
    string? TeamName,
    List<TenantMemberRoleDto> Roles,
    List<string>? DirectPermissions,
    bool LimitTo24Hours,
    DateTime? AccessEndsAt,
    bool HasEnded);

/// <summary>
/// A role a <see cref="TenantMemberDto"/> holds, with the permissions it confers.
/// </summary>
public record TenantMemberRoleDto(Guid RoleId, string Name, string Slug, List<string> Permissions);

public record SlugValidationResult(bool IsValid, string? Message = null);

/// <summary>
/// Passkey (WebAuthn) credential data provided during tenant provisioning.
/// </summary>
public record ProvisionCredentialData(
    string CredentialId,
    string PublicKey,
    uint SignCount,
    List<string> Transports,
    Guid? AaGuid,
    Guid? SubjectId);

/// <summary>
/// OIDC identity data provided during tenant provisioning for federated login.
/// </summary>
public record ProvisionOidcIdentityData(
    string Provider,
    string OidcSubjectId,
    string Issuer,
    string Email,
    Guid? SubjectId);

/// <summary>
/// Result of a full tenant-plus-owner provisioning operation.
/// </summary>
public record ProvisionResult(Guid TenantId, Guid SubjectId, string Slug);

/// <summary>A provision that would reuse an existing subject it has no claim to.</summary>
public sealed class ProvisionConflictException(string message) : Exception(message);

/// <summary>
/// Outcome of removing a membership. <paramref name="Ok"/> is false only for a refusal, which
/// carries the reason; a membership that was already absent is a success.
/// </summary>
public record MemberRemovalResult(bool Ok, string? ErrorDescription = null)
{
    /// <summary>The refusal is <see cref="Nocturne.Core.Models.Identity.OwnerGuardedChange.FromTeam"/>: the member holds no direct reason.</summary>
    public bool AccessFromTeam { get; init; }
}
