using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpenApi.Remote.Attributes;
using Nocturne.API.Models.Responses;
using Nocturne.API.Services.Auth;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Core.Models.Authorization;
using Nocturne.API.Extensions;

namespace Nocturne.API.Controllers.V4.Identity;

/// <summary>
/// Manages the tenant's single rotatable public share link ({token}.share.{baseDomain}).
/// All operations require the <c>sharing.manage</c> permission.
/// </summary>
[ApiController]
[Tags("Identity")]
[Route("api/v4/share")]
[Produces("application/json")]
[Authorize]
public class ShareLinkController : ControllerBase
{
    private readonly IShareLinkService _shareLinkService;
    private readonly ITenantAccessor _tenantAccessor;
    private readonly IAuthAuditService _auditService;

    public ShareLinkController(
        IShareLinkService shareLinkService,
        ITenantAccessor tenantAccessor,
        IAuthAuditService auditService)
    {
        _shareLinkService = shareLinkService;
        _tenantAccessor = tenantAccessor;
        _auditService = auditService;
    }

    /// <summary>Get the current public share link state.</summary>
    [HttpGet]
    [RemoteQuery]
    [ProducesResponseType(typeof(ShareLinkDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<ShareLinkDto>> GetShareLink(CancellationToken ct)
    {
        if (!HttpContext.HasScope(Scope.SharingManage))
            return Forbid();

        return Ok(await _shareLinkService.GetAsync(_tenantAccessor.TenantId, ct));
    }

    /// <summary>
    /// Show the live link in the clear. A command rather than a query so the secret travels only
    /// when the owner asks for it, instead of on every read of the sharing settings. Succeeds
    /// with a null URL and <c>canReveal</c> false in the cases
    /// <see cref="TenantEntity.ShareTokenEncrypted"/> lists.
    /// </summary>
    [HttpPost("reveal")]
    [RemoteCommand]
    [ProducesResponseType(typeof(ShareLinkDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<ShareLinkDto>> RevealShareLink(CancellationToken ct)
    {
        if (!HttpContext.HasScope(Scope.SharingManage))
            return Forbid();

        var link = await _shareLinkService.RevealAsync(_tenantAccessor.TenantId, ct);

        // Logged on the outcome, not the request: a reveal that produced nothing gave nothing away.
        // Why it is audited at all: <see cref="AuthAuditEventType.ShareLinkRevealed"/>.
        if (link.Url != null)
        {
            await _auditService.LogAsync(
                AuthAuditEventType.ShareLinkRevealed,
                HttpContext.GetSubjectId(),
                success: true,
                ipAddress: HttpContext.Connection.RemoteIpAddress?.ToString(),
                userAgent: Request.Headers.UserAgent.ToString());
        }

        return Ok(link);
    }

    /// <summary>
    /// Mint a new share token — enables sharing, or rotates an existing link. The previous link
    /// stops working immediately.
    /// </summary>
    [HttpPost("rotate")]
    [RemoteCommand(Invalidates = ["GetShareLink"])]
    [ProducesResponseType(typeof(ShareLinkDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<ShareLinkDto>> RotateShareLink(CancellationToken ct)
    {
        if (!HttpContext.HasScope(Scope.SharingManage))
            return Forbid();

        return Ok(await _shareLinkService.RotateAsync(_tenantAccessor.TenantId, ct));
    }

    /// <summary>Disable public sharing and invalidate the current link.</summary>
    [HttpDelete]
    [RemoteCommand(Invalidates = ["GetShareLink"])]
    [ProducesResponseType(typeof(ShareLinkDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<ShareLinkDto>> DisableShareLink(CancellationToken ct)
    {
        if (!HttpContext.HasScope(Scope.SharingManage))
            return Forbid();

        return Ok(await _shareLinkService.DisableAsync(_tenantAccessor.TenantId, ct));
    }

    /// <summary>Choose whether the public view shows full history or only the last 24 hours.</summary>
    [HttpPut("full-history")]
    [RemoteCommand(Invalidates = ["GetShareLink"])]
    [ProducesResponseType(typeof(ShareLinkDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<ShareLinkDto>> SetShareLinkFullHistory(
        [FromBody] SetShareFullHistoryRequest request, CancellationToken ct)
    {
        if (!HttpContext.HasScope(Scope.SharingManage))
            return Forbid();

        return Ok(await _shareLinkService.SetFullHistoryAsync(_tenantAccessor.TenantId, request.FullHistory, ct));
    }

    /// <summary>
    /// Set which data categories anonymous viewers can see. Scopes must be read-permission atoms
    /// drawn from <c>Scope.PublicShareScopes</c>; an empty list keeps the link live but
    /// shares nothing.
    /// </summary>
    [HttpPut("scopes")]
    [RemoteCommand(Invalidates = ["GetShareLink"])]
    [ProducesResponseType(typeof(ShareLinkDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<ShareLinkDto>> SetShareLinkScopes(
        [FromBody] SetShareScopesRequest request, CancellationToken ct)
    {
        if (!HttpContext.HasScope(Scope.SharingManage))
            return Forbid();

        try
        {
            return Ok(await _shareLinkService.SetScopesAsync(_tenantAccessor.TenantId, request.Scopes, ct));
        }
        catch (ArgumentException)
        {
            return Problem(
                detail: "Some of the chosen sharing options aren't available. Reload the page and choose again.",
                statusCode: 400);
        }
    }

}

public record SetShareFullHistoryRequest(bool FullHistory);

public record SetShareScopesRequest(List<string> Scopes);
