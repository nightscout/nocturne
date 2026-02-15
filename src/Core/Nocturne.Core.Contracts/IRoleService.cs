using Nocturne.Core.Models.Authorization;

namespace Nocturne.Core.Contracts;

/// <summary>
/// Service for managing authorization roles
/// </summary>
public interface IRoleService
{
    /// <summary>
    /// Get all roles
    /// </summary>
    /// <returns>List of all roles</returns>
    Task<List<Role>> GetAllRolesAsync();

    /// <summary>
    /// Get a role by ID
    /// </summary>
    /// <param name="roleId">Role identifier</param>
    /// <returns>Role if found, null otherwise</returns>
    Task<Role?> GetRoleByIdAsync(Guid roleId);

    /// <summary>
    /// Get a role by name
    /// </summary>
    /// <param name="name">Role name</param>
    /// <returns>Role if found, null otherwise</returns>
    Task<Role?> GetRoleByNameAsync(string name);

    /// <summary>
    /// Create a new role
    /// </summary>
    /// <param name="role">Role to create</param>
    /// <returns>Created role</returns>
    /// <exception cref="InvalidOperationException">Thrown when a role with the same name already exists.</exception>
    Task<Role> CreateRoleAsync(Role role);

    /// <summary>
    /// Update an existing role
    /// </summary>
    /// <param name="role">Role to update</param>
    /// <returns>Updated role or null if not found</returns>
    /// <exception cref="InvalidOperationException">Thrown when a role with the same name already exists, or when attempting to rename a system role.</exception>
    Task<Role?> UpdateRoleAsync(Role role);

    /// <summary>
    /// Delete a role
    /// </summary>
    /// <param name="roleId">Role identifier</param>
    /// <returns>True if deleted, false if not found or is system role</returns>
    Task<bool> DeleteRoleAsync(Guid roleId);

    /// <summary>
    /// Get all subjects assigned to a role
    /// </summary>
    /// <param name="roleId">Role identifier</param>
    /// <returns>List of subjects with this role</returns>
    Task<List<Subject>> GetSubjectsInRoleAsync(Guid roleId);

    /// <summary>
    /// Initialize default system roles if they don't exist
    /// </summary>
    /// <returns>Number of roles created</returns>
    Task<int> InitializeDefaultRolesAsync();
}
