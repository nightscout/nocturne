using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using Nocturne.Core.Contracts;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Abstractions;
using AuthRole = Nocturne.Core.Models.Authorization.Role;
using AuthSubject = Nocturne.Core.Models.Authorization.Subject;

namespace Nocturne.API.Services;

/// <summary>
/// Service for handling authorization operations including JWT generation and permission management
/// </summary>
public class AuthorizationService : IAuthorizationService, IDisposable
{
    private readonly IPostgreSqlService _postgreSqlService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AuthorizationService> _logger;
    private readonly ISubjectService _subjectService;
    private readonly IRoleService _roleService;
    private readonly IJwtService _jwtService;
    private readonly PermissionTrie _permissionTrie;
    private readonly Dictionary<string, Permission> _seenPermissions = new();
    private readonly object _permissionsLock = new();

    // Memory protection for _seenPermissions
    private const int MAX_PERMISSIONS_CACHE_SIZE = 5000;
    private bool _disposed;

    public AuthorizationService(
        IPostgreSqlService postgreSqlService,
        IConfiguration configuration,
        ILogger<AuthorizationService> logger,
        ISubjectService subjectService,
        IRoleService roleService,
        IJwtService jwtService
    )
    {
        _postgreSqlService = postgreSqlService;
        _configuration = configuration;
        _logger = logger;
        _subjectService = subjectService;
        _roleService = roleService;
        _jwtService = jwtService;
        _permissionTrie = new PermissionTrie();

        // Initialize with common permissions
        InitializeCommonPermissions();
    }

    /// <summary>
    /// Generate JWT token from access token
    /// </summary>
    /// <param name="accessToken">Access token to exchange</param>
    /// <returns>Authorization response with JWT token</returns>
    public async Task<AuthorizationResponse?> GenerateJwtFromAccessTokenAsync(string accessToken)
    {
        try
        {
            _logger.LogDebug("Generating JWT for access token");

            // Hash the access token to look it up
            var tokenHash = ComputeSha256Hash(accessToken);

            // Find subject by access token hash
            var subject = await _subjectService.GetSubjectByAccessTokenHashAsync(tokenHash);

            if (subject == null)
            {
                _logger.LogDebug("Access token not found");
                return null;
            }

            if (!subject.IsActive)
            {
                _logger.LogDebug("Subject {SubjectId} is deactivated", subject.Id);
                return null;
            }

            // Get permissions for the subject
            var permissions = await _subjectService.GetSubjectPermissionsAsync(subject.Id);
            var roles = await _subjectService.GetSubjectRolesAsync(subject.Id);

            // Generate JWT using the new JWT service
            var subjectInfo = new SubjectInfo
            {
                Id = subject.Id,
                Name = subject.Name,
                Email = subject.Email,
                OidcSubjectId = subject.OidcSubjectId,
                OidcIssuer = subject.OidcIssuer,
            };

            var jwt = _jwtService.GenerateAccessToken(subjectInfo, permissions, roles);

            // Update last login
            _ = _subjectService.UpdateLastLoginAsync(subject.Id);

            // Calculate expiration (default 1 hour from now for legacy compatibility)
            var now = DateTimeOffset.UtcNow;
            var exp = now.AddHours(1);

            return new AuthorizationResponse
            {
                Token = jwt,
                Sub = subject.Name,
                Iat = now.ToUnixTimeSeconds(),
                Exp = exp.ToUnixTimeSeconds(),
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating JWT from access token");
            return null;
        }
    }

    /// <summary>
    /// Compute SHA-256 hash of a string
    /// </summary>
    private static string ComputeSha256Hash(string input)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(input);
        var hash = System.Security.Cryptography.SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Get all permissions that have been seen by the system
    /// </summary>
    /// <returns>List of permissions with usage statistics</returns>
    public async Task<PermissionsResponse> GetAllPermissionsAsync()
    {
        try
        {
            _logger.LogDebug("Getting all seen permissions");

            var permissions = new List<Permission>();

            lock (_permissionsLock)
            {
                permissions = _seenPermissions.Values.ToList();
            }

            var roles = await _roleService.GetAllRolesAsync();

            var now = DateTime.UtcNow;

            foreach (var role in roles)
            {
                foreach (var permission in role.Permissions)
                {
                    if (!_seenPermissions.ContainsKey(permission))
                    {
                        permissions.Add(
                            new Permission
                            {
                                Name = permission,
                                Count = 0,
                                FirstSeen = now,
                                LastSeen = now,
                            }
                        );
                    }
                }
            }

            return new PermissionsResponse { Permissions = permissions.OrderBy(p => p.Name).ToList() };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting all permissions");
            return new PermissionsResponse();
        }
    }

    /// <summary>
    /// Get permission hierarchy structure as a trie
    /// </summary>
    /// <returns>Permission trie structure</returns>
    public async Task<PermissionTrieResponse> GetPermissionTrieAsync()
    {
        try
        {
            _logger.LogDebug("Building permission trie structure");

            // TODO: Implement Role management in PostgreSQL service
            var roles = await _roleService.GetAllRolesAsync();

            var allPermissions = new HashSet<string>();
            foreach (var role in roles)
            {
                foreach (var permission in role.Permissions)
                {
                    allPermissions.Add(permission);
                }
            }

            // Add seen permissions
            lock (_permissionsLock)
            {
                foreach (var permission in _seenPermissions.Keys)
                {
                    allPermissions.Add(permission);
                }
            }

            // Build new trie with all permissions
            var trie = new PermissionTrie();
            trie.Add(allPermissions);

            // Convert to our response format
            var response = new PermissionTrieResponse
            {
                Root = BuildTrieNode(trie),
                Count = trie.Count,
            };

            _logger.LogDebug("Built permission trie with {Count} permissions", response.Count);
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error building permission trie");
            return new PermissionTrieResponse();
        }
    }

    /// <summary>
    /// Check if a permission is allowed for a subject
    /// </summary>
    /// <param name="subjectId">Subject identifier</param>
    /// <param name="permission">Permission to check</param>
    /// <returns>True if permission is granted</returns>
    public async Task<bool> CheckPermissionAsync(string subjectId, string permission)
    {
        try
        {
            _logger.LogDebug(
                "Checking permission {Permission} for subject {SubjectId}",
                permission,
                subjectId
            );

            if (!Guid.TryParse(subjectId, out var guid))
            {
                _logger.LogDebug("Invalid subject ID format: {SubjectId}", subjectId);
                return false;
            }

            return await _subjectService.HasPermissionAsync(guid, permission);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Error checking permission {Permission} for subject {SubjectId}",
                permission,
                subjectId
            );
            return false;
        }
    }

    /// <summary>
    /// Record a permission usage for statistics
    /// </summary>
    /// <param name="permission">Permission that was used</param>
    public Task RecordPermissionUsageAsync(string permission)
    {
        try
        {
            lock (_permissionsLock)
            {
                var now = DateTime.UtcNow;

                if (_seenPermissions.ContainsKey(permission))
                {
                    _seenPermissions[permission].Count++;
                    _seenPermissions[permission].LastSeen = now;
                }
                else
                {
                    _seenPermissions[permission] = new Permission
                    {
                        Name = permission,
                        Count = 1,
                        FirstSeen = now,
                        LastSeen = now,
                    };
                }

                // Periodically clean up old permissions to prevent memory leaks
                if (_seenPermissions.Count % 100 == 0)
                {
                    CleanupOldPermissions();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error recording permission usage for {Permission}", permission);
        }

        return Task.CompletedTask;
    }

    // Subject management methods
    /// <summary>
    /// Get all subjects
    /// </summary>
    /// <returns>List of all subjects</returns>
    public async Task<List<Subject>> GetAllSubjectsAsync()
    {
        try
        {
            _logger.LogDebug("Getting all subjects");

            var subjects = await _subjectService.GetSubjectsAsync();

            // Map from new Subject model to legacy Subject model
            return subjects.Select(MapToLegacySubject).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting all subjects");
            throw;
        }
    }

    /// <summary>
    /// Get a subject by ID
    /// </summary>
    /// <param name="id">Subject ID</param>
    /// <returns>Subject or null if not found</returns>
    public async Task<Subject?> GetSubjectByIdAsync(string id)
    {
        try
        {
            _logger.LogDebug("Getting subject by ID: {Id}", id);

            if (!Guid.TryParse(id, out var guid))
            {
                _logger.LogDebug("Invalid subject ID format: {Id}", id);
                return null;
            }

            var subject = await _subjectService.GetSubjectByIdAsync(guid);
            return subject != null ? MapToLegacySubject(subject) : null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting subject by ID: {Id}", id);
            throw;
        }
    }

    /// <summary>
    /// Create a new subject
    /// </summary>
    /// <param name="subject">Subject to create</param>
    /// <returns>Created subject</returns>
    public async Task<Subject> CreateSubjectAsync(Subject subject)
    {
        try
        {
            _logger.LogDebug("Creating new subject: {Name}", subject.Name);

            // Map to new Subject model
            var newSubject = new AuthSubject
            {
                Name = subject.Name ?? "Unknown",
                Notes = subject.Notes,
                Type = Nocturne.Core.Models.Authorization.SubjectType.Service,
                IsActive = true,
            };

            var result = await _subjectService.CreateSubjectAsync(newSubject);

            // Assign roles if specified
            if (subject.Roles != null && subject.Roles.Count > 0)
            {
                foreach (var role in subject.Roles)
                {
                    await _subjectService.AssignRoleAsync(result.Subject.Id, role);
                }
            }

            // Map back to legacy model and include the generated access token
            var legacySubject = MapToLegacySubject(result.Subject);
            if (result.AccessToken != null)
            {
                legacySubject.AccessToken = result.AccessToken;
            }

            // Get the roles we just assigned
            var assignedRoles = await _subjectService.GetSubjectRolesAsync(result.Subject.Id);
            legacySubject.Roles = assignedRoles;

            _logger.LogDebug(
                "Successfully created subject: {Name} with ID: {Id}",
                legacySubject.Name,
                legacySubject.Id
            );

            return legacySubject;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating subject: {Name}", subject.Name);
            throw;
        }
    }

    /// <summary>
    /// Update an existing subject
    /// </summary>
    /// <param name="subject">Subject to update</param>
    /// <returns>Updated subject or null if not found</returns>
    public async Task<Subject?> UpdateSubjectAsync(Subject subject)
    {
        try
        {
            _logger.LogDebug("Updating subject: {Id}", subject.Id);

            if (string.IsNullOrEmpty(subject.Id) || !Guid.TryParse(subject.Id, out var guid))
            {
                _logger.LogDebug("Invalid subject ID format: {Id}", subject.Id);
                return null;
            }

            // Get existing subject
            var existing = await _subjectService.GetSubjectByIdAsync(guid);
            if (existing == null)
            {
                return null;
            }

            // Update fields
            existing.Name = subject.Name ?? existing.Name;
            existing.Notes = subject.Notes ?? existing.Notes;

            var updated = await _subjectService.UpdateSubjectAsync(existing);
            if (updated == null)
            {
                return null;
            }

            // Update roles if specified
            if (subject.Roles != null)
            {
                var currentRoles = await _subjectService.GetSubjectRolesAsync(guid);

                // Remove roles not in the new list
                foreach (var role in currentRoles.Except(subject.Roles))
                {
                    await _subjectService.RemoveRoleAsync(guid, role);
                }

                // Add roles not in the current list
                foreach (var role in subject.Roles.Except(currentRoles))
                {
                    await _subjectService.AssignRoleAsync(guid, role);
                }
            }

            return MapToLegacySubject(updated);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating subject: {Id}", subject.Id);
            throw;
        }
    }

    /// <summary>
    /// Delete a subject by ID
    /// </summary>
    /// <param name="id">Subject ID</param>
    /// <returns>True if deleted, false if not found</returns>
    public async Task<bool> DeleteSubjectAsync(string id)
    {
        try
        {
            _logger.LogDebug("Deleting subject: {Id}", id);

            if (!Guid.TryParse(id, out var guid))
            {
                _logger.LogDebug("Invalid subject ID format: {Id}", id);
                return false;
            }

            return await _subjectService.DeleteSubjectAsync(guid);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting subject: {Id}", id);
            throw;
        }
    }

    // Role management methods
    /// <summary>
    /// Get all roles
    /// </summary>
    /// <returns>List of all roles</returns>
    public async Task<List<Role>> GetAllRolesAsync()
    {
        try
        {
            _logger.LogDebug("Getting all roles");

            var roles = await _roleService.GetAllRolesAsync();
            return roles.Select(MapToLegacyRole).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting all roles");
            throw;
        }
    }

    /// <summary>
    /// Get a role by ID
    /// </summary>
    /// <param name="id">Role ID</param>
    /// <returns>Role or null if not found</returns>
    public async Task<Role?> GetRoleByIdAsync(string id)
    {
        try
        {
            _logger.LogDebug("Getting role by ID: {Id}", id);

            if (!Guid.TryParse(id, out var guid))
            {
                _logger.LogDebug("Invalid role ID format: {Id}", id);
                return null;
            }

            var role = await _roleService.GetRoleByIdAsync(guid);
            return role != null ? MapToLegacyRole(role) : null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting role by ID: {Id}", id);
            throw;
        }
    }

    /// <summary>
    /// Create a new role
    /// </summary>
    /// <param name="role">Role to create</param>
    /// <returns>Created role</returns>
    public async Task<Role> CreateRoleAsync(Role role)
    {
        try
        {
            _logger.LogDebug("Creating new role: {Name}", role.Name);

            // Map to new Role model
            var newRole = new AuthRole
            {
                Name = role.Name,
                Permissions = role.Permissions?.ToList() ?? new List<string>(),
                Description = role.Notes,
                IsSystemRole = false,
            };

            var created = await _roleService.CreateRoleAsync(newRole);

            _logger.LogDebug(
                "Successfully created role: {Name} with ID: {Id}",
                created.Name,
                created.Id
            );

            return MapToLegacyRole(created);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating role: {Name}", role.Name);
            throw;
        }
    }

    /// <summary>
    /// Update an existing role
    /// </summary>
    /// <param name="role">Role to update</param>
    /// <returns>Updated role or null if not found</returns>
    public async Task<Role?> UpdateRoleAsync(Role role)
    {
        try
        {
            _logger.LogDebug("Updating role: {Id}", role.Id);

            if (string.IsNullOrEmpty(role.Id) || !Guid.TryParse(role.Id, out var guid))
            {
                _logger.LogDebug("Invalid role ID format: {Id}", role.Id);
                return null;
            }

            // Get existing role
            var existing = await _roleService.GetRoleByIdAsync(guid);
            if (existing == null)
            {
                return null;
            }

            // Update fields
            existing.Name = role.Name ?? existing.Name;
            existing.Permissions = role.Permissions?.ToList() ?? existing.Permissions;
            existing.Description = role.Notes ?? existing.Description;

            var updated = await _roleService.UpdateRoleAsync(existing);
            return updated != null ? MapToLegacyRole(updated) : null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating role: {Id}", role.Id);
            throw;
        }
    }

    /// <summary>
    /// Delete a role by ID
    /// </summary>
    /// <param name="id">Role ID</param>
    /// <returns>True if deleted, false if not found</returns>
    public async Task<bool> DeleteRoleAsync(string id)
    {
        try
        {
            _logger.LogDebug("Deleting role: {Id}", id);

            if (!Guid.TryParse(id, out var guid))
            {
                _logger.LogDebug("Invalid role ID format: {Id}", id);
                return false;
            }

            return await _roleService.DeleteRoleAsync(guid);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting role: {Id}", id);
            throw;
        }
    }

    /// <summary>
    /// Generate a secure access token
    /// </summary>
    /// <returns>Access token string</returns>
    private static string GenerateAccessToken()
    {
        return Guid.CreateVersion7().ToString("N") + "-" + Guid.CreateVersion7().ToString("N");
    }

    /// <summary>
    /// Initialize common Nightscout permissions
    /// </summary>
    private void InitializeCommonPermissions()
    {
        var commonPermissions = new[]
        {
            "*",
            "api:*",
            "api:*:read",
            "api:*:create",
            "api:*:update",
            "api:*:delete",
            "api:*:admin",
            "api:entries:*",
            "api:entries:read",
            "api:entries:create",
            "api:entries:update",
            "api:entries:delete",
            "api:treatments:*",
            "api:treatments:read",
            "api:treatments:create",
            "api:treatments:update",
            "api:treatments:delete",
            "api:devicestatus:*",
            "api:devicestatus:read",
            "api:devicestatus:create",
            "api:devicestatus:update",
            "api:devicestatus:delete",
            "api:profile:*",
            "api:profile:read",
            "api:profile:create",
            "api:profile:update",
            "api:profile:delete",
            "api:food:*",
            "api:food:read",
            "api:food:create",
            "api:food:update",
            "api:food:delete",
            "api:activity:*",
            "api:activity:read",
            "api:activity:create",
            "api:activity:update",
            "api:activity:delete",
            "readable",
            "denied",
            "admin",
        };

        _permissionTrie.Add(commonPermissions);

        var now = DateTime.UtcNow;
        lock (_permissionsLock)
        {
            foreach (var permission in commonPermissions)
            {
                _seenPermissions[permission] = new Permission
                {
                    Name = permission,
                    Count = 0,
                    FirstSeen = now,
                    LastSeen = now,
                };
            }
        }
    }

    /// <summary>
    /// Build a trie node for API response (recursive helper)
    /// </summary>
    /// <param name="trie">The permission trie</param>
    /// <returns>Root trie node</returns>
    private PermissionTrieNode BuildTrieNode(PermissionTrie trie)
    {
        // NOTE: The ShiroTrie library doesn't expose internal structure directly,
        // so we'll create a simplified representation based on the permissions
        var root = new PermissionTrieNode { Name = "root" };

        // We'll need to reconstruct the tree structure from the permissions
        // This is a simplified version - in a real implementation, we'd need
        // access to the internal trie structure or build our own
        var allPermissions = new List<string>();

        lock (_permissionsLock)
        {
            allPermissions.AddRange(_seenPermissions.Keys);
        }

        foreach (var permission in allPermissions)
        {
            var parts = permission.Split(':');
            var currentNode = root;

            for (int i = 0; i < parts.Length; i++)
            {
                var part = parts[i];

                if (!currentNode.Children.ContainsKey(part))
                {
                    currentNode.Children[part] = new PermissionTrieNode
                    {
                        Name = part,
                        IsLeaf = i == parts.Length - 1,
                    };
                }

                currentNode = currentNode.Children[part];

                // Update leaf status - a node is a leaf if it's at the end of this path
                // or if it represents a complete permission
                if (i == parts.Length - 1)
                {
                    currentNode.IsLeaf = true;
                }
            }
        }

        return root;
    }

    /// <summary>
    /// Clean up old permissions from the cache to prevent unbounded memory growth
    /// </summary>
    private void CleanupOldPermissions()
    {
        try
        {
            if (_seenPermissions.Count <= MAX_PERMISSIONS_CACHE_SIZE)
                return;

            var now = DateTime.UtcNow;
            var cutoffTime = now.AddDays(-30); // Remove permissions not seen in 30 days

            var permissionsToRemove = _seenPermissions
                .Where(kvp => kvp.Value.LastSeen < cutoffTime)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var permission in permissionsToRemove)
            {
                _seenPermissions.Remove(permission);
            }

            // If still too many, remove the least recently used
            if (_seenPermissions.Count > MAX_PERMISSIONS_CACHE_SIZE)
            {
                var lruPermissions = _seenPermissions
                    .OrderBy(kvp => kvp.Value.LastSeen)
                    .Take(_seenPermissions.Count - (MAX_PERMISSIONS_CACHE_SIZE * 3 / 4))
                    .Select(kvp => kvp.Key)
                    .ToList();

                foreach (var permission in lruPermissions)
                {
                    _seenPermissions.Remove(permission);
                }
            }

            _logger.LogDebug(
                "Cleaned up permissions cache, current size: {Count}",
                _seenPermissions.Count
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error cleaning up permissions cache");
        }
    }

    /// <summary>
    /// Map new Subject model to legacy Subject model for API compatibility
    /// </summary>
    private Subject MapToLegacySubject(AuthSubject subject)
    {
        return new Subject
        {
            Id = subject.Id.ToString(),
            Name = subject.Name,
            Notes = subject.Notes,
            Roles = subject.Roles?.Select(r => r.Name).ToList() ?? new List<string>(),
            Created = subject.CreatedAt,
            Modified = subject.CreatedAt, // Use CreatedAt as fallback
        };
    }

    /// <summary>
    /// Map new Role model to legacy Role model for API compatibility
    /// </summary>
    private static Role MapToLegacyRole(AuthRole role)
    {
        return new Role
        {
            Id = role.Id.ToString(),
            Name = role.Name,
            Permissions = role.Permissions?.ToList() ?? new List<string>(),
            Notes = role.Description ?? "",
            Created = role.CreatedAt,
            Modified = role.UpdatedAt ?? role.CreatedAt,
        };
    }

    /// <summary>
    /// Dispose resources
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        try
        {
            lock (_permissionsLock)
            {
                _seenPermissions.Clear();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error disposing AuthorizationService");
        }

        _disposed = true;
    }
}
