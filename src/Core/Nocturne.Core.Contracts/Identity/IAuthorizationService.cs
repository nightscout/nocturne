using Nocturne.Core.Models;

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
    /// <summary>
    /// Generate JWT token from access token
    /// </summary>
    /// <param name="accessToken">Access token to exchange</param>
    /// <returns>Authorization response with JWT token</returns>
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
    /// <param name="subjectId">Subject identifier</param>
    /// <param name="permission">Permission to check</param>
    /// <returns>True if permission is granted</returns>
    Task<bool> CheckPermissionAsync(string subjectId, string permission);

    // Subject management methods
    /// <summary>
    /// Get all subjects
    /// </summary>
    /// <returns>List of all subjects</returns>
    Task<List<Subject>> GetAllSubjectsAsync();

    /// <summary>
    /// Get a subject by ID
    /// </summary>
    /// <param name="id">Subject ID</param>
    /// <returns>Subject or null if not found</returns>
    Task<Subject?> GetSubjectByIdAsync(string id);

    /// <summary>
    /// Create a new subject
    /// </summary>
    /// <param name="subject">Subject to create</param>
    /// <returns>Created subject</returns>
    Task<Subject> CreateSubjectAsync(Subject subject);

    /// <summary>
    /// Update an existing subject
    /// </summary>
    /// <param name="subject">Subject to update</param>
    /// <returns>Updated subject or null if not found</returns>
    Task<Subject?> UpdateSubjectAsync(Subject subject);

    /// <summary>
    /// Delete a subject by ID
    /// </summary>
    /// <param name="id">Subject ID</param>
    /// <returns>True if deleted, false if not found</returns>
    Task<bool> DeleteSubjectAsync(string id);

    // Role management methods
    /// <summary>
    /// Get all roles
    /// </summary>
    /// <returns>List of all roles</returns>
    Task<List<Role>> GetAllRolesAsync();

    /// <summary>
    /// Get a role by ID
    /// </summary>
    /// <param name="id">Role ID</param>
    /// <returns>Role or null if not found</returns>
    Task<Role?> GetRoleByIdAsync(string id);

    /// <summary>
    /// Create a new role
    /// </summary>
    /// <param name="role">Role to create</param>
    /// <returns>Created role</returns>
    Task<Role> CreateRoleAsync(Role role);

    /// <summary>
    /// Update an existing role
    /// </summary>
    /// <param name="role">Role to update</param>
    /// <returns>Updated role or null if not found</returns>
    Task<Role?> UpdateRoleAsync(Role role);

    /// <summary>
    /// Delete a role by ID
    /// </summary>
    /// <param name="id">Role ID</param>
    /// <returns>True if deleted, false if not found</returns>
    Task<bool> DeleteRoleAsync(string id);
}
