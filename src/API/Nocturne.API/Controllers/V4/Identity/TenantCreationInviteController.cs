using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Nocturne.API.Attributes;
using Nocturne.API.Authorization;
using Nocturne.API.Extensions;
using Nocturne.Core.Contracts.Multitenancy;
using OpenApi.Remote.Attributes;

namespace Nocturne.API.Controllers.V4.Identity;

/// <summary>
/// What the holder of a tenant creation invite uses, on the bare domain and without a tenant. A
/// visitor without an account creates one through <c>PasskeyController.TenantInviteOptions</c>; a
/// signed-in visitor redeems here.
/// </summary>
[ApiController]
[Tags("Identity")]
[Route("api/v4/tenant-invites")]
[Produces("application/json")]
public class TenantCreationInviteController(
    ITenantCreationInviteService invites,
    ITenantService tenantService) : ControllerBase
{
    /// <summary>
    /// What a usable link offers, and who is signed in on this browser, if anyone. An unusable link
    /// answers with the reason alone.
    /// </summary>
    [HttpGet("{token}/info")]
    [AllowAnonymous]
    [DenyDemoSubject]
    [EnableRateLimiting("invite-lookup")]
    [RemoteQuery]
    [ProducesResponseType(typeof(TenantCreationInviteViewInfo), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetInfo([CredentialRouteValue] string token, CancellationToken ct)
    {
        var outcome = await invites.GetInfoAsync(token, ct);
        if (outcome.Refusal is { } refusal)
            return this.ToResult(refusal);

        var auth = HttpContext.GetAuthContext();
        var viewer = auth?.SubjectId is not null ? auth.SubjectName : null;

        return Ok(new TenantCreationInviteViewInfo(outcome.Value!, auth?.SubjectId is not null, viewer));
    }

    /// <summary>
    /// Whether <paramref name="slug"/> is free for a new tenant. Answers only for a usable link,
    /// so the check is not open to anyone without one.
    /// </summary>
    [HttpGet("{token}/validate-slug")]
    [AllowAnonymous]
    [DenyDemoSubject]
    [EnableRateLimiting("name-availability")]
    [RemoteQuery]
    [ProducesResponseType(typeof(SlugValidationResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ValidateSlug(
        [CredentialRouteValue] string token, [FromQuery] string slug, CancellationToken ct)
    {
        var outcome = await invites.GetInfoAsync(token, ct);
        if (outcome.Refusal is { } refusal)
            return this.ToResult(refusal);

        if (string.IsNullOrWhiteSpace(slug))
            return Ok(new SlugValidationResult(false, "Slug is required"));

        return Ok(await tenantService.ValidateSlugAsync(slug, ct));
    }

    /// <summary>Creates a tenant owned by the signed-in caller, taking one use of the link.</summary>
    [HttpPost("{token}/redeem")]
    [Authorize]
    [DenyDemoSubject]
    [EnableRateLimiting("passkey-register")]
    [RemoteCommand]
    [ProducesResponseType(typeof(TenantCreationInviteRedeemed), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Redeem(
        [CredentialRouteValue] string token, [FromBody] RedeemTenantCreationInviteRequest request, CancellationToken ct)
    {
        if (HttpContext.GetSubjectId() is not { } subjectId)
            return Unauthorized();

        var slugCheck = await tenantService.ValidateSlugAsync(request.Slug, ct);
        if (!slugCheck.IsValid)
            return Problem(detail: slugCheck.Message, statusCode: StatusCodes.Status400BadRequest, title: "Bad Request");

        var claim = await invites.ClaimAsync(token, ct);
        if (claim.Refusal is { } refusal)
            return this.ToResult(refusal);

        var created = await invites.CreateTenantAsync(
            claim.Value, request.Slug, request.SiteName, subjectId, ownerUsername: null, ct);

        return created.Refusal is { } createRefusal ? this.ToResult(createRefusal) : Ok(created.Value);
    }
}

/// <param name="Invite">What the link offers.</param>
/// <param name="SignedIn">Whether this browser already holds a session.</param>
/// <param name="ViewerName">The signed-in person's name, when <paramref name="SignedIn"/>.</param>
public record TenantCreationInviteViewInfo(TenantCreationInviteInfo Invite, bool SignedIn, string? ViewerName);

/// <param name="Slug">The subdomain the new tenant is served at.</param>
/// <param name="SiteName">The new tenant's display name.</param>
public record RedeemTenantCreationInviteRequest(string Slug, string SiteName);
