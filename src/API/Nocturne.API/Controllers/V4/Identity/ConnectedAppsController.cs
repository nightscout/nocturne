using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Extensions;
using OpenApi.Remote.Attributes;
using Nocturne.Core.Models.Authorization;

namespace Nocturne.API.Controllers.V4.Identity;

/// <summary>
/// Manages OAuth app grants ("connected apps") for the authenticated user.
/// Filters to grant_type='app' so direct/follower grants are managed elsewhere.
/// </summary>
/// <seealso cref="IOAuthGrantService"/>
/// <seealso cref="IOAuthTokenService"/>
[ApiController]
[Tags("Identity")]
[Route("api/v4/account/connected-apps")]
[Authorize]
[Produces("application/json")]
public class ConnectedAppsController : ControllerBase
{
    private readonly IOAuthGrantService _grantService;
    private readonly IOAuthTokenService _tokenService;
    private readonly ILogger<ConnectedAppsController> _logger;

    /// <summary>
    /// Creates a new instance of ConnectedAppsController.
    /// </summary>
    public ConnectedAppsController(
        IOAuthGrantService grantService,
        IOAuthTokenService tokenService,
        ILogger<ConnectedAppsController> logger)
    {
        _grantService = grantService;
        _tokenService = tokenService;
        _logger = logger;
    }

    /// <summary>
    /// List all connected apps (OAuth app grants) for the authenticated user
    /// on the current tenant.
    /// </summary>
    /// <inheritdoc cref="IOAuthGrantService.GetGrantsForSubjectAsync"/>
    [HttpGet]
    [RemoteQuery]
    [ProducesResponseType(typeof(List<ConnectedAppDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<List<ConnectedAppDto>>> List(CancellationToken ct)
    {
        var subjectId = HttpContext.GetSubjectId();
        if (subjectId is null)
        {
            return Unauthorized();
        }

        var grants = await _grantService.GetGrantsForSubjectAsync(subjectId.Value, ct);

        var result = grants
            .Where(g => g.GrantType == OAuthScopes.GrantTypeApp && !g.IsRevoked)
            .OrderByDescending(g => g.LastUsedAt ?? g.CreatedAt)
            .Select(g => new ConnectedAppDto
            {
                GrantId = g.Id,
                ClientId = g.ClientId,
                ClientName = g.ClientDisplayName,
                ClientUri = g.ClientUri,
                LogoUri = g.LogoUri,
                IsVerified = g.IsKnownClient,
                Scopes = g.Scopes,
                Label = g.Label,
                CreatedAt = g.CreatedAt,
                LastUsedAt = g.LastUsedAt,
            })
            .ToList();

        return Ok(result);
    }

    /// <summary>
    /// Revoke a connected app. Soft-deletes the grant and invalidates all
    /// associated refresh tokens; previously-issued access tokens become
    /// unusable on next request via the revocation cache.
    /// </summary>
    /// <inheritdoc cref="IOAuthGrantService.RevokeGrantAsync"/>
    [HttpDelete("{grantId}")]
    [RemoteCommand(Invalidates = ["List"])]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> Revoke(Guid grantId, CancellationToken ct)
    {
        var subjectId = HttpContext.GetSubjectId();
        if (subjectId is null)
        {
            return Unauthorized();
        }

        // Verify ownership: the grant must belong to this subject on this tenant.
        var grants = await _grantService.GetGrantsForSubjectAsync(subjectId.Value, ct);
        var grant = grants.FirstOrDefault(g =>
            g.Id == grantId && g.GrantType == OAuthScopes.GrantTypeApp && !g.IsRevoked);

        if (grant is null)
        {
            return NotFound();
        }

        await _grantService.RevokeGrantAsync(grantId, ct);

        _logger.LogInformation(
            "Connected app revoked: grant_id={GrantId} subject_id={SubjectId} client_id={ClientId}",
            grantId, subjectId, grant.ClientId);

        return NoContent();
    }
}

/// <summary>
/// Connected app DTO returned by GET /api/v4/account/connected-apps.
/// </summary>
public class ConnectedAppDto
{
    [JsonPropertyName("grantId")]
    public Guid GrantId { get; set; }

    [JsonPropertyName("clientId")]
    public string ClientId { get; set; } = "";

    [JsonPropertyName("clientName")]
    public string? ClientName { get; set; }

    [JsonPropertyName("clientUri")]
    public string? ClientUri { get; set; }

    [JsonPropertyName("logoUri")]
    public string? LogoUri { get; set; }

    [JsonPropertyName("isVerified")]
    public bool IsVerified { get; set; }

    [JsonPropertyName("scopes")]
    public List<string> Scopes { get; set; } = [];

    [JsonPropertyName("label")]
    public string? Label { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; }

    [JsonPropertyName("lastUsedAt")]
    public DateTime? LastUsedAt { get; set; }
}
