using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Attributes;
using Nocturne.API.Authorization;
using OpenApi.Remote.Attributes;
using Nocturne.Core.Contracts.Identity;
using Nocturne.Core.Models;
using IAuthorizationService = Nocturne.Core.Contracts.Identity.IAuthorizationService;

namespace Nocturne.API.Controllers.V2;

/// <summary>
/// Authorization controller that provides 1:1 compatibility with Nightscout authorization endpoints.
/// </summary>
/// <seealso cref="IAuthorizationService"/>
[ApiController]
[Tags("V2")]
[Route("api/v2/authorization")]
[Authorize]
public class AuthorizationController : ControllerBase
{
    private readonly IAuthorizationService _authorizationService;
    private readonly ILogger<AuthorizationController> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="AuthorizationController"/>.
    /// </summary>
    /// <param name="authorizationService">Service handling JWT token generation and subject permissions.</param>
    /// <param name="logger">Logger instance.</param>
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
    /// <remarks>
    /// The access token in the URL is the caller's only credential — this is the bootstrap
    /// that exchanges it for a JWT (used by NSClientV3/AAPS). It must be anonymous: the
    /// class-level <c>[Authorize]</c> would otherwise reject the request with 401 before the
    /// method can validate the token. The action validates the token itself and returns 401
    /// when it is missing or invalid.
    ///
    /// Accepts both legacy subject access tokens (<c>name-hexstring</c>, resolved against
    /// subjects) and <c>noc_</c> direct-grant tokens (resolved against oauth_grants; the
    /// minted JWT carries the grant's scopes and tenant pin).
    /// </remarks>
    [AllowAnonymous]
    [HttpGet("request/{accessToken}")]
    [NightscoutEndpoint("/api/v2/authorization/request/:accessToken")]
    [ProducesResponseType(typeof(AuthorizationResponse), 200)]
    [ProducesResponseType(401)]
    [ErrorEnvelope]
    public async Task<ActionResult<AuthorizationResponse>> GenerateJwtFromAccessToken(
        string accessToken
    )
    {
        _logger.LogDebug("JWT generation requested for access token");

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

    /// <summary>
    /// Get all permissions that have been seen by the system
    /// </summary>
    /// <returns>List of permissions with usage statistics</returns>
    [HttpGet("permissions")]
    [NightscoutEndpoint("/api/v2/authorization/permissions")]
    [RemoteQuery]
    [ProducesResponseType(typeof(PermissionsResponse), 200)]
    [ErrorEnvelope]
    public async Task<ActionResult<PermissionsResponse>> GetAllPermissions()
    {
        _logger.LogDebug("All permissions requested");

        var result = await _authorizationService.GetAllPermissionsAsync();

        _logger.LogDebug("Successfully returned {Count} permissions", result.Permissions.Count);
        return Ok(result);
    }

    /// <summary>
    /// Get permission hierarchy structure as a trie
    /// </summary>
    /// <returns>Permission trie structure</returns>
    [HttpGet("permissions/trie")]
    [NightscoutEndpoint("/api/v2/authorization/permissions/trie")]
    [RemoteQuery]
    [ProducesResponseType(typeof(PermissionTrieResponse), 200)]
    [ErrorEnvelope]
    public async Task<ActionResult<PermissionTrieResponse>> GetPermissionTrie()
    {
        _logger.LogDebug("Permission trie structure requested");

        var result = await _authorizationService.GetPermissionTrieAsync();

        _logger.LogDebug(
            "Successfully returned permission trie with {Count} permissions",
            result.Count
        );
        return Ok(result);
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
    [ErrorEnvelope]
    public async Task<ActionResult<List<Subject>>> GetAllSubjects()
    {
        _logger.LogDebug("Get all subjects requested");

        var subjects = await _authorizationService.GetAllSubjectsAsync();

        _logger.LogDebug("Successfully returned {Count} subjects", subjects.Count);
        return Ok(subjects);
    }

    /// <summary>
    /// Create a new subject
    /// </summary>
    /// <param name="subject">Subject to create</param>
    /// <returns>Created subject</returns>
    [HttpPost("subjects")]
    [RequireAdmin]
    // A subject minted here carries a plaintext access token and any global role named in
    // the body, and its own is_demo_subject is false — so without this the shared demo
    // account could launder itself into an unflagged credential that [DenyDemoSubject]
    // waves through.
    [DenyDemoSubject]
    [NightscoutEndpoint("/api/v2/authorization/subjects")]
    [RemoteForm(Invalidates = ["GetAllSubjects"])]
    [ProducesResponseType(typeof(Subject), 201)]
    [ProducesResponseType(400)]
    [ProducesResponseType(401)]
    [ProducesResponseType(403)]
    [ErrorEnvelope]
    public async Task<ActionResult<Subject>> CreateSubject([FromBody] Subject subject)
    {
        _logger.LogDebug("Create subject requested: {Name}", subject.Name);

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

    /// <summary>
    /// Update an existing subject
    /// </summary>
    /// <param name="subject">Subject to update</param>
    /// <returns>Updated subject</returns>
    [HttpPut("subjects")]
    [RequireAdmin]
    [NightscoutEndpoint("/api/v2/authorization/subjects")]
    [RemoteForm(Invalidates = ["GetAllSubjects"])]
    [ProducesResponseType(typeof(Subject), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(401)]
    [ProducesResponseType(403)]
    [ProducesResponseType(404)]
    [ErrorEnvelope]
    public async Task<ActionResult<Subject>> UpdateSubject([FromBody] Subject subject)
    {
        _logger.LogDebug("Update subject requested: {Id}", subject.Id);

        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        if (string.IsNullOrEmpty(subject.Id))
        {
            return Problem(detail: "Subject ID is required for update", statusCode: 400, title: "Bad Request");
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
    [ErrorEnvelope]
    public async Task<IActionResult> DeleteSubject(string id)
    {
        _logger.LogDebug("Delete subject requested: {Id}", id);

        var deleted = await _authorizationService.DeleteSubjectAsync(id);

        if (!deleted)
        {
            _logger.LogDebug("Subject not found for deletion: {Id}", id);
            return NotFound();
        }

        _logger.LogDebug("Successfully deleted subject: {Id}", id);
        return NoContent();
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
    [ErrorEnvelope]
    public async Task<ActionResult<List<Role>>> GetAllRoles()
    {
        _logger.LogDebug("Get all roles requested");

        var roles = await _authorizationService.GetAllRolesAsync();

        _logger.LogDebug("Successfully returned {Count} roles", roles.Count);
        return Ok(roles);
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
    [ErrorEnvelope]
    public async Task<ActionResult<Role>> CreateRole([FromBody] Role role)
    {
        _logger.LogDebug("Create role requested: {Name}", role.Name);

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
    [ErrorEnvelope]
    public async Task<ActionResult<Role>> UpdateRole([FromBody] Role role)
    {
        _logger.LogDebug("Update role requested: {Id}", role.Id);

        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        if (string.IsNullOrEmpty(role.Id))
        {
            return Problem(detail: "Role ID is required for update", statusCode: 400, title: "Bad Request");
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
    [ErrorEnvelope]
    public async Task<IActionResult> DeleteRole(string id)
    {
        _logger.LogDebug("Delete role requested: {Id}", id);

        var deleted = await _authorizationService.DeleteRoleAsync(id);

        if (!deleted)
        {
            _logger.LogDebug("Role not found for deletion: {Id}", id);
            return NotFound();
        }

        _logger.LogDebug("Successfully deleted role: {Id}", id);
        return NoContent();
    }
}
