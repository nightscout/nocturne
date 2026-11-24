using Nocturne.API.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Attributes;
using Nocturne.Core.Models.Identity;
using OpenApi.Remote.Attributes;
using Nocturne.API.Models.Responses;
using Nocturne.API.Services.Auth;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Nocturne.API.Extensions;

namespace Nocturne.API.Controllers.V4.Identity;

/// <summary>
/// The tenant's public share link. A caller can read the link state, reveal the link, rotate the
/// link, turn sharing off, choose full history or the last 24 hours, and set the scopes the link
/// shares. All operations require the <c>sharing.manage</c> permission.
/// </summary>
/// <remarks>
/// The link is served at {token}.share.{baseDomain}.
/// </remarks>
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
    [RequireScope(Scope.SharingManage)]
    [RemoteQuery]
    [ProducesResponseType(typeof(ShareLinkDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<ShareLinkDto>> GetShareLink(CancellationToken ct)
    {
        var link = await _shareLinkService.GetAsync(_tenantAccessor.TenantId, ct);
        link.CallerAccessEndsAt = HttpContext.CallerGrantCeiling()?.AccessEndsAt;
        return Ok(link);
    }

    /// <summary>
    /// Show the live link in the clear. A command rather than a query so the secret travels only
    /// when the owner asks for it, instead of on every read of the sharing settings. Succeeds
    /// with a null URL and <c>canReveal</c> false in the cases
    /// <see cref="TenantRecord.ShareTokenEncrypted"/> lists.
    /// </summary>
    [HttpPost("reveal")]
    [RequireInteractiveSession]
    [RequireScope(Scope.SharingManage)]
    [RemoteCommand]
    [ProducesResponseType(typeof(ShareLinkDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<ShareLinkDto>> RevealShareLink(CancellationToken ct)
    {
        if (await HttpContext.IsCallerInactiveAsync(ct))
            return Challenge();

        if (HttpContext.CallerGrantCeiling() is null)
            return this.CallerAccessFromTeamProblem();

        if (await WouldHandOutFullHistoryAsync(ct))
            return Problem(detail: HttpContextExtensions.HistoryCeilingDetail, statusCode: 403);

        var link = await _shareLinkService.RevealAsync(_tenantAccessor.TenantId, ct);

        // Logged on the outcome, not the request: a reveal that produced nothing gave nothing away.
        // Why it is audited at all: <see cref="AuthAuditEventType.ShareLinkRevealed"/>.
        if (link.Url != null)
        {
            await _auditService.LogAsync(
                AuthAuditEventType.ShareLinkRevealed,
                HttpContext.GetSubjectId(),
                success: true,
                ipAddress: HttpContext.GetClientAddress(),
                userAgent: Request.Headers.UserAgent.ToString());
        }

        return Ok(link);
    }

    /// <summary>
    /// Mint a new share token — enables sharing, or rotates an existing link. The previous link
    /// stops working immediately.
    /// </summary>
    [HttpPost("rotate")]
    [RequireInteractiveSession]
    [RequireScope(Scope.SharingManage)]
    [RemoteCommand(Invalidates = ["GetShareLink"])]
    [ProducesResponseType(typeof(ShareLinkDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<ShareLinkDto>> RotateShareLink(CancellationToken ct)
    {
        if (await HttpContext.IsCallerInactiveAsync(ct))
            return Challenge();

        if (this.PublicSharingEndDateRefusal() is { } dated)
            return dated;

        if (await WouldHandOutFullHistoryAsync(ct))
            return Problem(detail: HttpContextExtensions.HistoryCeilingDetail, statusCode: 403);

        return Ok(await _shareLinkService.RotateAsync(_tenantAccessor.TenantId, ct));
    }

    /// <summary>Disable public sharing and invalidate the current link.</summary>
    [HttpDelete]
    [RequireScope(Scope.SharingManage)]
    [RemoteCommand(Invalidates = ["GetShareLink"])]
    [ProducesResponseType(typeof(ShareLinkDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<ShareLinkDto>> DisableShareLink(CancellationToken ct)
    {
        if (this.PublicSharingEndDateRefusal() is { } dated)
            return dated;

        return Ok(await _shareLinkService.DisableAsync(_tenantAccessor.TenantId, ct));
    }

    /// <summary>Choose whether the public view shows full history or only the last 24 hours.</summary>
    [HttpPut("full-history")]
    [RequireInteractiveSession]
    [RequireScope(Scope.SharingManage)]
    [RemoteCommand(Invalidates = ["GetShareLink"])]
    [ProducesResponseType(typeof(ShareLinkDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<ShareLinkDto>> SetShareLinkFullHistory(
        [FromBody] SetShareFullHistoryRequest request, CancellationToken ct)
    {
        if (await HttpContext.IsCallerInactiveAsync(ct))
            return Challenge();

        if (this.PublicSharingEndDateRefusal() is { } dated)
            return dated;

        if (request.FullHistory && HttpContext.CallerGrantCeiling() is not { HistoryClamped: false })
            return Problem(detail: HttpContextExtensions.HistoryCeilingDetail, statusCode: 403);

        return Ok(await _shareLinkService.SetFullHistoryAsync(_tenantAccessor.TenantId, request.FullHistory, ct));
    }

    /// <summary>
    /// Set which data categories anonymous viewers can see. Scopes must be read-permission atoms
    /// drawn from <c>Scope.PublicShareScopes</c>; an empty list keeps the link live but
    /// shares nothing.
    /// </summary>
    [HttpPut("scopes")]
    [RequireInteractiveSession]
    [RequireScope(Scope.SharingManage)]
    [RemoteCommand(Invalidates = ["GetShareLink"])]
    [ProducesResponseType(typeof(ShareLinkDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<ShareLinkDto>> SetShareLinkScopes(
        [FromBody] SetShareScopesRequest request, CancellationToken ct)
    {
        if (await HttpContext.IsCallerInactiveAsync(ct))
            return Challenge();

        if (this.PublicSharingEndDateRefusal() is { } dated)
            return dated;

        // The refusal above answers a caller who may give nothing. A member may publish only what
        // their own access reads, not what a team gives them.
        var ceiling = HttpContext.CallerGrantCeiling()!;
        if (Scope.ValidateDelegation(request.Scopes, ceiling.Scopes) is { } violation)
            return Problem(detail: violation.Description, statusCode: 403, title: violation.Code);

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

    /// <summary>
    /// True when a clamped caller would come away holding a live link to a full-history share:
    /// revealing it, or rotating it, which returns the new token. See
    /// <see cref="HttpContextExtensions.CallerGrantCeiling"/>.
    /// </summary>
    private async Task<bool> WouldHandOutFullHistoryAsync(CancellationToken ct) =>
        HttpContext.CallerGrantCeiling() is not { HistoryClamped: false }
        && (await _shareLinkService.GetAsync(_tenantAccessor.TenantId, ct)).FullHistory;

}

public record SetShareFullHistoryRequest(bool FullHistory);

public record SetShareScopesRequest(List<string> Scopes);
