using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpenApi.Remote.Attributes;
using Nocturne.API.Models.Responses;
using Nocturne.API.Services.Auth;
using Nocturne.Core.Contracts.Multitenancy;
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

    public ShareLinkController(IShareLinkService shareLinkService, ITenantAccessor tenantAccessor)
    {
        _shareLinkService = shareLinkService;
        _tenantAccessor = tenantAccessor;
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
