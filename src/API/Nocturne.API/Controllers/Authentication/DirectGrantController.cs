using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OpenApi.Remote.Attributes;
using Nocturne.API.Extensions;
using Nocturne.API.Middleware.Handlers;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;

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
/// <seealso cref="IAuthAuditService"/>
/// <seealso cref="OAuthScopes"/>
[ApiController]
[Route("api/auth/direct-grants")]
[Tags("Authentication")]
public class DirectGrantController : ControllerBase
{
    private const string TokenPrefix = "noc_";
    private const int TokenRandomBytes = 32;

    private readonly NocturneDbContext _dbContext;
    private readonly IAuthAuditService _auditService;
    private readonly ILogger<DirectGrantController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="DirectGrantController"/> class.
    /// </summary>
    /// <param name="dbContext">The application database context used to read and write grant records.</param>
    /// <param name="auditService">The audit service for recording token issuance and revocation events.</param>
    /// <param name="logger">The logger.</param>
    public DirectGrantController(
        NocturneDbContext dbContext,
        IAuthAuditService auditService,
        ILogger<DirectGrantController> logger)
    {
        _dbContext = dbContext;
        _auditService = auditService;
        _logger = logger;
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

        if (string.IsNullOrWhiteSpace(request.Label))
        {
            return Problem(detail: "Label is required", statusCode: 400, title: "Bad Request");
        }

        if (request.Scopes == null || request.Scopes.Count == 0)
        {
            return Problem(detail: "At least one scope is required", statusCode: 400, title: "Bad Request");
        }

        var normalizedScopes = OAuthScopes.Normalize(request.Scopes).ToList();
        if (normalizedScopes.Count == 0)
        {
            return Problem(detail: "No valid scopes provided", statusCode: 400, title: "Bad Request");
        }

        // Generate opaque token
        var randomBytes = RandomNumberGenerator.GetBytes(TokenRandomBytes);
        var plaintextToken = TokenPrefix + Base64UrlEncode(randomBytes);
        var tokenHash = DirectGrantTokenHandler.ComputeSha256Hex(plaintextToken);

        var entity = new OAuthGrantEntity
        {
            Id = Guid.CreateVersion7(),
            ClientEntityId = null,
            SubjectId = auth.SubjectId.Value,
            GrantType = OAuthGrantTypes.Direct,
            Scopes = normalizedScopes,
            Label = request.Label,
            TokenHash = tokenHash,
            CreatedAt = DateTime.UtcNow,
        };

        if (request.ExpiresAt.HasValue)
        {
            // Store expiration as part of the grant metadata
            // Note: Direct grants don't have a built-in expiry field,
            // but we can track it in the label or via a separate mechanism
        }

        _dbContext.OAuthGrants.Add(entity);
        await _dbContext.SaveChangesAsync();

        _logger.LogInformation(
            "DirectGrantAudit: {Event} grant_id={GrantId} subject_id={SubjectId} scopes={Scopes}",
            "direct_grant_created", entity.Id, auth.SubjectId.Value, string.Join(" ", normalizedScopes));

        await _auditService.LogAsync(AuthAuditEventType.TokenIssued, auth.SubjectId.Value, success: true,
            ipAddress: HttpContext.Connection.RemoteIpAddress?.ToString(),
            userAgent: Request.Headers.UserAgent.ToString(),
            detailsJson: JsonSerializer.Serialize(new { method = "direct_grant", grant_id = entity.Id }));

        return Ok(new CreateDirectGrantResponse
        {
            Id = entity.Id,
            Token = plaintextToken,
            Label = entity.Label!,
            Scopes = normalizedScopes,
            CreatedAt = entity.CreatedAt,
        });
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

        var grants = await _dbContext.OAuthGrants
            .AsNoTracking()
            .Where(g => g.SubjectId == auth.SubjectId.Value
                     && g.GrantType == OAuthGrantTypes.Direct
                     && g.RevokedAt == null)
            .OrderByDescending(g => g.CreatedAt)
            .Select(g => new DirectGrantDto
            {
                Id = g.Id,
                Label = g.Label ?? string.Empty,
                Scopes = g.Scopes,
                CreatedAt = g.CreatedAt,
                LastUsedAt = g.LastUsedAt,
                IsLegacy = g.LegacySecretHash != null,
            })
            .ToListAsync();

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

        var grant = await _dbContext.OAuthGrants
            .Where(g => g.Id == id
                     && g.SubjectId == auth.SubjectId.Value
                     && g.GrantType == OAuthGrantTypes.Direct)
            .FirstOrDefaultAsync();

        if (grant == null)
        {
            return Problem(detail: "Direct grant not found", statusCode: 404, title: "Not Found");
        }

        if (grant.RevokedAt.HasValue)
        {
            return NoContent(); // Already revoked, idempotent
        }

        grant.RevokedAt = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync();

        _logger.LogInformation(
            "DirectGrantAudit: {Event} grant_id={GrantId} subject_id={SubjectId}",
            "direct_grant_revoked", id, auth.SubjectId.Value);

        await _auditService.LogAsync(AuthAuditEventType.TokenRevoked, auth.SubjectId.Value, success: true,
            ipAddress: HttpContext.Connection.RemoteIpAddress?.ToString(),
            userAgent: Request.Headers.UserAgent.ToString(),
            detailsJson: JsonSerializer.Serialize(new { grant_id = id }));

        return NoContent();
    }

    private static string Base64UrlEncode(byte[] bytes)
    {
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
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
}

/// <summary>
/// Direct grant information (never includes the token)
/// </summary>
public class DirectGrantDto
{
    public Guid Id { get; set; }
    public string Label { get; set; } = string.Empty;
    public List<string> Scopes { get; set; } = new();
    public DateTime CreatedAt { get; set; }
    public DateTime? LastUsedAt { get; set; }

    /// <summary>
    /// True when this grant was created from a migrated Nightscout API secret
    /// rather than as a scoped <c>noc_</c> token.
    /// </summary>
    public bool IsLegacy { get; set; }
}

#endregion
