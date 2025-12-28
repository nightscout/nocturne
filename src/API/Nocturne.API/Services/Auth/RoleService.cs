using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.API.Services.Auth;

/// <summary>
/// Service for managing authorization roles
/// </summary>
public class RoleService : IRoleService
{
    private readonly NocturneDbContext _dbContext;
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

    public RoleService(NocturneDbContext dbContext, ILogger<RoleService> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task<List<Role>> GetAllRolesAsync()
    {
        var entities = await _dbContext.Roles.OrderBy(r => r.Name).ToListAsync();

        return entities.Select(MapToModel).ToList();
    }

    public async Task<Role?> GetRoleByIdAsync(Guid roleId)
    {
        var entity = await _dbContext.Roles.FindAsync(roleId);
        return entity != null ? MapToModel(entity) : null;
    }

    public async Task<Role?> GetRoleByNameAsync(string name)
    {
        var entity = await _dbContext.Roles.FirstOrDefaultAsync(r => r.Name == name);

        return entity != null ? MapToModel(entity) : null;
    }

    public async Task<Role> CreateRoleAsync(Role role)
    {
        // Check for duplicate name
        var existing = await _dbContext.Roles.FirstOrDefaultAsync(r => r.Name == role.Name);

        if (existing != null)
        {
            throw new InvalidOperationException($"Role with name '{role.Name}' already exists");
        }

        var entity = new RoleEntity
        {
            Name = role.Name,
            Description = role.Description,
            Permissions = role.Permissions,
            IsSystemRole = role.IsSystemRole,
            CreatedAt = DateTime.UtcNow,
        };

        _dbContext.Roles.Add(entity);
        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Created role: {RoleName}", role.Name);

        return MapToModel(entity);
    }

    public async Task<Role?> UpdateRoleAsync(Role role)
    {
        var entity = await _dbContext.Roles.FindAsync(role.Id);
        if (entity == null)
        {
            return null;
        }

        // Check for name conflict if name is changing
        if (entity.Name != role.Name)
        {
            var existing = await _dbContext.Roles.FirstOrDefaultAsync(r =>
                r.Name == role.Name && r.Id != role.Id
            );

            if (existing != null)
            {
                throw new InvalidOperationException($"Role with name '{role.Name}' already exists");
            }
        }

        // System roles can have permissions updated but not be renamed or deleted
        if (entity.IsSystemRole && entity.Name != role.Name)
        {
            throw new InvalidOperationException("Cannot rename system roles");
        }

        entity.Name = role.Name;
        entity.Description = role.Description;
        entity.Permissions = role.Permissions;
        entity.UpdatedAt = DateTime.UtcNow;

        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Updated role: {RoleName}", role.Name);

        return MapToModel(entity);
    }

    public async Task<bool> DeleteRoleAsync(Guid roleId)
    {
        var entity = await _dbContext.Roles.FindAsync(roleId);
        if (entity == null)
        {
            return false;
        }

        if (entity.IsSystemRole)
        {
            _logger.LogWarning("Attempted to delete system role: {RoleName}", entity.Name);
            return false;
        }

        // Remove all subject-role assignments first
        var assignments = await _dbContext
            .SubjectRoles.Where(sr => sr.RoleId == roleId)
            .ToListAsync();

        _dbContext.SubjectRoles.RemoveRange(assignments);
        _dbContext.Roles.Remove(entity);
        await _dbContext.SaveChangesAsync();

        _logger.LogInformation(
            "Deleted role: {RoleName} and {Count} assignments",
            entity.Name,
            assignments.Count
        );

        return true;
    }

    public async Task<List<Subject>> GetSubjectsInRoleAsync(Guid roleId)
    {
        // Load the assignments with included subject navigation property
        var assignments = await _dbContext
            .SubjectRoles.Where(sr => sr.RoleId == roleId)
            .Include(sr => sr.Subject)
            .ToListAsync();

        // Filter out any null navigations (i.e. inconsistent data) and map to model
        var subjects = assignments
            .Where(sr => sr.Subject != null)
            .Select(sr => sr.Subject!)
            .ToList();

        return subjects
            .Select(s => new Subject
            {
                Id = s.Id,
                Name = s.Name,
                Type = SubjectType.User, // Default type since entity doesn't track type
                Email = s.Email,
                IsActive = s.IsActive,
                OidcSubjectId = s.OidcSubjectId,
                OidcIssuer = s.OidcIssuer,
                CreatedAt = s.CreatedAt,
                LastLoginAt = s.LastLoginAt,
            })
            .ToList();
    }

    public async Task<int> InitializeDefaultRolesAsync()
    {
        int created = 0;

        foreach (var (name, (description, permissions)) in DefaultRoles)
        {
            var existing = await _dbContext.Roles.FirstOrDefaultAsync(r => r.Name == name);

            if (existing == null)
            {
                var entity = new RoleEntity
                {
                    Name = name,
                    Description = description,
                    Permissions = permissions.ToList(),
                    IsSystemRole = true,
                    CreatedAt = DateTime.UtcNow,
                };

                _dbContext.Roles.Add(entity);
                created++;
                _logger.LogInformation("Created default role: {RoleName}", name);
            }
            else if (existing.IsSystemRole)
            {
                // Update permissions for system roles if they've changed
                if (!existing.Permissions.SequenceEqual(permissions))
                {
                    existing.Permissions = permissions.ToList();
                    existing.Description = description;
                    existing.UpdatedAt = DateTime.UtcNow;
                    _logger.LogInformation("Updated permissions for system role: {RoleName}", name);
                }
            }
        }

        if (created > 0)
        {
            await _dbContext.SaveChangesAsync();
        }

        return created;
    }

    private static Role MapToModel(RoleEntity entity)
    {
        return new Role
        {
            Id = entity.Id,
            Name = entity.Name,
            Description = entity.Description,
            Permissions = entity.Permissions,
            IsSystemRole = entity.IsSystemRole,
            CreatedAt = entity.CreatedAt,
            UpdatedAt = entity.UpdatedAt,
        };
    }
}
