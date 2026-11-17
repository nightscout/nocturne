using Nocturne.Core.Models.Authorization;

namespace Nocturne.Core.Contracts.Auth;

/// <summary>
/// Service for managing authorization roles
/// </summary>
public interface IRoleService
{
    Task<List<Role>> GetAllRolesAsync();

    /// <returns>Role if found, null otherwise</returns>
    Task<Role?> GetRoleByIdAsync(Guid roleId);

    /// <exception cref="InvalidOperationException">Thrown when a role with the same name already exists.</exception>
    Task<Role> CreateRoleAsync(Role role);

    /// <returns>Updated role or null if not found</returns>
    /// <exception cref="InvalidOperationException">Thrown when a role with the same name already exists, or when attempting to rename a system role.</exception>
    Task<Role?> UpdateRoleAsync(Role role);

    /// <returns>True if deleted, false if not found or is system role</returns>
    Task<bool> DeleteRoleAsync(Guid roleId);

    /// <summary>
    /// Initialize default system roles if they don't exist
    /// </summary>
    Task<int> InitializeDefaultRolesAsync();
}
