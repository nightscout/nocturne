using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.API.Services.Auth;

/// <summary>
/// Service for managing authentication <see cref="Subject"/> entities — creation,
/// lookup by ID or token hash, password management, role assignment, and TOTP enrollment.
/// All audit-significant operations are forwarded to <see cref="IAuthAuditService"/>.
/// </summary>
/// <seealso cref="ISubjectService"/>
/// <seealso cref="IAuthAuditService"/>
/// <seealso cref="OAuthTokenService"/>
/// <seealso cref="RoleService"/>
public class SubjectService : ISubjectService
{
    private readonly NocturneDbContext _dbContext;
    private readonly IAuthAuditService _auditService;
    private readonly ILogger<SubjectService> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="SubjectService"/>.
    /// </summary>
    /// <param name="dbContext">The EF Core database context for subject and role entity access.</param>
    /// <param name="auditService">Service for recording audit events on authentication actions.</param>
    /// <param name="logger">The logger instance.</param>
    public SubjectService(NocturneDbContext dbContext, IAuthAuditService auditService, ILogger<SubjectService> logger)
    {
        _dbContext = dbContext;
        _auditService = auditService;
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
    public async Task<Subject?> FindSubjectByLegacyTokenAsync(string legacyAccessToken)
    {
        var prefix = LegacyNightscoutToken.ExtractDigestPrefix(legacyAccessToken);
        if (prefix == null)
        {
            return null;
        }

        // Push Nightscout's digest-prefix rule to the database. The prefix is validated hex, so it
        // is safe as a LIKE pattern; StartsWith translates to an index-friendly LIKE on PostgreSQL
        // (against the filtered ix_subjects_legacy_token_digest) and still evaluates on the InMemory
        // provider used in tests. Both stored digest and prefix are lowercase, so a case-sensitive
        // match is correct. Returns at most one row rather than materialising every migrated subject.
        var entity = await _dbContext
            .Subjects.AsNoTracking()
            .Include(s => s.SubjectRoles)
            .ThenInclude(sr => sr.Role)
            .Where(s => s.LegacyTokenDigest != null && s.IsActive && s.LegacyTokenDigest!.StartsWith(prefix))
            .FirstOrDefaultAsync();

        return entity == null ? null : MapToModel(entity);
    }

    /// <inheritdoc />
    public async Task<Subject> FindOrCreateFromOidcAsync(
        Guid providerId,
        string oidcSubjectId,
        string issuer,
        string? email = null,
        string? name = null,
        IEnumerable<string>? defaultRoles = null
    )
    {
        // Try to find existing subject via the join table
        var identity = await _dbContext.SubjectOidcIdentities
            .Include(x => x.Subject)
            .ThenInclude(s => s!.SubjectRoles)
            .ThenInclude(sr => sr.Role)
            .FirstOrDefaultAsync(x => x.OidcSubjectId == oidcSubjectId && x.Issuer == issuer);

        if (identity?.Subject != null)
        {
            var entity = identity.Subject;

            // Update email/name if provided
            if (!string.IsNullOrEmpty(email) && entity.Email != email)
            {
                entity.Email = email;
            }

            if (!string.IsNullOrEmpty(name) && entity.Name != name)
            {
                entity.Name = name;
            }

            // Always bump LastUsedAt on the identity row and the subject's UpdatedAt.
            identity.LastUsedAt = DateTime.UtcNow;
            entity.UpdatedAt = DateTime.UtcNow;
            await _dbContext.SaveChangesAsync();

            _logger.LogDebug(
                "Found existing subject {SubjectId} for OIDC identity {OidcSubjectId}",
                entity.Id,
                oidcSubjectId
            );
            return MapToModel(entity);
        }

        // Create new subject
        var newEntity = new SubjectEntity
        {
            Id = Guid.CreateVersion7(),
            Name = name ?? email ?? oidcSubjectId,
            Email = email,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        _dbContext.Subjects.Add(newEntity);

        // Create the OIDC identity link
        var identityEntity = new SubjectOidcIdentityEntity
        {
            Id = Guid.CreateVersion7(),
            SubjectId = newEntity.Id,
            ProviderId = providerId,
            OidcSubjectId = oidcSubjectId,
            Issuer = issuer,
            Email = email,
            LinkedAt = DateTime.UtcNow,
        };
        _dbContext.SubjectOidcIdentities.Add(identityEntity);

        await _dbContext.SaveChangesAsync();

        // Assign default roles if specified
        if (defaultRoles != null)
        {
            var assignedRoleIds = new HashSet<Guid>();
            foreach (var roleName in defaultRoles
                .Select(rn => rn.Trim())
                .Where(rn => !string.IsNullOrWhiteSpace(rn))
                .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var role = await _dbContext.Roles.FirstOrDefaultAsync(r => r.Name == roleName);
                if (role != null && assignedRoleIds.Add(role.Id))
                {
                    _dbContext.SubjectRoles.Add(
                        new SubjectRoleEntity
                        {
                            SubjectId = newEntity.Id,
                            RoleId = role.Id,
                            AssignedAt = DateTime.UtcNow,
                        }
                    );
                }
            }
            await _dbContext.SaveChangesAsync();
        }

        // Reload with roles
        newEntity = await _dbContext
            .Subjects.Include(s => s.SubjectRoles)
            .ThenInclude(sr => sr.Role)
            .FirstAsync(s => s.Id == newEntity.Id);

        _logger.LogInformation(
            "Created new subject {SubjectId} for OIDC identity {OidcSubjectId}",
            newEntity.Id,
            oidcSubjectId
        );
        return MapToModel(newEntity);
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

        await _auditService.LogAsync(AuthAuditEventType.SubjectCreated, entity.Id, success: true,
            detailsJson: JsonSerializer.Serialize(new { name = entity.Name }));

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

        await _auditService.LogAsync(AuthAuditEventType.SubjectUpdated, entity.Id, success: true,
            detailsJson: JsonSerializer.Serialize(new { name = entity.Name }));

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

        await _auditService.LogAsync(AuthAuditEventType.SubjectDeleted, subjectId, success: true);

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
        // Rotation must revoke the legacy Nightscout token too, otherwise the old (possibly
        // leaked) migrated token would keep authenticating through the legacy digest fallback.
        entity.LegacyTokenDigest = null;
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
                query = query.Where(s => s.OidcIdentities.Any(i => i.Issuer == filter.OidcIssuer));
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

        await _auditService.LogAsync(AuthAuditEventType.RoleAssigned, subjectId, success: true,
            detailsJson: JsonSerializer.Serialize(new { role = roleName }),
            actor: assignedBy is null ? null : new AuthAuditActor(assignedBy, null));

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

            await _auditService.LogAsync(AuthAuditEventType.RoleRemoved, subjectId, success: true,
                detailsJson: JsonSerializer.Serialize(new { role = roleName }));
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

    /// <inheritdoc />
    public async Task<IReadOnlyList<SubjectOidcIdentity>> GetLinkedOidcIdentitiesAsync(Guid subjectId)
    {
        var entities = await _dbContext.SubjectOidcIdentities
            .Include(x => x.Provider)
            .Where(x => x.SubjectId == subjectId)
            .OrderBy(x => x.LinkedAt)
            .ToListAsync();

        return entities.Select(e => new SubjectOidcIdentity
        {
            Id = e.Id,
            SubjectId = e.SubjectId,
            ProviderId = e.ProviderId,
            ProviderName = e.Provider?.Name ?? "Unknown",
            ProviderIcon = e.Provider?.Icon,
            ProviderButtonColor = e.Provider?.ButtonColor,
            OidcSubjectId = e.OidcSubjectId,
            Issuer = e.Issuer,
            Email = e.Email,
            LinkedAt = e.LinkedAt,
            LastUsedAt = e.LastUsedAt,
        }).ToList();
    }

    /// <inheritdoc />
    public async Task<(OidcLinkOutcome Outcome, Guid? IdentityId)> AttachOidcIdentityAsync(
        Guid subjectId, Guid providerId, string oidcSubjectId, string issuer, string? email)
    {
        var existing = await _dbContext.SubjectOidcIdentities
            .FirstOrDefaultAsync(x => x.OidcSubjectId == oidcSubjectId && x.Issuer == issuer);

        if (existing != null)
        {
            if (existing.SubjectId == subjectId)
            {
                existing.LastUsedAt = DateTime.UtcNow;
                if (!string.IsNullOrEmpty(email) && existing.Email != email)
                    existing.Email = email;
                await _dbContext.SaveChangesAsync();
                return (OidcLinkOutcome.AlreadyLinkedToSelf, existing.Id);
            }
            return (OidcLinkOutcome.AlreadyLinkedToOther, null);
        }

        var row = new SubjectOidcIdentityEntity
        {
            Id = Guid.CreateVersion7(),
            SubjectId = subjectId,
            ProviderId = providerId,
            OidcSubjectId = oidcSubjectId,
            Issuer = issuer,
            Email = email,
            LinkedAt = DateTime.UtcNow,
        };
        _dbContext.SubjectOidcIdentities.Add(row);
        await _dbContext.SaveChangesAsync();
        return (OidcLinkOutcome.Created, row.Id);
    }

    /// <inheritdoc />
    public async Task<SubjectOidcIdentity?> GetMostRecentlyUsedIdentityAsync(Guid subjectId)
    {
        var e = await _dbContext.SubjectOidcIdentities
            .Include(x => x.Provider)
            .Where(x => x.SubjectId == subjectId)
            .OrderByDescending(x => x.LastUsedAt ?? x.LinkedAt)
            .FirstOrDefaultAsync();

        if (e == null) return null;

        return new SubjectOidcIdentity
        {
            Id = e.Id,
            SubjectId = e.SubjectId,
            ProviderId = e.ProviderId,
            ProviderName = e.Provider?.Name ?? "Unknown",
            ProviderIcon = e.Provider?.Icon,
            ProviderButtonColor = e.Provider?.ButtonColor,
            OidcSubjectId = e.OidcSubjectId,
            Issuer = e.Issuer,
            Email = e.Email,
            LinkedAt = e.LinkedAt,
            LastUsedAt = e.LastUsedAt,
        };
    }

    /// <inheritdoc />
    public async Task<FactorRemovalResult> TryRemoveOidcIdentityAsync(Guid subjectId, Guid identityId)
    {
        // InMemory provider (used in tests) doesn't support transactions; skip in that case.
        var supportsTx = _dbContext.Database.ProviderName != "Microsoft.EntityFrameworkCore.InMemory";
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? tx = null;
        if (supportsTx)
        {
            tx = await _dbContext.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
        }
        try
        {
            var row = await _dbContext.SubjectOidcIdentities
                .FirstOrDefaultAsync(x => x.Id == identityId && x.SubjectId == subjectId);
            if (row == null)
            {
                if (tx != null) await tx.RollbackAsync();
                return FactorRemovalResult.NotFound;
            }

            var remainingPasskeys = await _dbContext.PasskeyCredentials
                .CountAsync(p => p.SubjectId == subjectId);
            var remainingOidc = await _dbContext.SubjectOidcIdentities
                .CountAsync(i => i.SubjectId == subjectId && i.Id != identityId);
            if (remainingPasskeys + remainingOidc < 1)
            {
                if (tx != null) await tx.RollbackAsync();
                return FactorRemovalResult.LastPrimaryFactor;
            }

            _dbContext.SubjectOidcIdentities.Remove(row);
            await _dbContext.SaveChangesAsync();
            if (tx != null) await tx.CommitAsync();
            return FactorRemovalResult.Removed;
        }
        finally
        {
            if (tx != null) await tx.DisposeAsync();
        }
    }

    /// <inheritdoc />
    public async Task<FactorRemovalResult> TryRemovePasskeyCredentialAsync(Guid subjectId, Guid credentialId)
    {
        var supportsTx = _dbContext.Database.ProviderName != "Microsoft.EntityFrameworkCore.InMemory";
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? tx = null;
        if (supportsTx)
        {
            tx = await _dbContext.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
        }
        try
        {
            var row = await _dbContext.PasskeyCredentials
                .FirstOrDefaultAsync(x => x.Id == credentialId && x.SubjectId == subjectId);
            if (row == null)
            {
                if (tx != null) await tx.RollbackAsync();
                return FactorRemovalResult.NotFound;
            }

            var remainingPasskeys = await _dbContext.PasskeyCredentials
                .CountAsync(p => p.SubjectId == subjectId && p.Id != credentialId);
            var remainingOidc = await _dbContext.SubjectOidcIdentities
                .CountAsync(i => i.SubjectId == subjectId);
            if (remainingPasskeys + remainingOidc < 1)
            {
                if (tx != null) await tx.RollbackAsync();
                return FactorRemovalResult.LastPrimaryFactor;
            }

            _dbContext.PasskeyCredentials.Remove(row);
            await _dbContext.SaveChangesAsync();
            if (tx != null) await tx.CommitAsync();
            return FactorRemovalResult.Removed;
        }
        finally
        {
            if (tx != null) await tx.DisposeAsync();
        }
    }

    /// <inheritdoc />
    public async Task<int> CountPrimaryAuthFactorsAsync(Guid subjectId)
    {
        var passkeys = await _dbContext.PasskeyCredentials.CountAsync(p => p.SubjectId == subjectId);
        var oidc = await _dbContext.SubjectOidcIdentities.CountAsync(i => i.SubjectId == subjectId);
        return passkeys + oidc;
    }

    /// <inheritdoc />
    public async Task UpdateOidcIdentityLastUsedAsync(Guid identityId)
    {
        var row = await _dbContext.SubjectOidcIdentities.FindAsync(identityId);
        if (row != null)
        {
            row.LastUsedAt = DateTime.UtcNow;
            await _dbContext.SaveChangesAsync();
        }
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
            IsActive = entity.IsActive,
            IsSystemSubject = entity.IsSystemSubject,
            IsPlatformAdmin = entity.IsPlatformAdmin,
            CreatedAt = entity.CreatedAt,
            LastLoginAt = entity.LastLoginAt,
            Notes = entity.Notes,
            PreferredLanguage = entity.PreferredLanguage,
            Preferences = entity.Preferences,
            AvatarUrl = entity.AvatarUrl,
            Roles = new List<Role>(),
            Permissions = new List<string>(),
        };

        // Determine type from columns on the entity itself (no navigation required).
        // Device/Service subjects are distinguished by the presence of an access token hash;
        // everything else is treated as a User (including OIDC-linked users whose identities
        // live in the SubjectOidcIdentities join table).
        //
        // SubjectType.Service is no longer derived here — with the OIDC columns gone,
        // MapToModel cannot distinguish a Service subject from a regular User without
        // loading the OidcIdentities navigation, which would be a hidden N+1. Service
        // subjects are explicitly constructed in AuthorizationService. Follow-up:
        // consider collapsing Service/User in the enum, or persisting the type as a
        // column on SubjectEntity.
        subject.Type = !string.IsNullOrEmpty(entity.AccessTokenHash)
            ? SubjectType.Device
            : SubjectType.User;

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
