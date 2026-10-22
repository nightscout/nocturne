using Nocturne.Core.Models.Authorization;

namespace Nocturne.Core.Contracts.Auth;

/// <summary>
/// Service for managing authentication subjects (users, devices, API keys).
/// </summary>
/// <seealso cref="IRoleService"/>
/// <seealso cref="IPasskeyService"/>
/// <seealso cref="ITotpService"/>
/// <seealso cref="IOidcAuthService"/>
public interface ISubjectService
{
    /// <returns>Subject if found, null otherwise</returns>
    Task<Subject?> GetSubjectByIdAsync(Guid subjectId);

    /// <param name="oidcSubjectId">OIDC subject identifier (sub claim)</param>
    /// <param name="issuer">OIDC issuer URL</param>
    /// <param name="email">
    /// Email from OIDC claims. Always recorded on the identity link; it becomes the subject's email
    /// only when <paramref name="emailVerified"/> is true, because the subject's email is matched
    /// on elsewhere and a provider can assert any address it has not verified.
    /// </param>
    /// <param name="emailVerified">Whether the provider asserted <paramref name="email"/> as verified.</param>
    /// <param name="name">Name from OIDC claims</param>
    /// <param name="defaultRoles">Default roles to assign if creating</param>
    /// <returns>Found or created subject</returns>
    Task<Subject> FindOrCreateFromOidcAsync(
        Guid providerId,
        string oidcSubjectId,
        string issuer,
        string? email = null,
        bool emailVerified = false,
        string? name = null,
        IEnumerable<string>? defaultRoles = null);

    Task<IReadOnlyList<SubjectOidcIdentity>> GetLinkedOidcIdentitiesAsync(Guid subjectId);

    /// <summary>Returns the OIDC identity most recently used to authenticate the subject, or null if none.</summary>
    Task<SubjectOidcIdentity?> GetMostRecentlyUsedIdentityAsync(Guid subjectId);

    /// <summary>
    /// Attaches an OIDC identity to an existing subject. Returns the outcome and the identity ID on success.
    /// </summary>
    /// <param name="oidcSubjectId">The subject identifier from the OIDC provider (sub claim).</param>
    /// <param name="issuer">The OIDC issuer URL.</param>
    /// <param name="email">The email from OIDC claims, if any.</param>
    Task<(OidcLinkOutcome Outcome, Guid? IdentityId)> AttachOidcIdentityAsync(
        Guid subjectId, Guid providerId, string oidcSubjectId, string issuer, string? email);

    /// <summary>
    /// Atomically remove an OIDC identity from a subject, enforcing the primary-factor rule
    /// (at least one factor counted by <see cref="CountPrimaryAuthFactorsAsync"/> must remain).
    /// The check and the delete run inside a serializable transaction to prevent TOCTOU races.
    /// </summary>
    Task<FactorRemovalResult> TryRemoveOidcIdentityAsync(Guid subjectId, Guid identityId);

    /// <summary>
    /// Atomically remove a passkey credential from a subject, enforcing the primary-factor rule
    /// (at least one factor counted by <see cref="CountPrimaryAuthFactorsAsync"/> must remain).
    /// The check and the delete run inside a serializable transaction to prevent TOCTOU races.
    /// </summary>
    Task<FactorRemovalResult> TryRemovePasskeyCredentialAsync(Guid subjectId, Guid credentialId);

    /// <summary>
    /// Returns the total number of primary authentication factors for a subject: passkeys plus
    /// OIDC identities whose provider is enabled.
    /// </summary>
    Task<int> CountPrimaryAuthFactorsAsync(Guid subjectId);

    /// <summary>
    /// Returns whether the subject can sign in exactly one way — a single primary factor with no
    /// unused recovery codes behind it — so that losing that one credential locks the account out.
    /// </summary>
    Task<bool> HasSingleSignInMethodAsync(Guid subjectId);

    /// <summary>Updates the last-used timestamp on an OIDC identity to track recent activity.</summary>
    Task UpdateOidcIdentityLastUsedAsync(Guid identityId);

    /// <summary>
    /// Create a new subject (device/API key)
    /// </summary>
    /// <returns>Created subject with generated token if applicable</returns>
    Task<SubjectCreationResult> CreateSubjectAsync(Subject subject);

    /// <returns>Updated subject or null if not found</returns>
    Task<Subject?> UpdateSubjectAsync(Subject subject);

    /// <returns>True if deleted, false if not found</returns>
    Task<bool> DeleteSubjectAsync(Guid subjectId);

    /// <returns>True if activated, false if not found</returns>
    Task<bool> ActivateSubjectAsync(Guid subjectId);

    /// <returns>True if deactivated, false if not found</returns>
    Task<bool> DeactivateSubjectAsync(Guid subjectId);

    /// <returns>List of role names</returns>
    Task<List<string>> GetSubjectRolesAsync(Guid subjectId);

    /// <summary>
    /// Get all permissions for a subject (aggregated from all roles)
    /// </summary>
    Task<List<string>> GetSubjectPermissionsAsync(Guid subjectId);

    /// <param name="assignedBy">Who assigned the role (subject ID)</param>
    /// <returns>True if assigned, false if already assigned or not found</returns>
    Task<bool> AssignRoleAsync(Guid subjectId, string roleName, Guid? assignedBy = null);

    /// <returns>True if removed, false if not assigned or not found</returns>
    Task<bool> RemoveRoleAsync(Guid subjectId, string roleName);

    /// <summary>
    /// Check if a subject has a specific permission
    /// </summary>
    /// <param name="permission">Permission to check (Shiro-style)</param>
    /// <returns>True if subject has the permission</returns>
    Task<bool> HasPermissionAsync(Guid subjectId, string permission);

    Task UpdateLastLoginAsync(Guid subjectId);

    /// <summary>
    /// Initialize the Public system subject for unauthenticated access
    /// </summary>
    Task<Subject?> InitializePublicSubjectAsync();
}

/// <summary>
/// Outcome of an atomic primary-factor removal attempt.
/// </summary>
public enum FactorRemovalResult
{
    Removed,

    /// <summary>
    /// The factor was not found or was not owned by the caller.
    /// </summary>
    NotFound,

    /// <summary>
    /// Removing the factor would leave the subject with zero primary auth factors.
    /// </summary>
    LastPrimaryFactor,
}

public class SubjectCreationResult
{
    public required Subject Subject { get; set; }
}
