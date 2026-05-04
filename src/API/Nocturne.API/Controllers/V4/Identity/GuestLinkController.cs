using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using OpenApi.Remote.Attributes;
using Nocturne.API.Extensions;
using Nocturne.API.Middleware.Handlers;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Models.Authorization;

namespace Nocturne.API.Controllers.V4.Identity;

/// <summary>
/// Manages temporary guest access links for read-only data sharing.
/// </summary>
[ApiController]
[Route("api/v4/guest-links")]
[Produces("application/json")]
public class GuestLinkController : ControllerBase
{
    private readonly IGuestLinkService _guestLinkService;
    private readonly GuestSessionHandler _guestSessionHandler;

    /// <summary>
    /// Initializes a new instance of <see cref="GuestLinkController"/>.
    /// </summary>
    public GuestLinkController(
        IGuestLinkService guestLinkService,
        GuestSessionHandler guestSessionHandler)
    {
        _guestLinkService = guestLinkService;
        _guestSessionHandler = guestSessionHandler;
    }

    /// <summary>
    /// Create a new guest link for temporary read-only data sharing.
    /// </summary>
    [HttpPost]
    [Authorize]
    [RemoteCommand]
    [ProducesResponseType(typeof(GuestLinkCreationResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CreateGuestLink(
        [FromBody] CreateGuestLinkRequest request,
        CancellationToken ct)
    {
        var auth = HttpContext.GetAuthContext();
        if (auth is not { IsAuthenticated: true, SubjectId: not null })
            return Unauthorized();

        if (!HasPermission(TenantPermissions.SharingGuest)
            && auth.SubjectId != auth.EffectiveSubjectId)
            return Forbid();

        var effectiveSubjectId = auth.EffectiveSubjectId!.Value;
        var baseUrl = $"{Request.Scheme}://{Request.Host}";

        try
        {
            var result = await _guestLinkService.CreateGuestLinkAsync(
                effectiveSubjectId,
                auth.SubjectId.Value,
                request.Label,
                baseUrl,
                request.Scopes,
                ct);

            return Ok(result);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// List guest links for the current user's effective subject.
    /// </summary>
    [HttpGet]
    [Authorize]
    [RemoteQuery]
    [ProducesResponseType(typeof(IReadOnlyList<GuestLinkInfo>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetGuestLinks(CancellationToken ct)
    {
        var auth = HttpContext.GetAuthContext();
        if (auth is not { IsAuthenticated: true })
            return Unauthorized();

        var effectiveSubjectId = auth.EffectiveSubjectId;
        if (effectiveSubjectId is null)
            return Unauthorized();

        var links = await _guestLinkService.GetGuestLinksAsync(effectiveSubjectId.Value, ct);
        return Ok(links);
    }

    /// <summary>
    /// Revoke an active guest link.
    /// </summary>
    [HttpDelete("{grantId:guid}")]
    [Authorize]
    [RemoteCommand(Invalidates = ["GetGuestLinks"])]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RevokeGuestLink(Guid grantId, CancellationToken ct)
    {
        var auth = HttpContext.GetAuthContext();
        if (auth is not { IsAuthenticated: true, SubjectId: not null })
            return Unauthorized();

        var result = await _guestLinkService.RevokeAsync(grantId, auth.SubjectId.Value, ct);
        if (!result)
            return NotFound();

        return NoContent();
    }

    /// <summary>
    /// Activate a guest link by code and receive a session cookie.
    /// </summary>
    [HttpPost("activate")]
    [AllowAnonymous]
    [EnableRateLimiting("guest-activate")]
    [ProducesResponseType(typeof(ActivateGuestLinkResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ActivateGuestLinkResponse), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ActivateGuestLink(
        [FromBody] ActivateGuestLinkRequest request,
        CancellationToken ct)
    {
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
        var userAgent = Request.Headers.UserAgent.FirstOrDefault();

        var result = await _guestLinkService.ActivateAsync(request.Code, ip, userAgent, ct);

        if (!result.Success || result.Session is null)
            return BadRequest(new ActivateGuestLinkResponse(null, result.Error));

        _guestSessionHandler.SetGuestSessionCookie(
            HttpContext,
            result.Session.GrantId,
            result.Session.ExpiresAt);

        return Ok(new ActivateGuestLinkResponse(result.Session.ExpiresAt, null));
    }

    private bool HasPermission(string permission)
    {
        var grantedScopes = HttpContext.Items["GrantedScopes"] as IReadOnlySet<string>;
        if (grantedScopes == null) return false;
        return TenantPermissions.HasPermission(grantedScopes, permission);
    }
}

/// <summary>
/// Request body for creating a guest link.
/// </summary>
public record CreateGuestLinkRequest(string Label, List<string>? Scopes = null);

/// <summary>
/// Request body for activating a guest link.
/// </summary>
public record ActivateGuestLinkRequest(string Code);

/// <summary>
/// Response from guest link activation.
/// </summary>
public record ActivateGuestLinkResponse(DateTime? ExpiresAt, string? Error);
