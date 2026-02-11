using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Attributes;
using Nocturne.Core.Contracts;
using Nocturne.Core.Models;

namespace Nocturne.API.Controllers;

/// <summary>
/// Authorization controller that provides 1:1 compatibility with Nightscout authorization endpoints
/// </summary>
[ApiController]
[Route("api/v2/authorization")]
public class AuthorizationController : ControllerBase
{
    private readonly IAuthorizationService _authorizationService;
    private readonly ILogger<AuthorizationController> _logger;

    public AuthorizationController(
        IAuthorizationService authorizationService,
        ILogger<AuthorizationController> logger
    )
    {
        _authorizationService = authorizationService;
        _logger = logger;
    }

    /// <summary>
    /// Generate JWT token from access token
    /// </summary>
    /// <param name="accessToken">Access token to exchange for JWT</param>
    /// <returns>JWT token response</returns>
    [HttpGet("request/{accessToken}")]
    [NightscoutEndpoint("/api/v2/authorization/request/:accessToken")]
    [ProducesResponseType(typeof(AuthorizationResponse), 200)]
    [ProducesResponseType(401)]
    public async Task<ActionResult<AuthorizationResponse>> GenerateJwtFromAccessToken(
        string accessToken
    )
    {
        _logger.LogDebug("JWT generation requested for access token");

        try
        {
            if (string.IsNullOrEmpty(accessToken))
            {
                _logger.LogWarning("Empty access token provided");
                return Unauthorized();
            }

            var result = await _authorizationService.GenerateJwtFromAccessTokenAsync(accessToken);

            if (result == null)
            {
                _logger.LogWarning("JWT generation failed - invalid access token");
                return Unauthorized();
            }

            _logger.LogDebug("Successfully generated JWT for subject {Subject}", result.Sub);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating JWT from access token");
            return StatusCode(500, "Internal server error");
        }
    }

    /// <summary>
    /// Get all permissions that have been seen by the system
    /// </summary>
    /// <returns>List of permissions with usage statistics</returns>
    [HttpGet("permissions")]
    [NightscoutEndpoint("/api/v2/authorization/permissions")]
    [RemoteQuery]
    [ProducesResponseType(typeof(PermissionsResponse), 200)]
    public async Task<ActionResult<PermissionsResponse>> GetAllPermissions()
    {
        _logger.LogDebug("All permissions requested");

        try
        {
            var result = await _authorizationService.GetAllPermissionsAsync();

            _logger.LogDebug("Successfully returned {Count} permissions", result.Permissions.Count);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting all permissions");
            return StatusCode(500, "Internal server error");
        }
    }

    /// <summary>
    /// Get permission hierarchy structure as a trie
    /// </summary>
    /// <returns>Permission trie structure</returns>
    [HttpGet("permissions/trie")]
    [NightscoutEndpoint("/api/v2/authorization/permissions/trie")]
    [RemoteQuery]
    [ProducesResponseType(typeof(PermissionTrieResponse), 200)]
    public async Task<ActionResult<PermissionTrieResponse>> GetPermissionTrie()
    {
        _logger.LogDebug("Permission trie structure requested");

        try
        {
            var result = await _authorizationService.GetPermissionTrieAsync();

            _logger.LogDebug(
                "Successfully returned permission trie with {Count} permissions",
                result.Count
            );
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting permission trie");
            return StatusCode(500, "Internal server error");
        }
    }

    // Subject management endpoints
    /// <summary>
    /// Get all subjects (users/devices)
    /// </summary>
    /// <returns>List of all subjects</returns>
    [HttpGet("subjects")]
    [RequireAdmin]
    [NightscoutEndpoint("/api/v2/authorization/subjects")]
    [RemoteQuery]
    [ProducesResponseType(typeof(List<Subject>), 200)]
    [ProducesResponseType(401)]
    [ProducesResponseType(403)]
    public async Task<ActionResult<List<Subject>>> GetAllSubjects()
    {
        _logger.LogDebug("Get all subjects requested");

        try
        {
            var subjects = await _authorizationService.GetAllSubjectsAsync();

            _logger.LogDebug("Successfully returned {Count} subjects", subjects.Count);
            return Ok(subjects);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting all subjects");
            return StatusCode(500, "Internal server error");
        }
    }

    /// <summary>
    /// Create a new subject
    /// </summary>
    /// <param name="subject">Subject to create</param>
    /// <returns>Created subject</returns>
    [HttpPost("subjects")]
    [RequireAdmin]
    [NightscoutEndpoint("/api/v2/authorization/subjects")]
    [RemoteCommand(Invalidates = ["GetAllSubjects"])]
    [ProducesResponseType(typeof(Subject), 201)]
    [ProducesResponseType(400)]
    [ProducesResponseType(401)]
    [ProducesResponseType(403)]
    public async Task<ActionResult<Subject>> CreateSubject([FromBody] Subject subject)
    {
        _logger.LogDebug("Create subject requested: {Name}", subject.Name);

        try
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(ModelState);
            }

            // Clear ID to ensure new object
            subject.Id = null;

            var createdSubject = await _authorizationService.CreateSubjectAsync(subject);

            _logger.LogDebug(
                "Successfully created subject: {Name} with ID: {Id}",
                createdSubject.Name,
                createdSubject.Id
            );
            return CreatedAtAction(nameof(GetAllSubjects), createdSubject);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating subject: {Name}", subject.Name);
            return StatusCode(500, "Internal server error");
        }
    }

    /// <summary>
    /// Update an existing subject
    /// </summary>
    /// <param name="subject">Subject to update</param>
    /// <returns>Updated subject</returns>
    [HttpPut("subjects")]
    [RequireAdmin]
    [NightscoutEndpoint("/api/v2/authorization/subjects")]
    [RemoteCommand(Invalidates = ["GetAllSubjects"])]
    [ProducesResponseType(typeof(Subject), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(401)]
    [ProducesResponseType(403)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<Subject>> UpdateSubject([FromBody] Subject subject)
    {
        _logger.LogDebug("Update subject requested: {Id}", subject.Id);

        try
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(ModelState);
            }

            if (string.IsNullOrEmpty(subject.Id))
            {
                return BadRequest("Subject ID is required for update");
            }

            var updatedSubject = await _authorizationService.UpdateSubjectAsync(subject);

            if (updatedSubject == null)
            {
                _logger.LogDebug("Subject not found for update: {Id}", subject.Id);
                return NotFound();
            }

            _logger.LogDebug("Successfully updated subject: {Id}", updatedSubject.Id);
            return Ok(updatedSubject);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating subject: {Id}", subject.Id);
            return StatusCode(500, "Internal server error");
        }
    }

    /// <summary>
    /// Delete a subject by ID
    /// </summary>
    /// <param name="id">Subject ID to delete</param>
    /// <returns>Success response</returns>
    [HttpDelete("subjects/{id}")]
    [RequireAdmin]
    [NightscoutEndpoint("/api/v2/authorization/subjects/:id")]
    [RemoteCommand(Invalidates = ["GetAllSubjects"])]
    [ProducesResponseType(204)]
    [ProducesResponseType(401)]
    [ProducesResponseType(403)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> DeleteSubject(string id)
    {
        _logger.LogDebug("Delete subject requested: {Id}", id);

        try
        {
            var deleted = await _authorizationService.DeleteSubjectAsync(id);

            if (!deleted)
            {
                _logger.LogDebug("Subject not found for deletion: {Id}", id);
                return NotFound();
            }

            _logger.LogDebug("Successfully deleted subject: {Id}", id);
            return NoContent();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting subject: {Id}", id);
            return StatusCode(500, "Internal server error");
        }
    }

    // Role management endpoints
    /// <summary>
    /// Get all roles
    /// </summary>
    /// <returns>List of all roles</returns>
    [HttpGet("roles")]
    [RequireAdmin]
    [NightscoutEndpoint("/api/v2/authorization/roles")]
    [RemoteQuery]
    [ProducesResponseType(typeof(List<Role>), 200)]
    [ProducesResponseType(401)]
    [ProducesResponseType(403)]
    public async Task<ActionResult<List<Role>>> GetAllRoles()
    {
        _logger.LogDebug("Get all roles requested");

        try
        {
            var roles = await _authorizationService.GetAllRolesAsync();

            _logger.LogDebug("Successfully returned {Count} roles", roles.Count);
            return Ok(roles);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting all roles");
            return StatusCode(500, "Internal server error");
        }
    }

    /// <summary>
    /// Create a new role
    /// </summary>
    /// <param name="role">Role to create</param>
    /// <returns>Created role</returns>
    [HttpPost("roles")]
    [RequireAdmin]
    [NightscoutEndpoint("/api/v2/authorization/roles")]
    [RemoteCommand(Invalidates = ["GetAllRoles"])]
    [ProducesResponseType(typeof(Role), 201)]
    [ProducesResponseType(400)]
    [ProducesResponseType(401)]
    [ProducesResponseType(403)]
    public async Task<ActionResult<Role>> CreateRole([FromBody] Role role)
    {
        _logger.LogDebug("Create role requested: {Name}", role.Name);

        try
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(ModelState);
            }

            // Clear ID to ensure new object
            role.Id = null;

            var createdRole = await _authorizationService.CreateRoleAsync(role);

            _logger.LogDebug(
                "Successfully created role: {Name} with ID: {Id}",
                createdRole.Name,
                createdRole.Id
            );
            return CreatedAtAction(nameof(GetAllRoles), createdRole);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating role: {Name}", role.Name);
            return StatusCode(500, "Internal server error");
        }
    }

    /// <summary>
    /// Update an existing role
    /// </summary>
    /// <param name="role">Role to update</param>
    /// <returns>Updated role</returns>
    [HttpPut("roles")]
    [RequireAdmin]
    [NightscoutEndpoint("/api/v2/authorization/roles")]
    [RemoteCommand(Invalidates = ["GetAllRoles"])]
    [ProducesResponseType(typeof(Role), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(401)]
    [ProducesResponseType(403)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<Role>> UpdateRole([FromBody] Role role)
    {
        _logger.LogDebug("Update role requested: {Id}", role.Id);

        try
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(ModelState);
            }

            if (string.IsNullOrEmpty(role.Id))
            {
                return BadRequest("Role ID is required for update");
            }

            var updatedRole = await _authorizationService.UpdateRoleAsync(role);

            if (updatedRole == null)
            {
                _logger.LogDebug("Role not found for update: {Id}", role.Id);
                return NotFound();
            }

            _logger.LogDebug("Successfully updated role: {Id}", updatedRole.Id);
            return Ok(updatedRole);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating role: {Id}", role.Id);
            return StatusCode(500, "Internal server error");
        }
    }

    /// <summary>
    /// Delete a role by ID
    /// </summary>
    /// <param name="id">Role ID to delete</param>
    /// <returns>Success response</returns>
    [HttpDelete("roles/{id}")]
    [RequireAdmin]
    [NightscoutEndpoint("/api/v2/authorization/roles/:id")]
    [RemoteCommand(Invalidates = ["GetAllRoles"])]
    [ProducesResponseType(204)]
    [ProducesResponseType(401)]
    [ProducesResponseType(403)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> DeleteRole(string id)
    {
        _logger.LogDebug("Delete role requested: {Id}", id);

        try
        {
            var deleted = await _authorizationService.DeleteRoleAsync(id);

            if (!deleted)
            {
                _logger.LogDebug("Role not found for deletion: {Id}", id);
                return NotFound();
            }

            _logger.LogDebug("Successfully deleted role: {Id}", id);
            return NoContent();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting role: {Id}", id);
            return StatusCode(500, "Internal server error");
        }
    }
}
