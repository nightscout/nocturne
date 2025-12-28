using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.API.Services.Auth;

/// <summary>
/// Service for managing authentication subjects
/// </summary>
public class SubjectService : ISubjectService
{
    private readonly NocturneDbContext _dbContext;
    private readonly ILogger<SubjectService> _logger;

    /// <summary>
    /// Creates a new instance of SubjectService
    /// </summary>
    public SubjectService(NocturneDbContext dbContext, ILogger<SubjectService> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Subject?> GetSubjectByIdAsync(Guid subjectId)
    {
        var entity = await _dbContext
            .Subjects.AsNoTracking()
            .Include(s => s.SubjectRoles)
            .ThenInclude(sr => sr.Role)
            .FirstOrDefaultAsync(s => s.Id == subjectId);

        return entity == null ? null : MapToModel(entity);
    }

    /// <inheritdoc />
    public async Task<Subject?> GetSubjectByAccessTokenHashAsync(string accessTokenHash)
    {
        var entity = await _dbContext
            .Subjects.AsNoTracking()
            .Include(s => s.SubjectRoles)
            .ThenInclude(sr => sr.Role)
            .FirstOrDefaultAsync(s => s.AccessTokenHash == accessTokenHash && s.IsActive);

        return entity == null ? null : MapToModel(entity);
    }

    /// <inheritdoc />
    public async Task<Subject> FindOrCreateFromOidcAsync(
        string oidcSubjectId,
        string issuer,
        string? email = null,
        string? name = null,
        IEnumerable<string>? defaultRoles = null
    )
    {
        // Try to find existing subject by OIDC identity
        var entity = await _dbContext
            .Subjects.Include(s => s.SubjectRoles)
            .ThenInclude(sr => sr.Role)
            .FirstOrDefaultAsync(s => s.OidcSubjectId == oidcSubjectId && s.OidcIssuer == issuer);

        if (entity != null)
        {
            // Update email/name if provided
            var needsUpdate = false;

            if (!string.IsNullOrEmpty(email) && entity.Email != email)
            {
                entity.Email = email;
                needsUpdate = true;
            }

            if (!string.IsNullOrEmpty(name) && entity.Name != name)
            {
                entity.Name = name;
                needsUpdate = true;
            }

            if (needsUpdate)
            {
                entity.UpdatedAt = DateTime.UtcNow;
                await _dbContext.SaveChangesAsync();
            }

            _logger.LogDebug(
                "Found existing subject {SubjectId} for OIDC identity {OidcSubjectId}",
                entity.Id,
                oidcSubjectId
            );
            return MapToModel(entity);
        }

        // Create new subject
        entity = new SubjectEntity
        {
            Id = Guid.CreateVersion7(),
            Name = name ?? email ?? oidcSubjectId,
            OidcSubjectId = oidcSubjectId,
            OidcIssuer = issuer,
            Email = email,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        _dbContext.Subjects.Add(entity);
        await _dbContext.SaveChangesAsync();

        // Assign default roles if specified
        if (defaultRoles != null)
        {
            var assignedRoleIds = new HashSet<Guid>();
            foreach (var roleName in defaultRoles
                .Select(name => name.Trim())
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var role = await _dbContext.Roles.FirstOrDefaultAsync(r => r.Name == roleName);
                if (role != null && assignedRoleIds.Add(role.Id))
                {
                    _dbContext.SubjectRoles.Add(
                        new SubjectRoleEntity
                        {
                            SubjectId = entity.Id,
                            RoleId = role.Id,
                            AssignedAt = DateTime.UtcNow,
                        }
                    );
                }
            }
            await _dbContext.SaveChangesAsync();
        }

        // Reload with roles
        entity = await _dbContext
            .Subjects.Include(s => s.SubjectRoles)
            .ThenInclude(sr => sr.Role)
            .FirstAsync(s => s.Id == entity.Id);

        _logger.LogInformation(
            "Created new subject {SubjectId} for OIDC identity {OidcSubjectId}",
            entity.Id,
            oidcSubjectId
        );
        return MapToModel(entity);
    }

    /// <inheritdoc />
    public async Task<SubjectCreationResult> CreateSubjectAsync(Subject subject)
    {
        string? plainAccessToken = null;

        var entity = new SubjectEntity
        {
            Id = subject.Id == Guid.Empty ? Guid.CreateVersion7() : subject.Id,
            Name = subject.Name,
            Email = subject.Email,
            OidcSubjectId = subject.OidcSubjectId,
            OidcIssuer = subject.OidcIssuer,
            Notes = subject.Notes,
            IsActive = subject.IsActive,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        // Generate access token for device/service subjects
        if (subject.Type == SubjectType.Device || subject.Type == SubjectType.Service)
        {
            plainAccessToken = GenerateAccessToken();
            entity.AccessTokenHash = HashAccessToken(plainAccessToken);
            entity.AccessTokenPrefix = $"{subject.Name.ToLowerInvariant()}-{plainAccessToken[..8]}";
        }

        _dbContext.Subjects.Add(entity);
        await _dbContext.SaveChangesAsync();

        // Assign roles
        var assignedRoleIds = new HashSet<Guid>();
        foreach (var roleName in subject.Roles
            .Select(role => role.Name.Trim())
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var roleEntity = await _dbContext.Roles.FirstOrDefaultAsync(r => r.Name == roleName);
            if (roleEntity != null && assignedRoleIds.Add(roleEntity.Id))
            {
                _dbContext.SubjectRoles.Add(
                    new SubjectRoleEntity
                    {
                        SubjectId = entity.Id,
                        RoleId = roleEntity.Id,
                        AssignedAt = DateTime.UtcNow,
                    }
                );
            }
        }
        await _dbContext.SaveChangesAsync();

        // Reload with roles
        entity = await _dbContext
            .Subjects.Include(s => s.SubjectRoles)
            .ThenInclude(sr => sr.Role)
            .FirstAsync(s => s.Id == entity.Id);

        _logger.LogInformation("Created subject {SubjectId} ({Name})", entity.Id, entity.Name);

        return new SubjectCreationResult
        {
            Subject = MapToModel(entity),
            AccessToken = plainAccessToken,
        };
    }

    /// <inheritdoc />
    public async Task<Subject?> UpdateSubjectAsync(Subject subject)
    {
        var entity = await _dbContext
            .Subjects.Include(s => s.SubjectRoles)
            .FirstOrDefaultAsync(s => s.Id == subject.Id);

        if (entity == null)
        {
            return null;
        }

        entity.Name = subject.Name;
        entity.Email = subject.Email;
        entity.Notes = subject.Notes;
        entity.IsActive = subject.IsActive;
        entity.UpdatedAt = DateTime.UtcNow;

        await _dbContext.SaveChangesAsync();

        // Reload with roles
        entity = await _dbContext
            .Subjects.Include(s => s.SubjectRoles)
            .ThenInclude(sr => sr.Role)
            .FirstAsync(s => s.Id == entity.Id);

        _logger.LogInformation("Updated subject {SubjectId}", entity.Id);
        return MapToModel(entity);
    }

    /// <inheritdoc />
    public async Task<bool> DeleteSubjectAsync(Guid subjectId)
    {
        var entity = await _dbContext.Subjects.FindAsync(subjectId);
        if (entity == null)
        {
            return false;
        }

        if (entity.IsSystemSubject)
        {
            _logger.LogWarning("Attempted to delete system subject {SubjectId} ({Name})", subjectId, entity.Name);
            return false;
        }

        _dbContext.Subjects.Remove(entity);
        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Deleted subject {SubjectId}", subjectId);
        return true;
    }

    /// <inheritdoc />
    public async Task<string?> RegenerateAccessTokenAsync(Guid subjectId)
    {
        var entity = await _dbContext.Subjects.FindAsync(subjectId);
        if (entity == null)
        {
            return null;
        }

        var plainAccessToken = GenerateAccessToken();
        entity.AccessTokenHash = HashAccessToken(plainAccessToken);
        entity.AccessTokenPrefix = $"{entity.Name.ToLowerInvariant()}-{plainAccessToken[..8]}";
        entity.UpdatedAt = DateTime.UtcNow;

        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Regenerated access token for subject {SubjectId}", subjectId);
        return plainAccessToken;
    }

    /// <inheritdoc />
    public async Task<bool> ActivateSubjectAsync(Guid subjectId)
    {
        var result = await _dbContext
            .Subjects.Where(s => s.Id == subjectId)
            .ExecuteUpdateAsync(s =>
                s.SetProperty(e => e.IsActive, true).SetProperty(e => e.UpdatedAt, DateTime.UtcNow)
            );

        return result > 0;
    }

    /// <inheritdoc />
    public async Task<bool> DeactivateSubjectAsync(Guid subjectId)
    {
        var result = await _dbContext
            .Subjects.Where(s => s.Id == subjectId)
            .ExecuteUpdateAsync(s =>
                s.SetProperty(e => e.IsActive, false).SetProperty(e => e.UpdatedAt, DateTime.UtcNow)
            );

        return result > 0;
    }

    /// <inheritdoc />
    public async Task<List<Subject>> GetSubjectsAsync(SubjectFilter? filter = null)
    {
        var query = _dbContext
            .Subjects.AsNoTracking()
            .Include(s => s.SubjectRoles)
            .ThenInclude(sr => sr.Role)
            .AsQueryable();

        if (filter != null)
        {
            if (filter.IsActive.HasValue)
            {
                query = query.Where(s => s.IsActive == filter.IsActive.Value);
            }

            if (!string.IsNullOrEmpty(filter.OidcIssuer))
            {
                query = query.Where(s => s.OidcIssuer == filter.OidcIssuer);
            }

            if (!string.IsNullOrEmpty(filter.NameContains))
            {
                query = query.Where(s => s.Name.Contains(filter.NameContains));
            }

            if (!string.IsNullOrEmpty(filter.EmailContains))
            {
                query = query.Where(s => s.Email != null && s.Email.Contains(filter.EmailContains));
            }

            if (!string.IsNullOrEmpty(filter.HasRole))
            {
                query = query.Where(s => s.SubjectRoles.Any(sr => sr.Role!.Name == filter.HasRole));
            }
        }

        query = query.OrderBy(s => s.Name);

        if (filter != null)
        {
            query = query.Skip(filter.Offset).Take(filter.Limit);
        }

        var entities = await query.ToListAsync();
        return entities.Select(MapToModel).ToList();
    }

    /// <inheritdoc />
    public async Task<List<string>> GetSubjectRolesAsync(Guid subjectId)
    {
        return await _dbContext
            .SubjectRoles.AsNoTracking()
            .Where(sr => sr.SubjectId == subjectId)
            .Select(sr => sr.Role!.Name)
            .ToListAsync();
    }

    /// <inheritdoc />
    public async Task<List<string>> GetSubjectPermissionsAsync(Guid subjectId)
    {
        var roles = await _dbContext
            .SubjectRoles.AsNoTracking()
            .Where(sr => sr.SubjectId == subjectId)
            .Include(sr => sr.Role)
            .Select(sr => sr.Role!)
            .ToListAsync();

        var permissions = new HashSet<string>();
        foreach (var role in roles)
        {
            if (role.Permissions != null)
            {
                foreach (var permission in role.Permissions)
                {
                    permissions.Add(permission);
                }
            }
        }

        return permissions.ToList();
    }

    /// <inheritdoc />
    public async Task<bool> AssignRoleAsync(
        Guid subjectId,
        string roleName,
        Guid? assignedBy = null
    )
    {
        var role = await _dbContext.Roles.FirstOrDefaultAsync(r => r.Name == roleName);
        if (role == null)
        {
            _logger.LogWarning(
                "Role {RoleName} not found when assigning to subject {SubjectId}",
                roleName,
                subjectId
            );
            return false;
        }

        var existingAssignment = await _dbContext.SubjectRoles.AnyAsync(sr =>
            sr.SubjectId == subjectId && sr.RoleId == role.Id
        );

        if (existingAssignment)
        {
            return false; // Already assigned
        }

        _dbContext.SubjectRoles.Add(
            new SubjectRoleEntity
            {
                SubjectId = subjectId,
                RoleId = role.Id,
                AssignedAt = DateTime.UtcNow,
                AssignedById = assignedBy,
            }
        );

        await _dbContext.SaveChangesAsync();

        _logger.LogInformation(
            "Assigned role {RoleName} to subject {SubjectId}",
            roleName,
            subjectId
        );
        return true;
    }

    /// <inheritdoc />
    public async Task<bool> RemoveRoleAsync(Guid subjectId, string roleName)
    {
        var role = await _dbContext.Roles.FirstOrDefaultAsync(r => r.Name == roleName);
        if (role == null)
        {
            return false;
        }

        var result = await _dbContext
            .SubjectRoles.Where(sr => sr.SubjectId == subjectId && sr.RoleId == role.Id)
            .ExecuteDeleteAsync();

        if (result > 0)
        {
            _logger.LogInformation(
                "Removed role {RoleName} from subject {SubjectId}",
                roleName,
                subjectId
            );
        }

        return result > 0;
    }

    /// <inheritdoc />
    public async Task<bool> HasPermissionAsync(Guid subjectId, string permission)
    {
        var permissions = await GetSubjectPermissionsAsync(subjectId);

        // Check for admin permission
        if (permissions.Contains("*"))
            return true;

        // Check exact match
        if (permissions.Contains(permission))
            return true;

        // Check hierarchical wildcards
        var parts = permission.Split(':');
        for (int i = 1; i <= parts.Length; i++)
        {
            var wildcardPermission = string.Join(":", parts.Take(i)) + ":*";
            if (permissions.Contains(wildcardPermission))
                return true;
        }

        // Check *:*:action pattern
        if (parts.Length >= 3)
        {
            var actionWildcard = $"*:*:{parts[^1]}";
            if (permissions.Contains(actionWildcard))
                return true;
        }

        return false;
    }

    /// <inheritdoc />
    public async Task<Subject?> InitializePublicSubjectAsync()
    {
        const string publicSubjectName = "Public";

        // Check if "Public" subject already exists
        var existing = await _dbContext
            .Subjects.Include(s => s.SubjectRoles)
            .ThenInclude(sr => sr.Role)
            .FirstOrDefaultAsync(s => s.Name == publicSubjectName && s.IsSystemSubject);

        if (existing != null)
        {
            _logger.LogDebug("Public subject already exists");
            return MapToModel(existing);
        }

        // Create the Public subject
        var entity = new SubjectEntity
        {
            Id = Guid.CreateVersion7(),
            Name = publicSubjectName,
            Email = null,
            Notes = "Represents unauthenticated access. Assign roles to control what the public can see.",
            IsActive = true,
            IsSystemSubject = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        _dbContext.Subjects.Add(entity);
        await _dbContext.SaveChangesAsync();

        // Assign the "public" role
        await AssignRoleAsync(entity.Id, "public");

        // Reload with roles
        entity = await _dbContext
            .Subjects.Include(s => s.SubjectRoles)
            .ThenInclude(sr => sr.Role)
            .FirstAsync(s => s.Id == entity.Id);

        _logger.LogInformation("Created system Public subject for unauthenticated access");

        return MapToModel(entity);
    }

    /// <inheritdoc />
    public async Task UpdateLastLoginAsync(Guid subjectId)
    {
        await _dbContext
            .Subjects.Where(s => s.Id == subjectId)
            .ExecuteUpdateAsync(s =>
                s.SetProperty(e => e.LastLoginAt, DateTime.UtcNow)
                    .SetProperty(e => e.UpdatedAt, DateTime.UtcNow)
            );
    }

    /// <summary>
    /// Generate a secure access token
    /// </summary>
    private static string GenerateAccessToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>
    /// Hash an access token with SHA256
    /// </summary>
    private static string HashAccessToken(string accessToken)
    {
        var bytes = Encoding.UTF8.GetBytes(accessToken);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Map entity to domain model
    /// </summary>
    private static Subject MapToModel(SubjectEntity entity)
    {
        var subject = new Subject
        {
            Id = entity.Id,
            Name = entity.Name,
            Email = entity.Email,
            OidcSubjectId = entity.OidcSubjectId,
            OidcIssuer = entity.OidcIssuer,
            IsActive = entity.IsActive,
            IsSystemSubject = entity.IsSystemSubject,
            CreatedAt = entity.CreatedAt,
            LastLoginAt = entity.LastLoginAt,
            Notes = entity.Notes,
            Roles = new List<Role>(),
            Permissions = new List<string>(),
        };

        // Determine type based on OIDC linkage
        if (!string.IsNullOrEmpty(entity.OidcSubjectId))
        {
            subject.Type = SubjectType.User;
        }
        else if (!string.IsNullOrEmpty(entity.AccessTokenHash))
        {
            subject.Type = SubjectType.Device;
        }
        else
        {
            subject.Type = SubjectType.Service;
        }

        // Map roles and aggregate permissions
        var permissions = new HashSet<string>();
        foreach (var subjectRole in entity.SubjectRoles)
        {
            if (subjectRole.Role != null)
            {
                subject.Roles.Add(
                    new Role
                    {
                        Id = subjectRole.Role.Id,
                        Name = subjectRole.Role.Name,
                        Description = subjectRole.Role.Description,
                        Permissions =
                            subjectRole.Role.Permissions != null
                                ? new List<string>(subjectRole.Role.Permissions)
                                : new List<string>(),
                        IsSystemRole = subjectRole.Role.IsSystemRole,
                    }
                );

                if (subjectRole.Role.Permissions != null)
                {
                    foreach (var permission in subjectRole.Role.Permissions)
                    {
                        permissions.Add(permission);
                    }
                }
            }
        }

        subject.Permissions = permissions.ToList();
        return subject;
    }
}
