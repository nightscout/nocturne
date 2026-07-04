using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Nocturne.API.Extensions;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Models.Configuration;
using OpenApi.Remote.Attributes;

namespace Nocturne.API.Controllers.V4.Identity;

/// <summary>
/// Manages the authenticated user's web login sessions. A session is one login/device —
/// a refresh-token rotation chain sharing a single OIDC session ID — so revoking a
/// session logs that device out everywhere its tokens are used.
/// </summary>
/// <seealso cref="IRefreshTokenService"/>
[ApiController]
[Tags("Identity")]
[Route("api/v4/account/sessions")]
[Authorize]
[Produces("application/json")]
public class SessionsController : ControllerBase
{
    private readonly IRefreshTokenService _refreshTokenService;
    private readonly OidcOptions _options;
    private readonly ILogger<SessionsController> _logger;

    /// <summary>
    /// Creates a new instance of SessionsController.
    /// </summary>
    public SessionsController(
        IRefreshTokenService refreshTokenService,
        IOptions<OidcOptions> options,
        ILogger<SessionsController> logger)
    {
        _refreshTokenService = refreshTokenService;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// List the authenticated user's active login sessions, most recently active first.
    /// </summary>
    /// <inheritdoc cref="IRefreshTokenService.GetActiveSessionsForSubjectAsync"/>
    [HttpGet]
    [RemoteQuery]
    [ProducesResponseType(typeof(List<SessionDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<List<SessionDto>>> List(CancellationToken ct)
    {
        var subjectId = HttpContext.GetSubjectId();
        if (subjectId is null)
        {
            return Unauthorized();
        }

        var currentSessionId = await GetCurrentSessionIdAsync();
        var sessions = await _refreshTokenService.GetActiveSessionsForSubjectAsync(subjectId.Value);

        var result = sessions
            .Select(s => new SessionDto
            {
                SessionId = s.OidcSessionId ?? s.Id.ToString(),
                DeviceDescription = s.DeviceDescription,
                IpAddress = s.IpAddress,
                LastActiveAt = s.LastUsedAt ?? s.IssuedAt,
                ExpiresAt = s.ExpiresAt,
                IsCurrent = s.OidcSessionId == currentSessionId,
            })
            .ToList();

        return Ok(result);
    }

    /// <summary>
    /// Revoke a login session, logging that device out. Revoking the current
    /// session is allowed and acts as a logout.
    /// </summary>
    /// <inheritdoc cref="IRefreshTokenService.RevokeSessionForSubjectAsync"/>
    [HttpDelete("{sessionId}")]
    [RemoteCommand(Invalidates = ["List"])]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> Revoke(string sessionId, CancellationToken ct)
    {
        var subjectId = HttpContext.GetSubjectId();
        if (subjectId is null)
        {
            return Unauthorized();
        }

        // The revoke is subject-scoped, so a session ID belonging to another subject
        // matches nothing and reports 404 rather than leaking existence.
        var revoked = await _refreshTokenService.RevokeSessionForSubjectAsync(
            subjectId.Value, sessionId, "Revoked by user");

        if (revoked == 0)
        {
            return NotFound();
        }

        _logger.LogInformation(
            "Session revoked: session_id={SessionId} subject_id={SubjectId} tokens={Count}",
            sessionId, subjectId, revoked);

        return NoContent();
    }

    /// <summary>
    /// Revoke every session except the current one (log out everywhere else).
    /// </summary>
    /// <inheritdoc cref="IRefreshTokenService.RevokeOtherSessionsForSubjectAsync"/>
    [HttpPost("revoke-others")]
    [RemoteCommand(Invalidates = ["List"])]
    [ProducesResponseType(typeof(RevokeOthersResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<RevokeOthersResultDto>> RevokeOthers(CancellationToken ct)
    {
        var subjectId = HttpContext.GetSubjectId();
        if (subjectId is null)
        {
            return Unauthorized();
        }

        // Without a resolvable current session (e.g. bearer-token caller with no session
        // cookie), "everything else" would include every session — refuse instead.
        var currentSessionId = await GetCurrentSessionIdAsync();
        if (currentSessionId is null)
        {
            return BadRequest(new { error = "Current session could not be determined." });
        }

        var revoked = await _refreshTokenService.RevokeOtherSessionsForSubjectAsync(
            subjectId.Value, currentSessionId, "Revoked by user (logout everywhere else)");

        _logger.LogInformation(
            "Other sessions revoked: subject_id={SubjectId} tokens={Count}",
            subjectId, revoked);

        return Ok(new RevokeOthersResultDto { RevokedTokenCount = revoked });
    }

    /// <summary>
    /// Resolves the session key of the request's refresh-token cookie, or null when
    /// the request carries no known refresh token.
    /// </summary>
    private async Task<string?> GetCurrentSessionIdAsync()
    {
        var refreshToken = Request.Cookies[_options.Cookie.RefreshTokenName];
        if (string.IsNullOrEmpty(refreshToken))
        {
            return null;
        }

        return await _refreshTokenService.GetSessionIdForTokenAsync(refreshToken);
    }
}

/// <summary>
/// Login session DTO returned by GET /api/v4/account/sessions.
/// </summary>
public class SessionDto
{
    [JsonPropertyName("sessionId")]
    public string SessionId { get; set; } = "";

    [JsonPropertyName("deviceDescription")]
    public string? DeviceDescription { get; set; }

    [JsonPropertyName("ipAddress")]
    public string? IpAddress { get; set; }

    [JsonPropertyName("lastActiveAt")]
    public DateTime LastActiveAt { get; set; }

    [JsonPropertyName("expiresAt")]
    public DateTime ExpiresAt { get; set; }

    [JsonPropertyName("isCurrent")]
    public bool IsCurrent { get; set; }
}

/// <summary>
/// Result of POST /api/v4/account/sessions/revoke-others.
/// </summary>
public class RevokeOthersResultDto
{
    [JsonPropertyName("revokedTokenCount")]
    public int RevokedTokenCount { get; set; }
}
