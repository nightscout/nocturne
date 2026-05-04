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

    /// <summary>Creates a new tenant without assigning an owner.</summary>
    Task<TenantCreatedDto> CreateWithoutOwnerAsync(string slug, string displayName, CancellationToken ct = default);

    /// <summary>Re-seeds roles, public membership, and OAuth clients for an existing tenant after a data purge.</summary>
    Task SeedAfterResetAsync(Guid tenantId, CancellationToken ct = default);

    /// <summary>Returns all tenants on the platform.</summary>
    Task<List<TenantDto>> GetAllAsync(CancellationToken ct = default);

    /// <summary>Returns the tenant with its member list, or null if not found.</summary>
    Task<TenantDetailDto?> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>Updates a tenant's display name, active state, and access-request policy.</summary>
    Task<TenantDto> UpdateAsync(Guid id, string displayName, bool isActive, bool? allowAccessRequests = null, CancellationToken ct = default);

    /// <summary>Permanently deletes a tenant and all associated data.</summary>
    Task DeleteAsync(Guid id, CancellationToken ct = default);

    /// <summary>Adds a subject as a member of the specified tenant with the given roles and permissions.</summary>
    Task AddMemberAsync(Guid tenantId, Guid subjectId, List<Guid> roleIds, List<string>? directPermissions = null, string? label = null, bool limitTo24Hours = false, CancellationToken ct = default);

    /// <summary>Removes a subject's membership from the specified tenant.</summary>
    Task RemoveMemberAsync(Guid tenantId, Guid subjectId, CancellationToken ct = default);

    /// <summary>Returns all tenants that the specified subject is a member of.</summary>
    Task<List<TenantDto>> GetTenantsForSubjectAsync(Guid subjectId, CancellationToken ct = default);

    /// <summary>Checks whether a slug is valid and available for use.</summary>
    Task<SlugValidationResult> ValidateSlugAsync(string slug, CancellationToken ct = default);

    /// <summary>Creates a new tenant and its owner subject in a single operation, using either a passkey credential or an OIDC identity.</summary>
    Task<ProvisionResult> ProvisionWithOwnerAsync(
        string slug, string displayName, string ownerUsername, string ownerEmail,
        ProvisionCredentialData? credential, ProvisionOidcIdentityData? oidcIdentity,
        CancellationToken ct = default);
}

public record TenantDto(Guid Id, string Slug, string DisplayName, bool IsActive, DateTime SysCreatedAt);

public record TenantCreatedDto(Guid Id, string Slug, string DisplayName, bool IsActive, DateTime SysCreatedAt);

public record TenantDetailDto(Guid Id, string Slug, string DisplayName, bool IsActive, DateTime SysCreatedAt, List<TenantMemberDto> Members);

/// <summary>
/// Projection of a tenant member, including their assigned roles and direct permissions.
/// </summary>
public record TenantMemberDto(
    Guid Id,
    Guid SubjectId,
    string? Name,
    List<TenantMemberRoleDto> Roles,
    List<string>? DirectPermissions,
    string? Label,
    bool LimitTo24Hours,
    DateTime? LastUsedAt,
    DateTime SysCreatedAt);

/// <summary>
/// Lightweight role reference attached to a <see cref="TenantMemberDto"/>.
/// </summary>
public record TenantMemberRoleDto(Guid RoleId, string Name, string Slug);

/// <summary>
/// Result of a tenant slug validation check.
/// </summary>
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
