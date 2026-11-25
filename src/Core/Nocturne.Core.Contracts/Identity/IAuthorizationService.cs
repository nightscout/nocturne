using Nocturne.Core.Models;
using CredentialMaker = Nocturne.Core.Models.Authorization.CredentialMaker;

namespace Nocturne.Core.Contracts.Identity;

/// <summary>
/// Service for handling authorization operations including JWT generation and permission management.
/// </summary>
/// <seealso cref="Subject"/>
/// <seealso cref="Role"/>
/// <seealso cref="ISubjectService"/>
/// <seealso cref="IRoleService"/>
public interface IAuthorizationService
{
    /// <param name="accessToken">Access token to exchange</param>
    Task<AuthorizationResponse?> GenerateJwtFromAccessTokenAsync(string accessToken);

    /// <summary>
    /// Get all permissions that have been seen by the system
    /// </summary>
    /// <returns>List of permissions with usage statistics</returns>
    Task<PermissionsResponse> GetAllPermissionsAsync();

    /// <summary>
    /// Get permission hierarchy structure as a trie
    /// </summary>
    /// <returns>Permission trie structure</returns>
    Task<PermissionTrieResponse> GetPermissionTrieAsync();

    /// <summary>
    /// Check if a permission is allowed for a subject
    /// </summary>
    /// <returns>True if permission is granted</returns>
    Task<bool> CheckPermissionAsync(string subjectId, string permission);

    // Subject management methods
    Task<List<Subject>> GetAllSubjectsAsync();

    /// <returns>Subject or null if not found</returns>
    Task<Subject?> GetSubjectByIdAsync(string id);

    /// <summary>
    /// Mints a Nightscout API token as a device credential. It expires when
    /// <paramref name="maker"/>'s access ends, and never when that does not end or there is no maker.
    /// </summary>
    Task<Subject> CreateSubjectAsync(Subject subject, CredentialMaker? maker);

    /// <returns>Updated subject or null if not found</returns>
    Task<Subject?> UpdateSubjectAsync(Subject subject);

    /// <returns>True if deleted, false if not found</returns>
    Task<bool> DeleteSubjectAsync(string id);

    // Role management methods
    Task<List<Role>> GetAllRolesAsync();

    /// <returns>Role or null if not found</returns>
    Task<Role?> GetRoleByIdAsync(string id);

    Task<Role> CreateRoleAsync(Role role);

    /// <returns>Updated role or null if not found</returns>
    Task<Role?> UpdateRoleAsync(Role role);

    /// <returns>True if deleted, false if not found</returns>
    Task<bool> DeleteRoleAsync(string id);
}
