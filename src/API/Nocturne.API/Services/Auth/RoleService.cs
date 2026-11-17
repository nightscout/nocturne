using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Models.Authorization;

namespace Nocturne.API.Services.Auth;

/// <summary>
/// Manages the global authorisation roles and their permissions, and seeds the default system
/// roles on first run. A system role keeps its name and cannot be deleted.
/// </summary>
/// <seealso cref="IRoleService"/>
public class RoleService : IRoleService
{
    private readonly IRoleRepository _roles;
    private readonly ILogger<RoleService> _logger;

    /// <summary>
    /// Default system roles with their permissions
    /// </summary>
    private static readonly Dictionary<
        string,
        (string Description, string[] Permissions)
    > DefaultRoles = new()
    {
        ["admin"] = ("Full administrative access", new[] { "*" }),
        ["readable"] = (
            "Read-only access to all data",
            new[]
            {
                "api:entries:read",
                "api:treatments:read",
                "api:devicestatus:read",
                "api:profile:read",
                "api:food:read",
                "api:activity:read",
                "api:trackers:read",
            }
        ),
        ["public"] = (
            "Default permissions for unauthenticated access",
            new[]
            {
                "api:entries:read",
                "api:treatments:read",
                "api:devicestatus:read",
                "api:profile:read",
                "api:food:read",
                "api:activity:read",
                "api:trackers:read",
            }
        ),
        ["api"] = (
            "API access for devices and services",
            new[] { "api:entries:*", "api:treatments:*", "api:devicestatus:*", "api:profile:read" }
        ),
        ["careportal"] = (
            "Care portal access for entering treatments",
            new[]
            {
                "api:entries:read",
                "api:treatments:*",
                "api:devicestatus:read",
                "api:profile:read",
                "careportal:*",
                "api:trackers:*",
            }
        ),
        ["denied"] = ("No access", Array.Empty<string>()),
    };

    public RoleService(IRoleRepository roles, ILogger<RoleService> logger)
    {
        _roles = roles ?? throw new ArgumentNullException(nameof(roles));
        _logger = logger;
    }

    public async Task<List<Role>> GetAllRolesAsync() => [.. await _roles.ListAsync(CancellationToken.None)];

    public Task<Role?> GetRoleByIdAsync(Guid roleId) => _roles.GetAsync(roleId, CancellationToken.None);

    public async Task<Role> CreateRoleAsync(Role role)
    {
        if (await _roles.FindByNameAsync(role.Name, CancellationToken.None) is not null)
        {
            throw new InvalidOperationException($"Role with name '{role.Name}' already exists");
        }

        var created = await _roles.CreateAsync(
            new NewRole(role.Name, role.Description, role.Permissions, role.IsSystemRole), CancellationToken.None);

        _logger.LogInformation("Created role: {RoleName}", role.Name);

        return created;
    }

    public async Task<Role?> UpdateRoleAsync(Role role)
    {
        var existing = await _roles.GetAsync(role.Id, CancellationToken.None);
        if (existing == null)
        {
            return null;
        }

        if (existing.Name != role.Name)
        {
            var sameName = await _roles.FindByNameAsync(role.Name, CancellationToken.None);
            if (sameName is not null && sameName.Id != role.Id)
            {
                throw new InvalidOperationException($"Role with name '{role.Name}' already exists");
            }
        }

        // System roles can have permissions updated but not be renamed or deleted
        if (existing.IsSystemRole && existing.Name != role.Name)
        {
            throw new InvalidOperationException("Cannot rename system roles");
        }

        var updated = await _roles.UpdateAsync(
            role.Id, role.Name, role.Description, role.Permissions, CancellationToken.None);

        _logger.LogInformation("Updated role: {RoleName}", role.Name);

        return updated;
    }

    public async Task<bool> DeleteRoleAsync(Guid roleId)
    {
        var existing = await _roles.GetAsync(roleId, CancellationToken.None);
        if (existing == null)
        {
            return false;
        }

        if (existing.IsSystemRole)
        {
            _logger.LogWarning("Attempted to delete system role: {RoleName}", existing.Name);
            return false;
        }

        var assignments = await _roles.DeleteAsync(roleId, CancellationToken.None);
        if (assignments is null)
        {
            return false;
        }

        _logger.LogInformation(
            "Deleted role: {RoleName} and {Count} assignments",
            existing.Name,
            assignments);

        return true;
    }

    public async Task<int> InitializeDefaultRolesAsync()
    {
        int created = 0;

        foreach (var (name, (description, permissions)) in DefaultRoles)
        {
            var existing = await _roles.FindByNameAsync(name, CancellationToken.None);

            if (existing == null)
            {
                await _roles.CreateAsync(new NewRole(name, description, permissions, IsSystemRole: true), CancellationToken.None);
                created++;
                _logger.LogInformation("Created default role: {RoleName}", name);
            }
        }

        return created;
    }
}
