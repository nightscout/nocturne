using Microsoft.AspNetCore.Mvc;
using OpenApi.Remote.Attributes;
using Nocturne.API.Extensions;
using Nocturne.API.Middleware.Handlers;
using Nocturne.API.Services.Auth;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data;

namespace Nocturne.API.Controllers.Authentication;

/// <summary>
/// Controller for managing direct grant tokens (programmatic API tokens without an OAuth client).
/// These tokens use the <c>noc_</c> prefix and are validated by <see cref="DirectGrantTokenHandler"/>.
/// </summary>
/// <remarks>
/// Direct grants are bearer tokens tied to a specific user but issued outside the standard
/// OAuth 2.0 consent flow. They are intended for scripts, automation, and server-to-server
/// integrations where launching an authorization-code flow is impractical.
///
/// Token generation uses <see cref="System.Security.Cryptography.RandomNumberGenerator"/> to produce
/// 32 bytes of entropy encoded as a Base64-URL string. Only the SHA-256 hash of the token is stored
/// (<see cref="DirectGrantTokenHandler.ComputeSha256Hex"/>); the plaintext is returned once at
/// creation and cannot be retrieved again.
///
/// Scopes are validated and normalized via <see cref="OAuthScopes.Normalize"/> before storage.
/// All mutations are audit-logged through <see cref="IAuthAuditService"/>.
/// </remarks>
/// <seealso cref="DirectGrantTokenHandler"/>
/// <seealso cref="IDirectGrantService"/>
/// <seealso cref="OAuthScopes"/>
[ApiController]
[Route("api/auth/direct-grants")]
[Tags("Authentication")]
public class DirectGrantController : ControllerBase
{
    private readonly NocturneDbContext _dbContext;
    private readonly IDirectGrantService _directGrantService;

    /// <summary>
    /// Initializes a new instance of the <see cref="DirectGrantController"/> class.
    /// </summary>
    /// <param name="dbContext">The application database context used to read and write grant records.</param>
    /// <param name="directGrantService">The service that creates, lists, and revokes direct grants.</param>
    public DirectGrantController(
        NocturneDbContext dbContext,
        IDirectGrantService directGrantService)
    {
        _dbContext = dbContext;
        _directGrantService = directGrantService;
    }

    /// <summary>
    /// Create a new direct grant token. The plaintext token is returned once and cannot be retrieved again.
    /// </summary>
    /// <param name="request">The create request containing the human-readable label, desired scopes, and optional expiry.</param>
    /// <returns>A <see cref="CreateDirectGrantResponse"/> containing the grant ID and the single-use plaintext token.</returns>
    [HttpPost]
    [RemoteCommand(Invalidates = ["List"])]
    [ProducesResponseType(typeof(CreateDirectGrantResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<CreateDirectGrantResponse>> Create([FromBody] CreateDirectGrantRequest request)
    {
        var auth = HttpContext.GetAuthContext();
        if (auth == null || !auth.IsAuthenticated || auth.SubjectId == null)
        {
            return Problem(detail: "Authentication required", statusCode: 401, title: "Unauthorized");
        }

        var result = await _directGrantService.CreateAsync(
            _dbContext, auth.SubjectId.Value, request.Label, request.Scopes, request.ExpiresAt,
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            Request.Headers.UserAgent.ToString(),
            ct: HttpContext.RequestAborted);

        if (result.Error != null)
        {
            return Problem(detail: result.Error, statusCode: 400, title: "Bad Request");
        }

        return Ok(result.Response);
    }

    /// <summary>
    /// List all active direct grants for the authenticated user.
    /// Never returns the token itself.
    /// </summary>
    /// <returns>A list of <see cref="DirectGrantDto"/> objects representing non-revoked grants for the current user.</returns>
    [HttpGet]
    [RemoteQuery]
    [ProducesResponseType(typeof(List<DirectGrantDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<List<DirectGrantDto>>> List()
    {
        var auth = HttpContext.GetAuthContext();
        if (auth == null || !auth.IsAuthenticated || auth.SubjectId == null)
        {
            return Problem(detail: "Authentication required", statusCode: 401, title: "Unauthorized");
        }

        var grants = await _directGrantService.ListAsync(
            _dbContext, auth.SubjectId.Value, HttpContext.RequestAborted);

        return Ok(grants);
    }

    /// <summary>
    /// Revoke a direct grant by setting its <c>RevokedAt</c> timestamp. This operation is idempotent.
    /// </summary>
    /// <param name="id">The GUID of the grant to revoke.</param>
    /// <returns><c>204 No Content</c> on success (including when already revoked); <c>404 Not Found</c> if the grant does not belong to the current user.</returns>
    [HttpDelete("{id:guid}")]
    [RemoteCommand(Invalidates = ["List"])]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Revoke(Guid id)
    {
        var auth = HttpContext.GetAuthContext();
        if (auth == null || !auth.IsAuthenticated || auth.SubjectId == null)
        {
            return Problem(detail: "Authentication required", statusCode: 401, title: "Unauthorized");
        }

        var found = await _directGrantService.RevokeAsync(
            _dbContext, id, auth.SubjectId.Value,
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            Request.Headers.UserAgent.ToString(),
            ct: HttpContext.RequestAborted);

        if (!found)
        {
            return Problem(detail: "Direct grant not found", statusCode: 404, title: "Not Found");
        }

        return NoContent();
    }
}

#region Request/Response DTOs

/// <summary>
/// Request to create a new direct grant
/// </summary>
public class CreateDirectGrantRequest
{
    public string Label { get; set; } = string.Empty;
    public List<string> Scopes { get; set; } = new();
    public DateTime? ExpiresAt { get; set; }
}

/// <summary>
/// Response containing the newly created direct grant and plaintext token
/// </summary>
public class CreateDirectGrantResponse
{
    public Guid Id { get; set; }
    public string Token { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public List<string> Scopes { get; set; } = new();
    public DateTime CreatedAt { get; set; }
    public DateTime? ExpiresAt { get; set; }
}

/// <summary>
/// Direct grant information (never includes the token)
/// </summary>
public class DirectGrantDto
{
    public Guid Id { get; set; }
    public Guid SubjectId { get; set; }
    public string Label { get; set; } = string.Empty;
    public List<string> Scopes { get; set; } = new();
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// When the grant stops authenticating. Null means it never does.
    /// </summary>
    public DateTime? ExpiresAt { get; set; }

    public DateTime? LastUsedAt { get; set; }

    /// <summary>
    /// True when this grant was created from a migrated Nightscout API secret
    /// rather than as a scoped <c>noc_</c> token.
    /// </summary>
    public bool IsLegacy { get; set; }
}

#endregion
